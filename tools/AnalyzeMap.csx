using System.Drawing;

var path = args.Length > 0 ? args[0] : @"d:\lpm\ndimergerLPM\assets\pixelmap.jpg";
using var bmp = new Bitmap(path);
int w = bmp.Width, h = bmp.Height;
double sx = 8038.0 / w, sy = 5798.0 / h;
Console.WriteLine($"img {w}x{h} scale {sx:F4}x{sy:F4}");

bool Lit(int x, int y)
{
    if ((uint)x >= (uint)w || (uint)y >= (uint)h) return false;
    var c = bmp.GetPixel(x, y);
    return c.R > 28 || c.G > 28 || c.B > 28;
}

var hx = new int[w];
var hy = new int[h];
for (int x = 0; x < w; x++)
{
    int n = 0;
    for (int y = 0; y < h; y += 1)
        if (Lit(x, y)) n++;
    hx[x] = n;
}
for (int y = 0; y < h; y++)
{
    int n = 0;
    for (int x = 0; x < w; x += 1)
        if (Lit(x, y)) n++;
    hy[y] = n;
}

Console.WriteLine("--- major X transitions ---");
for (int x = 2; x < w - 2; x++)
{
    int a = (hx[x - 2] + hx[x - 1]) / 2;
    int b = (hx[x] + hx[x + 1]) / 2;
    int d = Math.Abs(b - a);
    if (d > 40)
        Console.WriteLine($"x={x,4} img  dens {a}->{b}   mapX≈{x * sx:F0}");
}

Console.WriteLine("--- major Y transitions ---");
for (int y = 2; y < h - 2; y++)
{
    int a = (hy[y - 2] + hy[y - 1]) / 2;
    int b = (hy[y] + hy[y + 1]) / 2;
    int d = Math.Abs(b - a);
    if (d > 40)
        Console.WriteLine($"y={y,4} img  dens {a}->{b}   mapY≈{y * sy:F0}");
}

// Scan rows at mid of expected bands to find left/right extents of content
void RowExtents(string name, int yImg)
{
    int left = -1, right = -1;
    for (int x = 0; x < w; x++)
        if (Lit(x, yImg)) { left = x; break; }
    for (int x = w - 1; x >= 0; x--)
        if (Lit(x, yImg)) { right = x; break; }
    Console.WriteLine($"{name} yImg={yImg} mapY≈{yImg * sy:F0}: x=[{left},{right}] mapX=[{left * sx:F0},{right * sx:F0}]");
}

void ColExtents(string name, int xImg)
{
    int top = -1, bot = -1;
    for (int y = 0; y < h; y++)
        if (Lit(xImg, y)) { top = y; break; }
    for (int y = h - 1; y >= 0; y--)
        if (Lit(xImg, y)) { bot = y; break; }
    Console.WriteLine($"{name} xImg={xImg} mapX≈{xImg * sx:F0}: y=[{top},{bot}] mapY=[{top * sy:F0},{bot * sy:F0}]");
}

Console.WriteLine("--- row extents ---");
RowExtents("top-band", (int)(600 / sy));      // mid of west ~ y=600
RowExtents("floor-band", (int)(2900 / sy));   // mid floor
RowExtents("est-band", (int)(5200 / sy));     // mid est

Console.WriteLine("--- col extents ---");
ColExtents("sud-col", (int)(550 / sx));
ColExtents("floor-col", (int)(4000 / sx));
ColExtents("right-col", (int)(7400 / sx));
ColExtents("far-right", (int)(7800 / sx));
