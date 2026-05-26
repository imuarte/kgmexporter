// One-shot: convert vertical-strip atlas (64x5120 = 80 slices stacked) into a
// 16x5 tile grid PNG (1024x320). Run once, output is committed to assets/.
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
if (sliceCount * tile != src.Height || sliceCount != 80)
{
    Console.Error.WriteLine($"Unexpected layout: {src.Width}x{src.Height} (expected NxN*80)");
    return 1;
}

int cols = 16, rows = 5;
using var grid = new Image<Rgba32>(tile * cols, tile * rows);

for (int i = 0; i < sliceCount; i++)
{
    int col = i % cols;
    int rowFromTop = i / cols;
    int dstX = col * tile;
    int dstY = rowFromTop * tile;

    int srcY = i * tile;
    using var slice = src.Clone(ctx => ctx.Crop(new Rectangle(0, srcY, tile, tile)));
    grid.Mutate(ctx => ctx.DrawImage(slice, new Point(dstX, dstY), 1f));
}

// AssetsTools BC7 decoder returns the bytes as Unity stored them, which is
// already in the sRGB encoding the atlas was authored in. Blender/glTF expect
// baseColorTexture to be sRGB-encoded - so we save the bytes verbatim.
grid.SaveAsPng(outPath);
Console.WriteLine($"Wrote {outPath} ({grid.Width}x{grid.Height})");
return 0;
