// Builds docs/index.json by walking the kogama-maps-kgmexporter collection
// on archive.org. For each .kgmap it range-fetches just the first ~16 KB,
// gunzips the head, and parses the embedded JSON metadata block.
//
// Usage: dotnet run --project tools/Indexer.csproj -- <outPath>
//   <outPath> defaults to docs/index.json relative to repo root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

const string Identifier = "kogama-maps-kgmexporter";
const int HeadFetchBytes = 16 * 1024;
const int Concurrency = 16;

string outPath = args.Length > 0
    ? args[0]
    : Path.Combine(FindRepoRoot(), "docs", "index.json");

Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

using var http = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(30),
};
http.DefaultRequestHeaders.UserAgent.ParseAdd("kgmexporter-indexer/1.0 (+https://github.com/imuarte/kgmexporter)");

Console.Error.WriteLine($"Fetching collection metadata for {Identifier}...");
var meta = await GetJsonAsync(http, $"https://archive.org/metadata/{Identifier}");
if (meta.ValueKind != JsonValueKind.Object || !meta.TryGetProperty("files", out var filesEl))
{
    Console.Error.WriteLine("No files[] in collection metadata.");
    return 1;
}

var kgmapFiles = new List<(string Name, long Size, string? Mtime)>();
foreach (var f in filesEl.EnumerateArray())
{
    if (!f.TryGetProperty("name", out var nameEl)) continue;
    string name = nameEl.GetString() ?? "";
    if (!name.EndsWith(".kgmap", StringComparison.OrdinalIgnoreCase)) continue;

    long size = 0;
    if (f.TryGetProperty("size", out var sizeEl))
        long.TryParse(sizeEl.GetString(), out size);

    string? mtime = f.TryGetProperty("mtime", out var mtimeEl) ? mtimeEl.GetString() : null;

    kgmapFiles.Add((name, size, mtime));
}

Console.Error.WriteLine($"Found {kgmapFiles.Count} .kgmap files. Pulling metadata...");

var results = new ConcurrentBag<MapEntry>();
int done = 0;
int failed = 0;
var gate = new SemaphoreSlim(Concurrency, Concurrency);
var tasks = new List<Task>(kgmapFiles.Count);

foreach (var f in kgmapFiles)
{
    await gate.WaitAsync();
    tasks.Add(Task.Run(async () =>
    {
        try
        {
            var entry = await ProcessAsync(http, f.Name, f.Size, f.Mtime);
            if (entry != null) results.Add(entry);
            else Interlocked.Increment(ref failed);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref failed);
            Console.Error.WriteLine($"  ! {f.Name}: {ex.Message}");
        }
        finally
        {
            gate.Release();
            int n = Interlocked.Increment(ref done);
            if (n % 50 == 0) Console.Error.WriteLine($"  {n}/{kgmapFiles.Count}...");
        }
    }));
}
await Task.WhenAll(tasks);

Console.Error.WriteLine($"Indexed {results.Count} maps ({failed} failed).");

var sorted = new List<MapEntry>(results);
sorted.Sort((a, b) => string.Compare(b.Mtime ?? "", a.Mtime ?? "", StringComparison.Ordinal));

var output = new
{
    generatedAt = DateTime.UtcNow.ToString("O"),
    collection = Identifier,
    count = sorted.Count,
    maps = sorted,
};

await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(output, new JsonSerializerOptions
{
    WriteIndented = false,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
}));
Console.Error.WriteLine($"Wrote {outPath}");
return 0;


async Task<MapEntry?> ProcessAsync(HttpClient http, string name, long size, string? mtime)
{
    var url = $"https://archive.org/download/{Identifier}/{Uri.EscapeDataString(name)}";
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.Range = new RangeHeaderValue(0, HeadFetchBytes - 1);

    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
        return null;

    using var raw = await resp.Content.ReadAsStreamAsync();
    using var gz = new GZipStream(raw, CompressionMode.Decompress);
    using var br = new BinaryReader(gz);

    uint magic = br.ReadUInt32();
    if (magic != 0x504D474B) return new MapEntry(name, size, mtime, null, null, 0, null, null, null);

    ushort version = br.ReadUInt16();
    KgmapMetadata? meta = null;
    if (version >= 6)
    {
        int metaLen = br.ReadInt32();
        if (metaLen > 0 && metaLen < 1_000_000)
        {
            byte[] metaBytes = br.ReadBytes(metaLen);
            try { meta = JsonSerializer.Deserialize<KgmapMetadata>(metaBytes, Opts.MetaOpts); } catch { }
        }
    }

    return new MapEntry(
        Name: name,
        Size: size,
        Mtime: mtime,
        GameId: meta?.gameId,
        GameTitle: meta?.gameTitle,
        OwnerProfileId: meta?.ownerProfileId ?? 0,
        OwnerUsername: meta?.ownerUsername,
        SavedAt: meta?.savedAt,
        Region: meta?.region);
}

static async Task<JsonElement> GetJsonAsync(HttpClient http, string url)
{
    using var resp = await http.GetAsync(url);
    resp.EnsureSuccessStatusCode();
    string body = await resp.Content.ReadAsStringAsync();
    return JsonDocument.Parse(body).RootElement.Clone();
}

static string FindRepoRoot()
{
    string dir = Directory.GetCurrentDirectory();
    while (!string.IsNullOrEmpty(dir))
    {
        if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
        var parent = Directory.GetParent(dir);
        if (parent == null) break;
        dir = parent.FullName;
    }
    return Directory.GetCurrentDirectory();
}


internal sealed record MapEntry(
    string Name,
    long Size,
    string? Mtime,
    string? GameId,
    string? GameTitle,
    int OwnerProfileId,
    string? OwnerUsername,
    string? SavedAt,
    string? Region);

internal sealed record KgmapMetadata(
    string? gameId,
    string? gameTitle,
    int ownerProfileId,
    string? ownerUsername,
    string? savedAt,
    string? region,
    string? kgmexporter);

internal static class Opts
{
    public static readonly JsonSerializerOptions MetaOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
