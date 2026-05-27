using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace KgmExporter;

internal enum ArchiveUploadStatus
{
    Uploaded,
    AlreadyExists,
    Failed,
}

// Kept as a record so the existing call sites compile, but no per-user
// configuration is needed any more - uploads go through our public proxy.
internal sealed record ArchiveUploadOptions
{
    public string ItemUrl => $"https://archive.org/details/{ArchiveUploader.ItemIdentifier}";
}

internal sealed record ArchiveUploadResult(ArchiveUploadStatus Status, string? ItemUrl = null, string? Message = null)
{
    public static ArchiveUploadResult Uploaded(string itemUrl) => new(ArchiveUploadStatus.Uploaded, itemUrl);
    public static ArchiveUploadResult AlreadyExists(string itemUrl) => new(ArchiveUploadStatus.AlreadyExists, itemUrl);
    public static ArchiveUploadResult Failed(string message) => new(ArchiveUploadStatus.Failed, Message: message);
}

internal static class ArchiveUploader
{
    // Public archive.org proxy run by the kgmexporter project.
    // The proxy holds archive.org S3 keys server-side, so the desktop client
    // never needs to ship or prompt for them.
    private const string ProxyBaseUrl = "http://35.198.123.54:9080";
    private const string ProxyToken   = "99901074f9a314cefd7f17cb5b2ea544dbd2612ce8583ed1";
    internal const string ItemIdentifier = "kogama-maps-kgmexporter";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(20),
    };

    // Short-timeout client just for HEAD existence checks - we never want one
    // slow archive.org node to hang a per-game pre-flight in a batch loop.
    private static readonly HttpClient HeadCheckHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static readonly SemaphoreSlim UploadGate = new(2, 2);

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
        bool skipDuplicates = true)
    {
        string fileName = Path.GetFileName(filePath);
        if (UploadGate.CurrentCount == 0)
            onStatus?.Invoke("Waiting for archive.org upload slot...");

        await UploadGate.WaitAsync(ct);
        try
        {
            var fi = new FileInfo(filePath);
            onStatus?.Invoke($"Uploading to archive.org ({fi.Length / 1024.0:N1} KB)...");

            string force = skipDuplicates ? "" : "&force=1";
            var url = new Uri($"{ProxyBaseUrl}/upload?filename={Uri.EscapeDataString(fileName)}{force}");
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            using var content = new StreamContent(stream);
            content.Headers.ContentLength = stream.Length;
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.TryAddWithoutValidation("X-Auth", ProxyToken);

            using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                string snippet = body.Length > 300 ? body[..300] + "..." : body;
                return ArchiveUploadResult.Failed($"proxy {(int)response.StatusCode} {response.ReasonPhrase}: {snippet}");
            }

            string itemUrl = $"https://archive.org/details/{ItemIdentifier}";
            return body.Contains("\"already_exists\"")
                ? ArchiveUploadResult.AlreadyExists(itemUrl)
                : ArchiveUploadResult.Uploaded(itemUrl);
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
}
