// Reads atlas.png, dumps the average color of each tile so we can see
// which compact indices are empty/black.

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: InspectAtlas <atlas.png>");
    return 1;
}

using var img = Image.Load<Rgba32>(args[0]);
int cols = 16, rows = 5;
int tile = img.Width / cols;
if (tile * cols != img.Width || tile * rows != img.Height)
{
    Console.Error.WriteLine($"Unexpected size {img.Width}x{img.Height} (expected {cols*tile}x{rows*tile})");
    return 1;
}

Console.WriteLine($"Atlas {img.Width}x{img.Height}, tile={tile}px, grid={cols}x{rows}");
for (int row = 0; row < rows; row++)
{
    for (int col = 0; col < cols; col++)
    {
        int compactIdx = row * cols + col;
        long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
        int sx = col * tile;
        int sy = row * tile;
        // sample a 32x32 grid in the centre
        int step = Math.Max(1, tile / 16);
        int count = 0;
        for (int y = sy + tile / 4; y < sy + 3 * tile / 4; y += step)
        for (int x = sx + tile / 4; x < sx + 3 * tile / 4; x += step)
        {
            var p = img[x, y];
            sumR += p.R; sumG += p.G; sumB += p.B; sumA += p.A;
            count++;
        }
        if (count == 0) continue;
        int r = (int)(sumR / count);
        int g = (int)(sumG / count);
        int b = (int)(sumB / count);
        int a = (int)(sumA / count);
        string note = (r < 5 && g < 5 && b < 5) ? "  <- BLACK" : "";
        Console.WriteLine($"  tile {compactIdx,3} (col={col,2}, row={row}): avg RGBA = ({r,3},{g,3},{b,3},{a,3}){note}");
    }
}
return 0;
