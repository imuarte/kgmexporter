using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Text;
using KogamaScripts;

namespace KgmExporter;

// Reads the raw .kgmap (v5: batches-only; v6: optional JSON metadata block
// before the batches) and writes .obj + .mtl + atlas.png. Decoding the batches
// into prototypes/objects is delegated to KogamaScripts.WorldDumpLoader so
// kgmexporter never needs to know the on-the-wire layout - it just owns the
// mesh-building, face culling, UV layout, and material->atlas mapping.
static class KgmapToObj
{
    private const uint Magic = 0x504D474B; // "KGMP"
    private const ushort MinVersion = 5;
    private const ushort MaxVersion = 6;
    private const int AtlasColumns = 16;
    private const int AtlasRows = 5;
    private const bool CullInternalFaces = true;
    private const string AtlasFileName = "atlas.png";
    private const string MaterialName = "kogama_atlas";

    private static readonly byte[] IdentityByteCorners = { 20, 120, 124, 24, 4, 104, 100, 0 };
    private static readonly int[] GlowingMaterials = { 26, 28, 55 };

    private static bool IsCubeModelLike(int typeId)
        => typeId == 1 || typeId == 8 || typeId == 32;

    public static int Convert(string kgmapPath, string objPath)
    {
        var batches = ReadKgmapBatches(kgmapPath);
        var parsed = WorldDumpLoader.LoadBatches(batches);
        return WriteObj(objPath, parsed);
    }

    private static List<byte[]> ReadKgmapBatches(string path)
    {
        byte[] decompressed;
        using (var fs = File.OpenRead(path))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var ms = new MemoryStream())
        {
            gz.CopyTo(ms);
            decompressed = ms.ToArray();
        }
        using var reader = new BinaryReader(new MemoryStream(decompressed), Encoding.UTF8);

        uint magic = reader.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException("Not a .kgmap file.");
        ushort version = reader.ReadUInt16();
        if (version < MinVersion || version > MaxVersion)
            throw new InvalidDataException(
                $"Unsupported .kgmap version {version}. " +
                "Re-save the world with a current kgmexporter build.");

        if (version >= 6)
        {
            int metaLen = reader.ReadInt32();
            if (metaLen > 0) reader.ReadBytes(metaLen); // metadata is informational; skip during conversion
        }

        int batchCount = reader.ReadInt32();
        var batches = new List<byte[]>(batchCount);
        for (int i = 0; i < batchCount; i++)
        {
            int len = reader.ReadInt32();
            batches.Add(reader.ReadBytes(len));
        }
        return batches;
    }

    private static int WriteObj(string objPath, ParsedWorldData parsed)
    {
        var ci = CultureInfo.InvariantCulture;
        string dir = Path.GetDirectoryName(Path.GetFullPath(objPath)) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(objPath);
        string mtlPath = Path.Combine(dir, baseName + ".mtl");
        string atlasPath = Path.Combine(dir, AtlasFileName);

        CopyAtlasNextTo(atlasPath);
        WriteMtl(mtlPath);

        var protoMeshes = new Dictionary<int, List<(Vector3 P, Vector2 Uv)>>();
        foreach (var proto in parsed.Prototypes.Values)
        {
            if (proto.Cubes == null || proto.Cubes.Count == 0) continue;
            var verts = BuildPrototypeMesh(proto);
            if (verts.Count > 0)
                protoMeshes[proto.PrototypeId] = verts;
        }

        Matrix4x4 zFlip = new(
            1, 0,  0, 0,
            0, 1,  0, 0,
            0, 0, -1, 0,
            0, 0,  0, 1);

        var instances = parsed.Objects;
        var parentMatrixCache = new Dictionary<int, Matrix4x4>(instances.Count);
        var visiting = new HashSet<int>();

        using var fs = File.Create(objPath);
        using var writer = new StreamWriter(fs, new UTF8Encoding(false), bufferSize: 1 << 16);
        writer.WriteLine("# kgmexporter");
        writer.WriteLine($"mtllib {baseName}.mtl");
        writer.WriteLine($"usemtl {MaterialName}");

        int vBase = 1;
        int vtBase = 1;
        int written = 0;

        foreach (var inst in instances.Values)
        {
            if (!protoMeshes.TryGetValue(inst.PrototypeId, out var mesh))
                continue;

            bool useOwnScale = !IsCubeModelLike(inst.TypeId);
            Quaternion leafRot = inst.Rotation.LengthSquared() <= 1e-8f
                ? Quaternion.Identity
                : Quaternion.Normalize(inst.Rotation);
            Vector3 leafScale = useOwnScale ? inst.Scale : Vector3.One;
            Matrix4x4 leaf =
                Matrix4x4.CreateScale(leafScale) *
                Matrix4x4.CreateFromQuaternion(leafRot) *
                Matrix4x4.CreateTranslation(inst.Position);

            Matrix4x4 world = leaf;
            if (instances.TryGetValue(inst.ParentWoId, out var parent))
            {
                visiting.Clear();
                world = leaf * GetParentMatrix(parent, instances, parentMatrixCache, visiting);
            }
            world *= zFlip;

            writer.WriteLine($"o object_{inst.WoId}");

            foreach (var (p, _) in mesh)
            {
                Vector3 wp = Vector3.Transform(p, world);
                writer.Write("v ");
                writer.Write(wp.X.ToString("R", ci)); writer.Write(' ');
                writer.Write(wp.Y.ToString("R", ci)); writer.Write(' ');
                writer.WriteLine(wp.Z.ToString("R", ci));
            }
            foreach (var (_, uv) in mesh)
            {
                writer.Write("vt ");
                writer.Write(uv.X.ToString("R", ci)); writer.Write(' ');
                writer.WriteLine(uv.Y.ToString("R", ci));
            }

            for (int i = 0; i < mesh.Count; i += 3)
            {
                int a = vBase + i, b = vBase + i + 1, c = vBase + i + 2;
                int ta = vtBase + i, tb = vtBase + i + 1, tc = vtBase + i + 2;
                writer.WriteLine($"f {a}/{ta} {b}/{tb} {c}/{tc}");
            }

            vBase += mesh.Count;
            vtBase += mesh.Count;
            written++;
        }

        return written;
    }

    private static List<(Vector3 P, Vector2 Uv)> BuildPrototypeMesh(Prototype proto)
    {
        float protoScale = proto.Scale == 0 ? 1f : proto.Scale;
        float tileW = 1f / AtlasColumns;
        float tileH = 1f / AtlasRows;
        static float WrapUnit(float v) { float w = v - MathF.Floor(v); return w < 0 ? w + 1 : w; }

        var cubes = proto.Cubes!;
        var verts = new List<(Vector3 P, Vector2 Uv)>(cubes.Count * 12);
        foreach (var (grid, cube) in cubes)
        {
            foreach (var face in Faces)
            {
                var neighbor = new CubeGridPosition(
                    (short)(grid.X + face.Neighbor.X),
                    (short)(grid.Y + face.Neighbor.Y),
                    (short)(grid.Z + face.Neighbor.Z));

                if (CullInternalFaces &&
                    cubes.TryGetValue(neighbor, out var neighborCube) &&
                    FacesTouch(cube, face, neighborCube, Faces[(int)face.Opposite]))
                {
                    continue;
                }

                byte material = MaterialFor(cube, face.MaterialIndex);
                var atlasOrigin = MaterialToAtlasOrigin(material);
                EmitFace(verts, protoScale, grid, face, cube, atlasOrigin, tileW, tileH, WrapUnit);
            }
        }
        return verts;
    }

    private static byte MaterialFor(Cube cube, int faceIndex)
    {
        var mats = cube.FaceMaterials;
        if (mats.Length == 0) return 1;
        if (mats.Length == 1) return mats[0];
        if (faceIndex < mats.Length) return mats[faceIndex];
        return mats[0];
    }

    private static void EmitFace(
        List<(Vector3 P, Vector2 Uv)> dst,
        float protoScale,
        CubeGridPosition grid,
        Face face,
        Cube cube,
        (float AtlasU, float AtlasV) atlas,
        float tileW, float tileH,
        Func<float, float> wrap)
    {
        Vector3[] faceCorners = GetFaceCorners(cube, face);
        Span<Vector3> local = stackalloc Vector3[4];
        Span<Vector2> uv = stackalloc Vector2[4];
        float vBase = 1f - atlas.AtlasV - tileH;

        Span<Vector2> faceLocalUv = stackalloc Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            local[i] = new Vector3(
                grid.X + faceCorners[i].X,
                grid.Y + faceCorners[i].Y,
                grid.Z + faceCorners[i].Z);
            Vector3 corner = faceCorners[i];
            faceLocalUv[i] = face.Kind switch
            {
                CubeFace.Top => new Vector2(corner.X, corner.Z) + new Vector2(0.5f, 0.5f),
                CubeFace.Bottom => new Vector2(-corner.X, corner.Z) + new Vector2(0.5f, 0.5f),
                CubeFace.Front => new Vector2(corner.X, corner.Y) + new Vector2(0.5f, 0.5f),
                CubeFace.Back => new Vector2(-corner.X, corner.Y) + new Vector2(0.5f, 0.5f),
                CubeFace.Left => new Vector2(-corner.Z, corner.Y) + new Vector2(0.5f, 0.5f),
                CubeFace.Right => new Vector2(corner.Z, corner.Y) + new Vector2(0.5f, 0.5f),
                _ => Vector2.Zero
            };
            uv[i] = new Vector2(
                atlas.AtlasU + faceLocalUv[i].X * tileW,
                vBase + faceLocalUv[i].Y * tileH);
        }

        Vector3 a = local[0] * protoScale, b = local[1] * protoScale,
                c = local[2] * protoScale, d = local[3] * protoScale;

        dst.Add((a, uv[0])); dst.Add((b, uv[1])); dst.Add((c, uv[2]));
        dst.Add((a, uv[0])); dst.Add((c, uv[2])); dst.Add((d, uv[3]));
    }

    private static (float AtlasU, float AtlasV) MaterialToAtlasOrigin(byte material)
    {
        int compact = MaterialToCompactIndex(material);
        int col = compact % AtlasColumns;
        int rowFromTop = compact / AtlasColumns;
        return (col / (float)AtlasColumns, rowFromTop / (float)AtlasRows);
    }

    private static int MaterialToCompactIndex(int material)
    {
        if (material < 0 || material >= 63) material = 24;
        for (int i = 0; i < GlowingMaterials.Length; i++)
            if (GlowingMaterials[i] == material) return 60 + i;
        if (material < GlowingMaterials[0]) return material;
        if (material > GlowingMaterials[^1]) return material - GlowingMaterials.Length;
        int subtract = 0;
        for (int i = 0; i < GlowingMaterials.Length - 1 && GlowingMaterials[i] <= material; i++)
            subtract++;
        return material - subtract;
    }

    private static Vector3[] GetFaceCorners(Cube cube, Face face)
    {
        byte[] bytes = cube.ByteCorners.Length >= 8 ? cube.ByteCorners : IdentityByteCorners;
        var corners = new Vector3[4];
        for (int i = 0; i < corners.Length; i++)
            corners[i] = DecodeCorner(bytes[face.CornerIndices[i]]);
        return corners;
    }

    private static Vector3 DecodeCorner(byte key)
    {
        int value = Math.Min((int)key, 124);
        int x = value / 25;
        int y = value / 5 % 5;
        int z = value % 5;
        return new Vector3(-0.5f + x * 0.25f, -0.5f + y * 0.25f, -0.5f + z * 0.25f);
    }

    private static bool FacesTouch(Cube cube, Face face, Cube neighborCube, Face neighborFace)
    {
        Vector3[] faceCorners = GetFaceCorners(cube, face);
        if (!FaceTouchesBorder(faceCorners, face.BorderAxis, face.BorderValue))
            return false;
        Vector3[] neighborCorners = GetFaceCorners(neighborCube, neighborFace);
        if (!FaceTouchesBorder(neighborCorners, neighborFace.BorderAxis, neighborFace.BorderValue))
            return false;
        return FaceShapesMatch(faceCorners, neighborCorners, face.BorderAxis);
    }

    private static bool FaceTouchesBorder(Vector3[] corners, int axis, float border)
    {
        foreach (var corner in corners)
            if (!NearlyEqual(Component(corner, axis), border))
                return false;
        return true;
    }

    private static bool FaceShapesMatch(Vector3[] face, Vector3[] neighborFace, int borderAxis)
    {
        int axisA, axisB;
        int[] order;
        if (borderAxis == 1) { axisA = 0; axisB = 2; order = new[] { 3, 2, 1, 0 }; }
        else if (borderAxis == 0) { axisA = 2; axisB = 1; order = new[] { 1, 0, 3, 2 }; }
        else { axisA = 0; axisB = 1; order = new[] { 1, 0, 3, 2 }; }

        for (int i = 0; i < face.Length; i++)
        {
            Vector3 a = face[i];
            Vector3 b = neighborFace[order[i]];
            if (!NearlyEqual(Component(a, axisA), Component(b, axisA)) ||
                !NearlyEqual(Component(a, axisB), Component(b, axisB)))
                return false;
        }
        return true;
    }

    private static float Component(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
    private static bool NearlyEqual(float a, float b) => MathF.Abs(a - b) <= 1e-5f;

    private static Matrix4x4 GetParentMatrix(
        WorldObject inst,
        IReadOnlyDictionary<int, WorldObject> byId,
        Dictionary<int, Matrix4x4> cache,
        HashSet<int> visiting)
    {
        if (cache.TryGetValue(inst.WoId, out var cached))
            return cached;
        if (!visiting.Add(inst.WoId))
            return Matrix4x4.Identity;

        Quaternion rot = inst.Rotation.LengthSquared() <= 1e-8f
            ? Quaternion.Identity
            : Quaternion.Normalize(inst.Rotation);

        Matrix4x4 local =
            Matrix4x4.CreateScale(inst.Scale) *
            Matrix4x4.CreateFromQuaternion(rot) *
            Matrix4x4.CreateTranslation(inst.Position);

        Matrix4x4 world = local;
        if (byId.TryGetValue(inst.ParentWoId, out var parent))
            world = local * GetParentMatrix(parent, byId, cache, visiting);

        cache[inst.WoId] = world;
        return world;
    }

    private static void CopyAtlasNextTo(string atlasPath)
    {
        if (File.Exists(atlasPath))
            return;
        var asm = Assembly.GetExecutingAssembly();
        const string resName = "KgmExporter.assets.atlas.png";
        using var s = asm.GetManifestResourceStream(resName)
            ?? throw new InvalidOperationException("Embedded atlas.png is missing.");
        using var dst = File.Create(atlasPath);
        s.CopyTo(dst);
    }

    private static void WriteMtl(string mtlPath)
    {
        using var w = new StreamWriter(mtlPath);
        w.WriteLine($"newmtl {MaterialName}");
        w.WriteLine("Ka 1 1 1");
        w.WriteLine("Kd 1 1 1");
        w.WriteLine("Ks 0 0 0");
        w.WriteLine("Ke 1 1 1");
        w.WriteLine("d 1");
        w.WriteLine("illum 1");
        w.WriteLine($"map_Kd {AtlasFileName}");
        w.WriteLine($"map_Ke {AtlasFileName}");
    }

    private enum CubeFace { Top, Bottom, Front, Back, Left, Right }

    private readonly record struct Face(
        CubeFace Kind,
        (int X, int Y, int Z) Neighbor,
        CubeFace Opposite,
        int MaterialIndex,
        int[] CornerIndices,
        int BorderAxis,
        float BorderValue);

    private static readonly Face[] Faces =
    {
        new(CubeFace.Top,    new( 0, 1, 0), CubeFace.Bottom, 0, new[] { 0, 1, 2, 3 }, 1,  0.5f),
        new(CubeFace.Bottom, new( 0,-1, 0), CubeFace.Top,    1, new[] { 4, 5, 6, 7 }, 1, -0.5f),
        new(CubeFace.Front,  new( 0, 0,-1), CubeFace.Back,   2, new[] { 7, 6, 1, 0 }, 2, -0.5f),
        new(CubeFace.Back,   new( 0, 0, 1), CubeFace.Front,  3, new[] { 5, 4, 3, 2 }, 2,  0.5f),
        new(CubeFace.Left,   new(-1, 0, 0), CubeFace.Right,  4, new[] { 4, 7, 0, 3 }, 0, -0.5f),
        new(CubeFace.Right,  new( 1, 0, 0), CubeFace.Left,   5, new[] { 6, 5, 2, 1 }, 0,  0.5f),
    };
}
