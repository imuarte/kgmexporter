// Convert vertical-strip atlas (1024x81920 = 80 slices stacked top-down) into
// the 16x5 packed grid kgmview expects. Glowing materials (26, 28, 55) move to
// the last 3 slots so the remaining 60 tiles stay contiguous.
//
//   dotnet run --project tools/BuildAtlasGrid.csproj -- <input> <output>

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: BuildAtlasGrid <input-strip.png> <output-grid.png>");
    return 1;
}

string inPath = args[0];
string outPath = args[1];

using var src = Image.Load<Rgba32>(inPath);
int tile = src.Width;
int sliceCount = src.Height / tile;
if (sliceCount * tile != src.Height)
{
    Console.Error.WriteLine($"Unexpected layout: {src.Width}x{src.Height}");
    return 1;
}

int cols = 16, rows = 5;
using var grid = new Image<Rgba32>(tile * cols, tile * rows);

for (int i = 0; i < sliceCount; i++)
{
    // Mirrors kgmview/src/gl/Texture.cpp:67 - source slices 0..62 get compacted
    // through MaterialToCompactIndex, but slices 63..79 must keep their raw
    // index. The packed mapping clamps anything >= 63 to 24, which would
    // overwrite the slice-24 tile (the default cube material) with the empty
    // slice 79 and leave material 24 black in-game.
    int dstTile = i < 63 ? MaterialToCompactIndex(i) : i;
    if (dstTile >= cols * rows) continue;

    int col = dstTile % cols;
    int rowFromTop = dstTile / cols;
    int dstX = col * tile;
    int dstY = rowFromTop * tile;

    int srcY = i * tile;
    using var slice = src.Clone(ctx => ctx.Crop(new Rectangle(0, srcY, tile, tile)));
    grid.Mutate(ctx => ctx.DrawImage(slice, new Point(dstX, dstY), 1f));
}

static int MaterialToCompactIndex(int material)
{
    int[] glowing = { 26, 28, 55 };
    if (material < 0 || material >= 63) material = 24;
    for (int i = 0; i < 3; i++)
        if (glowing[i] == material) return 60 + i;
    if (material < glowing[0]) return material;
    if (material > glowing[2]) return material - 3;
    int subtract = 0;
    for (int i = 0; i < 2 && glowing[i] <= material; i++) subtract++;
    return material - subtract;
}

// AssetsTools BC7 decoder returns the bytes as Unity stored them, which is
// already in the sRGB encoding the atlas was authored in. Blender/glTF expect
// baseColorTexture to be sRGB-encoded - so we save the bytes verbatim.
grid.SaveAsPng(outPath);
Console.WriteLine($"Wrote {outPath} ({grid.Width}x{grid.Height})");
return 0;
