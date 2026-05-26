using System.IO.Compression;
using System.Numerics;
using System.Text;
using KogamaScripts;

namespace KgmExporter;

// .kgmap layout (gzip-compressed):
//   magic "KGMP" (uint32), version (uint16)
//   prototypeCount (int32), then per proto: protoId, scale, vertexCount, vertices
//     vertex = x y z u v atlasU atlasV (7 floats) + r g b a (4 bytes)
//   instanceCount (int32), then per instance: woId, parentWoId, protoId,
//     typeId (v2+), position (3 floats), rotation (4 floats), scale (3 floats)
static class KgmapExport
{
    private const uint Magic = 0x504D474B; // "KGMP"
    private const ushort Version = 2; // v2 adds TypeId to instances
    private const int AtlasColumns = 16;
    private const int AtlasRows = 5;
    private const bool CullInternalFaces = true;
    private static readonly byte[] IdentityByteCorners = { 20, 120, 124, 24, 4, 104, 100, 0 };
    private static readonly int[] GlowingMaterials = { 26, 28, 55 };

    private readonly record struct MeshVertex(
        float X, float Y, float Z,
        float U, float V,
        float AtlasU, float AtlasV,
        byte R, byte G, byte B, byte A);

    public static int WriteWorld(Stream output, WorldSession ws)
    {
        return WriteWorld(output, ws.Client.World.Objects.Values, ws.Client.World.Prototypes);
    }

    public static int WriteWorld(Stream output, IEnumerable<WorldObject> objects, IReadOnlyDictionary<int, Prototype> prototypes)
    {
        var objectList = objects as IReadOnlyCollection<WorldObject> ?? objects.ToArray();

        var protoMeshes = new Dictionary<int, List<MeshVertex>>();
        var usedProtoIds = new HashSet<int>();
        foreach (var obj in objectList)
            usedProtoIds.Add(obj.PrototypeId);

        foreach (int protoId in usedProtoIds)
        {
            if (!prototypes.TryGetValue(protoId, out var proto) || proto.Cubes == null || proto.Cubes.Count == 0)
                continue;
            var verts = BuildPrototypeMesh(proto);
            if (verts.Count == 0)
                continue;
            protoMeshes[protoId] = verts;
        }

        var instances = objectList
            .Where(o => protoMeshes.ContainsKey(o.PrototypeId))
            .ToList();

        using var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
        using var writer = new BinaryWriter(gz, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(Version);

        writer.Write(protoMeshes.Count);
        foreach (var (protoId, verts) in protoMeshes)
        {
            float scale = prototypes[protoId].Scale == 0 ? 1f : prototypes[protoId].Scale;
            writer.Write(protoId);
            writer.Write(scale);
            writer.Write(verts.Count);
            foreach (var v in verts)
            {
                writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z);
                writer.Write(v.U); writer.Write(v.V);
                writer.Write(v.AtlasU); writer.Write(v.AtlasV);
                writer.Write(v.R); writer.Write(v.G); writer.Write(v.B); writer.Write(v.A);
            }
        }

        writer.Write(instances.Count);
        foreach (var inst in instances)
        {
            writer.Write(inst.WoId);
            writer.Write(inst.ParentWoId);
            writer.Write(inst.PrototypeId);
            writer.Write(inst.TypeId);
            writer.Write(inst.Position.X); writer.Write(inst.Position.Y); writer.Write(inst.Position.Z);
            Quaternion rot = NormalizeRotation(inst.Rotation);
            writer.Write(rot.X); writer.Write(rot.Y); writer.Write(rot.Z); writer.Write(rot.W);
            writer.Write(inst.Scale.X); writer.Write(inst.Scale.Y); writer.Write(inst.Scale.Z);
        }

        return instances.Count;
    }

    private static List<MeshVertex> BuildPrototypeMesh(Prototype proto)
    {
        var verts = new List<MeshVertex>(proto.Cubes!.Count * 12);
        foreach (var (grid, cube) in proto.Cubes!)
        {
            foreach (var face in Faces)
            {
                var neighbor = new CubeGridPosition(
                    (short)(grid.X + face.Neighbor.X),
                    (short)(grid.Y + face.Neighbor.Y),
                    (short)(grid.Z + face.Neighbor.Z));

                if (CullInternalFaces &&
                    proto.Cubes!.TryGetValue(neighbor, out var neighborCube) &&
                    FacesTouch(cube, face, neighborCube, Faces[(int)face.Opposite]))
                {
                    continue;
                }

                byte material = cube.FaceMaterials.Length > face.MaterialIndex
                    ? cube.FaceMaterials[face.MaterialIndex]
                    : cube.FaceMaterials.Length > 0 ? cube.FaceMaterials[0] : (byte)1;

                EmitFace(verts, grid, face, cube, MaterialToAtlas(material));
            }
        }
        return verts;
    }

    private static void EmitFace(
        List<MeshVertex> vertices,
        CubeGridPosition grid,
        Face face,
        Cube cube,
        (byte R, byte G, byte B, float AtlasU, float AtlasV) material)
    {
        Vector3[] faceCorners = GetFaceCorners(cube, face);
        Span<Vector3> local = stackalloc Vector3[4];
        Span<Vector2> uv = stackalloc Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            local[i] = new Vector3(
                grid.X + faceCorners[i].X,
                grid.Y + faceCorners[i].Y,
                grid.Z + faceCorners[i].Z);
            uv[i] = GetFaceUv(local[i], face.Kind);
        }

        Vector3 a = local[0], b = local[1], c = local[2], d = local[3];

        vertices.Add(new MeshVertex(a.X, a.Y, a.Z, uv[0].X, uv[0].Y, material.AtlasU, material.AtlasV, material.R, material.G, material.B, 255));
        vertices.Add(new MeshVertex(b.X, b.Y, b.Z, uv[1].X, uv[1].Y, material.AtlasU, material.AtlasV, material.R, material.G, material.B, 255));
        vertices.Add(new MeshVertex(c.X, c.Y, c.Z, uv[2].X, uv[2].Y, material.AtlasU, material.AtlasV, material.R, material.G, material.B, 255));
        vertices.Add(new MeshVertex(a.X, a.Y, a.Z, uv[0].X, uv[0].Y, material.AtlasU, material.AtlasV, material.R, material.G, material.B, 255));
        vertices.Add(new MeshVertex(c.X, c.Y, c.Z, uv[2].X, uv[2].Y, material.AtlasU, material.AtlasV, material.R, material.G, material.B, 255));
        vertices.Add(new MeshVertex(d.X, d.Y, d.Z, uv[3].X, uv[3].Y, material.AtlasU, material.AtlasV, material.R, material.G, material.B, 255));
    }

    private static Quaternion NormalizeRotation(Quaternion rotation)
        => rotation.LengthSquared() <= 1e-8f ? Quaternion.Identity : Quaternion.Normalize(rotation);

    private static Vector2 GetFaceUv(Vector3 local, CubeFace face)
    {
        Vector2 uv = face switch
        {
            CubeFace.Top => new Vector2(local.X, local.Z) + new Vector2(0.5f, 0.5f),
            CubeFace.Bottom => new Vector2(-local.X, local.Z) + new Vector2(-0.5f, 0.5f),
            CubeFace.Front => new Vector2(local.X, local.Y) + new Vector2(0.5f, 0.5f),
            CubeFace.Back => new Vector2(-local.X, local.Y) + new Vector2(-0.5f, 0.5f),
            CubeFace.Left => new Vector2(-local.Z, local.Y) + new Vector2(-0.5f, 0.5f),
            CubeFace.Right => new Vector2(local.Z, local.Y) + new Vector2(0.5f, 0.5f),
            _ => Vector2.Zero
        };
        return uv * 0.5f;
    }

    private static (byte R, byte G, byte B, float AtlasU, float AtlasV) MaterialToAtlas(byte material)
    {
        int i = material;
        if (i < 0 || i >= 63) i = 24;

        int num = 0;
        int num2 = Array.IndexOf(GlowingMaterials, i);
        if (num2 >= 0)
            num = 63 - GlowingMaterials.Length + num2;
        else if (i < GlowingMaterials[0])
            num = i;
        else if (i > GlowingMaterials[^1])
            num = i - GlowingMaterials.Length;
        else
        {
            int num3 = 0;
            for (int j = 0; j < GlowingMaterials.Length - 1 && GlowingMaterials[j] <= i; j++)
                num3++;
            num = i - num3;
        }
        int col = num % 16;
        int rowFromTop = 16 - (num - col) / 16 - 1;
        int row = 15 - rowFromTop;
        return (255, (byte)col, (byte)rowFromTop, col / (float)AtlasColumns, row / (float)AtlasRows);
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
