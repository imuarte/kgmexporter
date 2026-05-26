using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace KgmExporter;

// Converts .kgmap to binary glTF (.glb). One mesh per prototype, one node per
// instance referencing the mesh - lets DCC tools share geometry across clones.
static class KgmapToGlb
{
    private const uint Magic = 0x504D474B; // "KGMP"
    private const ushort MinVersion = 1;
    private const ushort MaxVersion = 2;
    private const int AtlasColumns = 16;
    private const int AtlasRows = 5;

    private readonly record struct Vert(float X, float Y, float Z, float U, float V, float AtlasU, float AtlasV);

    private sealed class ProtoMesh
    {
        public int PrototypeId;
        public float Scale;
        public Vert[] Verts = Array.Empty<Vert>();
        public int MeshIndex;
    }

    private sealed class Instance
    {
        public int WoId;
        public int ParentWoId;
        public int PrototypeId;
        public int TypeId; // WorldObjectType. 0 when reading v1 files.
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
    }

    // CubeModel-like types skip their own scale (prototype already bakes size).
    // 1 = CubeModel, 8 = CubeModelPrototypeTerrain, 32 = CubeModelTerrainFineGrained.
    private static bool IsCubeModelLike(int typeId)
        => typeId == 1 || typeId == 8 || typeId == 32;

    public static int Convert(string kgmapPath, string glbPath)
    {
        var (protos, instances) = ReadKgmap(kgmapPath);
        return WriteGlb(glbPath, protos, instances);
    }

    private static (List<ProtoMesh>, List<Instance>) ReadKgmap(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new BinaryReader(gz, Encoding.UTF8);

        uint magic = reader.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException("Not a .kgmap file.");
        ushort version = reader.ReadUInt16();
        if (version < MinVersion || version > MaxVersion)
            throw new InvalidDataException($"Unsupported .kgmap version {version}.");

        int protoCount = reader.ReadInt32();
        var protos = new List<ProtoMesh>(protoCount);
        for (int p = 0; p < protoCount; p++)
        {
            int protoId = reader.ReadInt32();
            float scale = reader.ReadSingle();
            int vc = reader.ReadInt32();
            var verts = new Vert[vc];
            for (int i = 0; i < vc; i++)
            {
                float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle();
                float u = reader.ReadSingle(), v = reader.ReadSingle();
                float au = reader.ReadSingle(), av = reader.ReadSingle();
                reader.ReadByte(); reader.ReadByte(); reader.ReadByte(); reader.ReadByte();
                verts[i] = new Vert(x, y, z, u, v, au, av);
            }
            protos.Add(new ProtoMesh { PrototypeId = protoId, Scale = scale, Verts = verts });
        }

        int instCount = reader.ReadInt32();
        var instances = new List<Instance>(instCount);
        for (int i = 0; i < instCount; i++)
        {
            var inst = new Instance
            {
                WoId = reader.ReadInt32(),
                ParentWoId = reader.ReadInt32(),
                PrototypeId = reader.ReadInt32(),
                TypeId = version >= 2 ? reader.ReadInt32() : 0,
            };
            inst.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            inst.Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            inst.Scale = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            instances.Add(inst);
        }

        return (protos, instances);
    }

    private static int WriteGlb(string glbPath, List<ProtoMesh> protos, List<Instance> instances)
    {

        var bin = new MemoryStream();
        var bufferViews = new List<object>();
        var accessors = new List<object>();
        var meshes = new List<object>();

        float tileW = 1f / AtlasColumns;
        float tileH = 1f / AtlasRows;
        static float WrapUnit(float v) { float w = v - MathF.Floor(v); return w < 0 ? w + 1 : w; }

        for (int p = 0; p < protos.Count; p++)
        {
            var proto = protos[p];
            int vc = proto.Verts.Length;
            if (vc == 0)
            {
                proto.MeshIndex = -1;
                continue;
            }

            int posOffset = (int)bin.Position;
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
            using (var w = new BinaryWriter(bin, Encoding.UTF8, leaveOpen: true))
            {
                for (int i = 0; i < vc; i++)
                {
                    var v = proto.Verts[i];
                    float x = v.X * proto.Scale, y = v.Y * proto.Scale, z = v.Z * proto.Scale;
                    w.Write(x); w.Write(y); w.Write(z);
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                }
            }
            int posLength = vc * 12;
            PadTo4(bin);

            int uvOffset = (int)bin.Position;
            using (var w = new BinaryWriter(bin, Encoding.UTF8, leaveOpen: true))
            {
                for (int i = 0; i < vc; i++)
                {
                    var v = proto.Verts[i];
                    float u = v.AtlasU + WrapUnit(v.U) * tileW;
                    float uv = v.AtlasV + WrapUnit(v.V) * tileH;
                    w.Write(u);
                    w.Write(uv);
                }
            }
            int uvLength = vc * 8;
            PadTo4(bin);

            int idxOffset = (int)bin.Position;
            using (var w = new BinaryWriter(bin, Encoding.UTF8, leaveOpen: true))
            {
                for (int i = 0; i < vc; i++)
                    w.Write((uint)i);
            }
            int idxLength = vc * 4;
            PadTo4(bin);

            int posViewIdx = bufferViews.Count;
            bufferViews.Add(new Dictionary<string, object> {
                ["buffer"] = 0, ["byteOffset"] = posOffset, ["byteLength"] = posLength, ["target"] = 34962
            });
            int uvViewIdx = bufferViews.Count;
            bufferViews.Add(new Dictionary<string, object> {
                ["buffer"] = 0, ["byteOffset"] = uvOffset, ["byteLength"] = uvLength, ["target"] = 34962
            });
            int idxViewIdx = bufferViews.Count;
            bufferViews.Add(new Dictionary<string, object> {
                ["buffer"] = 0, ["byteOffset"] = idxOffset, ["byteLength"] = idxLength, ["target"] = 34963
            });

            int posAccIdx = accessors.Count;
            accessors.Add(new Dictionary<string, object> {
                ["bufferView"] = posViewIdx, ["componentType"] = 5126, ["count"] = vc, ["type"] = "VEC3",
                ["min"] = new[] { minX, minY, minZ }, ["max"] = new[] { maxX, maxY, maxZ }
            });
            int uvAccIdx = accessors.Count;
            accessors.Add(new Dictionary<string, object> {
                ["bufferView"] = uvViewIdx, ["componentType"] = 5126, ["count"] = vc, ["type"] = "VEC2"
            });
            int idxAccIdx = accessors.Count;
            accessors.Add(new Dictionary<string, object> {
                ["bufferView"] = idxViewIdx, ["componentType"] = 5125, ["count"] = vc, ["type"] = "SCALAR"
            });

            proto.MeshIndex = meshes.Count;
            meshes.Add(new Dictionary<string, object> {
                ["name"] = $"proto_{proto.PrototypeId}",
                ["primitives"] = new[] {
                    new Dictionary<string, object> {
                        ["attributes"] = new Dictionary<string, object> {
                            ["POSITION"] = posAccIdx,
                            ["TEXCOORD_0"] = uvAccIdx,
                        },
                        ["indices"] = idxAccIdx,
                        ["material"] = 0,
                    }
                }
            });
        }

        int atlasBufferViewIdx = bufferViews.Count;
        int atlasOffset = (int)bin.Position;
        byte[] atlasBytes = LoadAtlasBytes();
        bin.Write(atlasBytes, 0, atlasBytes.Length);
        bufferViews.Add(new Dictionary<string, object> {
            ["buffer"] = 0, ["byteOffset"] = atlasOffset, ["byteLength"] = atlasBytes.Length
        });
        PadTo4(bin);

        // Flatten parent chain like kgmview's MeshExport, then post-multiply by
        // zFlip to convert Kogama left-handed Y-up to glTF right-handed Y-up.
        Matrix4x4 zFlip = new(
            1, 0,  0, 0,
            0, 1,  0, 0,
            0, 0, -1, 0,
            0, 0,  0, 1);

        var protoById = protos.ToDictionary(p => p.PrototypeId);
        var instanceById = instances.ToDictionary(i => i.WoId);
        var worldMatrixCache = new Dictionary<int, Matrix4x4>();

        var nodes = new List<object>();
        var sceneNodes = new List<int>();
        int written = 0;

        foreach (var inst in instances)
        {
            if (!protoById.TryGetValue(inst.PrototypeId, out var proto) || proto.MeshIndex < 0)
                continue;

            bool useOwnScale = !IsCubeModelLike(inst.TypeId);
            Matrix4x4 world = GetWorldMatrix(inst, instanceById, worldMatrixCache, new HashSet<int>(), useOwnScale);
            Matrix4x4 flipped = world * zFlip;

            if (!Matrix4x4.Decompose(flipped, out Vector3 scale, out Quaternion rot, out Vector3 trans))
                continue;

            sceneNodes.Add(nodes.Count);
            nodes.Add(new Dictionary<string, object>
            {
                ["name"] = $"obj_{inst.WoId}",
                ["mesh"] = proto.MeshIndex,
                ["translation"] = new[] { trans.X, trans.Y, trans.Z },
                ["rotation"] = new[] { rot.X, rot.Y, rot.Z, rot.W },
                ["scale"] = new[] { scale.X, scale.Y, scale.Z },
            });
            written++;
        }

        var gltf = new Dictionary<string, object>
        {
            ["asset"] = new Dictionary<string, object> { ["version"] = "2.0", ["generator"] = "kgmexporter" },
            ["scene"] = 0,
            ["scenes"] = new[] { new Dictionary<string, object> { ["nodes"] = sceneNodes } },
            ["nodes"] = nodes,
            ["meshes"] = meshes,
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new[] { new Dictionary<string, object> { ["byteLength"] = (int)bin.Length } },
            ["images"] = new[] { new Dictionary<string, object> { ["bufferView"] = atlasBufferViewIdx, ["mimeType"] = "image/png" } },
            ["samplers"] = new[] { new Dictionary<string, object> {
                ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = 33071, ["wrapT"] = 33071
            } },
            ["textures"] = new[] { new Dictionary<string, object> { ["sampler"] = 0, ["source"] = 0 } },
            ["extensionsUsed"] = new[] { "KHR_materials_unlit" },
            ["materials"] = new[] { new Dictionary<string, object> {
                ["name"] = "kogama_atlas",
                ["pbrMetallicRoughness"] = new Dictionary<string, object> {
                    ["baseColorTexture"] = new Dictionary<string, object> { ["index"] = 0 },
                    ["baseColorFactor"] = new[] { 0.7f, 0.7f, 0.7f, 1f },
                    ["metallicFactor"] = 0.0f,
                    ["roughnessFactor"] = 1.0f,
                },
                ["extensions"] = new Dictionary<string, object> {
                    ["KHR_materials_unlit"] = new Dictionary<string, object>()
                },
                // Z-flip inverts triangle winding; doubleSided avoids culling.
                ["doubleSided"] = true,
            } },
        };

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(gltf);
        int jsonPad = (4 - jsonBytes.Length % 4) % 4;
        int binPad = (4 - (int)bin.Length % 4) % 4;

        int totalLength = 12 + 8 + jsonBytes.Length + jsonPad + 8 + (int)bin.Length + binPad;

        using var fs = File.Create(glbPath);
        using var w2 = new BinaryWriter(fs);
        w2.Write((uint)0x46546C67); // "glTF"
        w2.Write((uint)2);
        w2.Write((uint)totalLength);

        w2.Write((uint)(jsonBytes.Length + jsonPad));
        w2.Write((uint)0x4E4F534A); // "JSON"
        w2.Write(jsonBytes);
        for (int i = 0; i < jsonPad; i++) w2.Write((byte)0x20);

        w2.Write((uint)(bin.Length + binPad));
        w2.Write((uint)0x004E4942); // "BIN\0"
        bin.Position = 0;
        bin.CopyTo(fs);
        for (int i = 0; i < binPad; i++) w2.Write((byte)0);

        return written;
    }

    private static Matrix4x4 GetWorldMatrix(
        Instance inst,
        IReadOnlyDictionary<int, Instance> byId,
        Dictionary<int, Matrix4x4> cache,
        HashSet<int> visiting,
        bool useOwnScale)
    {
        if (cache.TryGetValue(inst.WoId, out var cached) && useOwnScale)
            return cached;
        if (!visiting.Add(inst.WoId))
            return Matrix4x4.Identity;

        Quaternion rot = inst.Rotation.LengthSquared() <= 1e-8f
            ? Quaternion.Identity
            : Quaternion.Normalize(inst.Rotation);

        Vector3 ownScale = useOwnScale ? inst.Scale : Vector3.One;

        Matrix4x4 local =
            Matrix4x4.CreateScale(ownScale) *
            Matrix4x4.CreateFromQuaternion(rot) *
            Matrix4x4.CreateTranslation(inst.Position);

        Matrix4x4 world = local;
        if (byId.TryGetValue(inst.ParentWoId, out var parent))
        {
            // Parent always contributes full scale*rot*trans regardless of leaf type.
            world = local * GetWorldMatrix(parent, byId, cache, visiting, useOwnScale: true);
        }

        visiting.Remove(inst.WoId);
        if (useOwnScale)
            cache[inst.WoId] = world;
        return world;
    }

    private static byte[] LoadAtlasBytes()
    {
        var asm = Assembly.GetExecutingAssembly();
        const string resName = "KgmExporter.assets.atlas.png";
        using var s = asm.GetManifestResourceStream(resName)
            ?? throw new InvalidOperationException("Embedded atlas.png is missing.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static void PadTo4(MemoryStream s)
    {
        int pad = (4 - (int)s.Position % 4) % 4;
        for (int i = 0; i < pad; i++) s.WriteByte(0);
    }
}
