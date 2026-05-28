using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace KgmExporter;

internal enum ArchiveUploadStatus
{
    Uploaded,
    AlreadyExists,
    Pending,
    Failed,
}

// Kept as a record so the existing call sites compile, but no per-user
// configuration is needed any more - uploads go straight to archive.org IAS3
// using credentials baked into the binary (overridable in Advanced options).
internal sealed record ArchiveUploadOptions
{
    public string ItemUrl => $"https://archive.org/details/{ArchiveUploader.ItemIdentifier}";
}

internal sealed record ArchiveUploadResult(ArchiveUploadStatus Status, string? ItemUrl = null, string? Message = null)
{
    public static ArchiveUploadResult Uploaded(string itemUrl) => new(ArchiveUploadStatus.Uploaded, itemUrl);
    public static ArchiveUploadResult AlreadyExists(string itemUrl) => new(ArchiveUploadStatus.AlreadyExists, itemUrl);
    public static ArchiveUploadResult Pending(string itemUrl, string? note = null) => new(ArchiveUploadStatus.Pending, itemUrl, note);
    public static ArchiveUploadResult Failed(string message) => new(ArchiveUploadStatus.Failed, Message: message);
}

internal static class ArchiveUploader
{
    internal const string ItemIdentifier = "kogama-maps-kgmexporter";

    // Built-in S3 credentials. Round-robined per upload; a key that gets a
    // 401/403 is banned for the session and skipped on subsequent picks.
    private static readonly (string access, string secret)[] DefaultKeys =
    {
        ("urCMydfdy7SKw4PQ", "XhXsv5EH6iQaRebc"),
        ("iNIS7eSqZrPQKqum", "BSVT1RybddC0HtfZ"),
        ("3xK4MSZhnHQKssru", "xC4JE2X5eMpjSAwz"),
        ("n9lSGN0chFELVrX0", "AWFcvF9cW5cNR707"),
    };

    private const string PlaceholderAccessKey = "__PASTE_ACCESS_KEY__";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> BannedKeys = new();

    // archive.org IAS3 is HTTP/1.1; forcing HTTP/2 has been seen to cause
    // PUT failures, so we stay on 1.1.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxConnectionsPerServer  = 256,
        AutomaticDecompression   = DecompressionMethods.All,
    })
    {
        DefaultRequestVersion = HttpVersion.Version11,
        Timeout = TimeSpan.FromSeconds(90),
    };

    // Short-timeout client just for HEAD existence checks - we never want one
    // slow archive.org node to hang a per-game pre-flight in a batch loop.
    private static readonly HttpClient HeadCheckHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxConnectionsPerServer  = 128,
        AutomaticDecompression   = DecompressionMethods.All,
    })
    {
        DefaultRequestVersion = HttpVersion.Version11,
        Timeout = TimeSpan.FromSeconds(5),
    };

    // Round-robin across keys distributes load.
    private static readonly SemaphoreSlim UploadGate = new(128, 128);
    private static int _keyCursor;

    private static TimeSpan RetryDelay(int attempt, int baseMs)
        => TimeSpan.FromMilliseconds(Math.Max(0, baseMs) * Math.Max(1, attempt));

    public static bool TryCreateOptions(AppSettings settings, out ArchiveUploadOptions? options, out string error)
    {
        options = new ArchiveUploadOptions();
        error = "";
        return true;
    }

    public static async Task<bool> RemoteFileExistsAsync(string fileName, CancellationToken ct)
    {
        var url = new Uri($"https://archive.org/download/{ItemIdentifier}/{Uri.EscapeDataString(fileName)}");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using HttpResponseMessage response = await HeadCheckHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Timeout or network error - treat as "unknown / not found" so the caller falls through to download.
            return false;
        }
    }

    public static async Task<ArchiveUploadResult> UploadKgmapAsync(
        string filePath,
        ArchiveUploadOptions options,
        KgmapMetadata? metadata,
        Action<string>? onStatus,
        CancellationToken ct,
        bool skipDuplicates = true,
        bool skipRemoteCheck = false)
    {
        string fileName = Path.GetFileName(filePath);
        if (UploadGate.CurrentCount == 0)
            onStatus?.Invoke("Waiting for archive.org upload slot...");

        await UploadGate.WaitAsync(ct);
        try
        {
            var settings = LocalSettings.Load();

            var fi = new FileInfo(filePath);
            onStatus?.Invoke($"Uploading to archive.org ({fi.Length / 1024.0:N1} KB)...");

            // Optional pre-flight: if the caller hasn't already done a HEAD check
            // (queue does), do it here so duplicates skip the PUT entirely.
            if (skipDuplicates && !skipRemoteCheck)
            {
                if (await RemoteFileExistsAsync(fileName, ct))
                    return ArchiveUploadResult.AlreadyExists($"https://archive.org/details/{ItemIdentifier}");
            }

            var url = new Uri($"https://s3.us.archive.org/{ItemIdentifier}/{Uri.EscapeDataString(fileName)}");
            string itemUrl = $"https://archive.org/details/{ItemIdentifier}";

            int retryDelayMs = UploadTuning.RetryDelay(settings);
            string lastKey = "";
            int sameKeyFails = 0;
            const int sameKeyFailLimit = 3;

            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var creds = PickS3Credentials(settings);
                if (creds is null)
                {
                    onStatus?.Invoke("all archive.org keys retired, cooling down 3s...");
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    BannedKeys.Clear();
                    continue;
                }
                var (access, secret) = creds.Value;
                if (access == PlaceholderAccessKey)
                {
                    onStatus?.Invoke("no S3 key configured, waiting...");
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    continue;
                }

                if (access != lastKey) sameKeyFails = 0;
                lastKey = access;

                string authHeader = $"LOW {access}:{secret}";

                if (attempt > 1)
                    onStatus?.Invoke($"Retrying archive.org upload (attempt {attempt})...");

                try
                {
                    using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024 * 1024, useAsync: true);
                    using var content = new StreamContent(stream);
                    content.Headers.ContentLength = stream.Length;
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                    using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
                    request.Headers.ExpectContinue = false;
                    request.Headers.TryAddWithoutValidation("Authorization", authHeader);
                    request.Headers.TryAddWithoutValidation("x-archive-queue-derive", "0");
                    request.Headers.TryAddWithoutValidation("x-archive-keep-old-version", "1");

                    using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                    if (response.IsSuccessStatusCode)
                        return ArchiveUploadResult.Uploaded(itemUrl);

                    string body = await response.Content.ReadAsStringAsync(ct);
                    int code = (int)response.StatusCode;
                    string snippet = body.Length > 300 ? body[..300] + "..." : body;
                    string error = $"archive.org {code} {response.ReasonPhrase}: {snippet}";

                    // Duplicate detection: archive.org returns 409 / error mentioning conflict
                    // when keep-old-version=0 isn't honored and the file exists.
                    if (skipDuplicates && (code == 409 || body.Contains("already exists", StringComparison.OrdinalIgnoreCase)))
                        return ArchiveUploadResult.AlreadyExists(itemUrl);

                    // Banned (401/403) or rate-limited (429): retire this key for
                    // the session and immediately retry with the next key.
                    if (code == 401 || code == 403 || code == 429)
                    {
                        BanKey(access);
                        string reason = code == 429 ? "rate-limited" : "banned";
                        onStatus?.Invoke($"archive.org key {access[..Math.Min(6, access.Length)]}... {reason}, switching to next");
                        continue;
                    }

                    // Any other error: count a strike against the current key and
                    // keep trying. After enough strikes the key is retired.
                    if (++sameKeyFails >= sameKeyFailLimit)
                    {
                        BanKey(access);
                        onStatus?.Invoke($"archive.org key {access[..Math.Min(6, access.Length)]}... keeps erroring ({code}), switching to next");
                        continue;
                    }

                    onStatus?.Invoke($"archive.org upload error {code}, will retry...");
                    await Task.Delay(RetryDelay(attempt, retryDelayMs), ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (++sameKeyFails >= sameKeyFailLimit)
                    {
                        BanKey(access);
                        onStatus?.Invoke($"archive.org key {access[..Math.Min(6, access.Length)]}... keeps timing out, switching to next");
                        continue;
                    }

                    onStatus?.Invoke("archive.org upload timed out, will retry...");
                    await Task.Delay(RetryDelay(attempt, retryDelayMs), ct);
                }
                catch (HttpRequestException ex)
                {
                    if (++sameKeyFails >= sameKeyFailLimit)
                    {
                        BanKey(access);
                        onStatus?.Invoke($"archive.org key {access[..Math.Min(6, access.Length)]}... keeps erroring, switching to next");
                        continue;
                    }

                    onStatus?.Invoke($"archive.org upload network error, will retry: {ex.Message}");
                    await Task.Delay(RetryDelay(attempt, retryDelayMs), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (++sameKeyFails >= sameKeyFailLimit)
                    {
                        BanKey(access);
                        onStatus?.Invoke($"archive.org key {access[..Math.Min(6, access.Length)]}... keeps erroring, switching to next");
                        continue;
                    }

                    onStatus?.Invoke($"archive.org upload error, will retry: {ex.Message}");
                    await Task.Delay(RetryDelay(attempt, retryDelayMs), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ArchiveUploadResult.Failed(ex.Message);
        }
        finally
        {
            UploadGate.Release();
        }
    }

    private static (string access, string secret)? PickS3Credentials(AppSettings? settings)
    {
        // User-provided override always wins and is used exclusively.
        if (!string.IsNullOrWhiteSpace(settings?.S3AccessKey) && !string.IsNullOrWhiteSpace(settings?.S3SecretKey))
            return (settings!.S3AccessKey!.Trim(), settings!.S3SecretKey!.Trim());

        // Round-robin across non-banned keys so 4 bots share the load.
        for (int i = 0; i < DefaultKeys.Length; i++)
        {
            int idx = (Interlocked.Increment(ref _keyCursor) - 1) % DefaultKeys.Length;
            if (idx < 0) idx += DefaultKeys.Length;
            var pair = DefaultKeys[idx];
            if (!BannedKeys.ContainsKey(pair.access))
                return pair;
        }
        return null;
    }

    private static void BanKey(string access) => BannedKeys.TryAdd(access, 0);

    private static bool IsTransient(int httpStatus) =>
        httpStatus == 408 || httpStatus == 429 ||
        httpStatus == 500 || httpStatus == 502 ||
        httpStatus == 503 || httpStatus == 504;
}
