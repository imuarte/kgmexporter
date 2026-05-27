using System.IO.Compression;
using System.Text;
using System.Text.Json;
using KogamaScripts;

namespace KgmExporter;

// .kgmap layout (gzip-compressed):
//   magic       "KGMP" (uint32)
//   version     ushort
//   [v6+ only] metaLen (int32) + metaJson (UTF-8 bytes) ← optional metadata block
//   batchCount  (int32)
//   per batch:
//     length (int32)
//     bytes  (raw N bytes - the exact payload of one GetGameBatch /
//             GameSnapshotData event, as the server sent it)
//
// Nothing in the batch stream is filtered or interpreted. Every byte the
// server delivered as world data lands in the file in order.
//
// Version history (do not touch v5 layout; only add new versions):
//   v5 - magic + version + batchCount + batches (no metadata).
//   v6 - same as v5, but inserts a length-prefixed UTF-8 JSON metadata block
//        between version and batchCount. Old v5 readers cannot parse v6,
//        but KgmapToObj accepts both versions. Metadata fields (all optional,
//        any missing key reads back as null/0/""):
//          gameId         string  kogama game / project id
//          gameTitle      string  title at the time of save
//          ownerProfileId int     owner's profile id (0 if unknown)
//          ownerUsername  string  owner's username (empty if unknown)
//          savedAt        string  ISO-8601 UTC save timestamp
//          region         string  "www" | "br" | "friends"
//          kgmexporter    string  exporter version label
static class KgmapExport
{
    private const uint Magic = 0x504D474B; // "KGMP"
    private const ushort Version = 6;
    private const string ExporterTag = "kgmexporter/v6";

    public static int WriteWorld(Stream output, WorldSession ws, KgmapMetadata? metadata = null)
        => WriteBatches(output, ws.SnapshotBatches(), metadata);

    public static int WriteBatches(Stream output, IReadOnlyList<byte[]> batches, KgmapMetadata? metadata = null)
    {
        using var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
        using var writer = new BinaryWriter(gz, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(Version);

        var meta = (metadata ?? new KgmapMetadata()) with
        {
            SavedAt = metadata?.SavedAt ?? DateTime.UtcNow.ToString("O"),
            Kgmexporter = metadata?.Kgmexporter ?? ExporterTag,
        };
        byte[] metaBytes = JsonSerializer.SerializeToUtf8Bytes(meta, MetadataJsonOptions);
        writer.Write(metaBytes.Length);
        writer.Write(metaBytes);

        writer.Write(batches.Count);

        foreach (var bytes in batches)
        {
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        return batches.Count;
    }

    internal static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record KgmapMetadata(
    string? GameId = null,
    string? GameTitle = null,
    int OwnerProfileId = 0,
    string? OwnerUsername = null,
    string? SavedAt = null,
    string? Region = null,
    string? Kgmexporter = null);
