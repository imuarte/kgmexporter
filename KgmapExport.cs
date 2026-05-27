using System.IO.Compression;
using System.Text;
using KogamaScripts;

namespace KgmExporter;

// .kgmap layout (gzip-compressed) - raw, unparsed world dump:
//   magic   "KGMP" (uint32)
//   version 5      (uint16)
//   batchCount     (int32)
//   per batch:
//     length (int32)
//     bytes  (raw N bytes - the exact payload of one GetGameBatch /
//             GameSnapshotData event, as the server sent it)
//
// Nothing is filtered or interpreted. Every byte the server delivered as
// world data lands in the file in order. Re-parsing into cubes / scripts /
// terrain / whatever else lives outside this exporter.
static class KgmapExport
{
    private const uint Magic = 0x504D474B; // "KGMP"
    private const ushort Version = 5;

    public static int WriteWorld(Stream output, WorldSession ws)
        => WriteBatches(output, ws.SnapshotBatches());

    public static int WriteBatches(Stream output, IReadOnlyList<byte[]> batches)
    {
        using var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
        using var writer = new BinaryWriter(gz, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(batches.Count);

        foreach (var bytes in batches)
        {
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        return batches.Count;
    }
}
