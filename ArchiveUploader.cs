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

    // Built-in fallback S3 credentials. Paste real values here before shipping.
    // Users can override these in Advanced options if our key gets banned.
    private const string DefaultS3AccessKey = "urCMydfdy7SKw4PQ";
    private const string DefaultS3SecretKey = "XhXsv5EH6iQaRebc";

    private const string PlaceholderAccessKey = "__PASTE_ACCESS_KEY__";

    // archive.org IAS3 is HTTP/1.1; forcing HTTP/2 has been seen to cause
    // PUT failures, so we stay on 1.1.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxConnectionsPerServer  = 128,
        AutomaticDecompression   = DecompressionMethods.All,
    })
    {
        DefaultRequestVersion = HttpVersion.Version11,
        Timeout = TimeSpan.FromMinutes(20),
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

    // 32 concurrent uploads. archive.org IAS3 handles this comfortably per item.
    private static readonly SemaphoreSlim UploadGate = new(32, 32);

    private static readonly TimeSpan[] RetryBackoff = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) };

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
            var (access, secret) = ResolveS3Credentials(LocalSettings.Load());
            if (string.IsNullOrWhiteSpace(access) || access == PlaceholderAccessKey)
                return ArchiveUploadResult.Failed("archive.org S3 access key is not configured (set it in Advanced options)");

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
            string authHeader = $"LOW {access}:{secret}";

            string? transientError = null;
            int maxAttempts = 1 + RetryBackoff.Length;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (attempt > 1)
                    onStatus?.Invoke($"Retrying archive.org upload (attempt {attempt}/{maxAttempts})...");

                try
                {
                    using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 * 1024 * 1024, useAsync: true);
                    using var content = new StreamContent(stream);
                    content.Headers.ContentLength = stream.Length;
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                    using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
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

                    if (IsTransient(code) && attempt < maxAttempts)
                    {
                        transientError = error;
                        onStatus?.Invoke($"archive.org upload transient {code}, will retry...");
                        await Task.Delay(RetryBackoff[attempt - 1], ct);
                        continue;
                    }
                    return ArchiveUploadResult.Failed(error);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Client timeout (not user-cancel). Retry if we have attempts left.
                    transientError = "client timed out talking to archive.org";
                    if (attempt < maxAttempts)
                    {
                        onStatus?.Invoke("archive.org upload timed out, will retry...");
                        await Task.Delay(RetryBackoff[attempt - 1], ct);
                        continue;
                    }
                    return ArchiveUploadResult.Failed(transientError);
                }
                catch (HttpRequestException ex)
                {
                    transientError = ex.Message;
                    if (attempt < maxAttempts)
                    {
                        onStatus?.Invoke($"archive.org upload network error, will retry: {ex.Message}");
                        await Task.Delay(RetryBackoff[attempt - 1], ct);
                        continue;
                    }
                    return ArchiveUploadResult.Failed(ex.Message);
                }
            }

            return ArchiveUploadResult.Failed(transientError ?? "upload failed");
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

    private static (string access, string secret) ResolveS3Credentials(AppSettings? settings)
    {
        string access = !string.IsNullOrWhiteSpace(settings?.S3AccessKey) ? settings!.S3AccessKey!.Trim() : DefaultS3AccessKey;
        string secret = !string.IsNullOrWhiteSpace(settings?.S3SecretKey) ? settings!.S3SecretKey!.Trim() : DefaultS3SecretKey;
        return (access, secret);
    }

    private static bool IsTransient(int httpStatus) =>
        httpStatus == 408 || httpStatus == 429 ||
        httpStatus == 500 || httpStatus == 502 ||
        httpStatus == 503 || httpStatus == 504;
}
