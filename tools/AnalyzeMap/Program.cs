using System.Drawing;

var path = @"d:\lpm\ndimergerLPM\assets\pixelmap.jpg";
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
    for (int y = 0; y < h; y++)
        if (Lit(x, y)) n++;
    hx[x] = n;
}
for (int y = 0; y < h; y++)
{
    int n = 0;
    for (int x = 0; x < w; x++)
        if (Lit(x, y)) n++;
    hy[y] = n;
}

Console.WriteLine("--- major X transitions ---");
int lastX = -999;
for (int x = 2; x < w - 2; x++)
{
    int a = (hx[x - 2] + hx[x - 1]) / 2;
    int b = (hx[x] + hx[x + 1]) / 2;
    int d = Math.Abs(b - a);
    if (d > 35 && x - lastX > 3)
    {
        Console.WriteLine($"x={x,4} img  dens {a}->{b}   mapX≈{x * sx:F0}");
        lastX = x;
    }
}

Console.WriteLine("--- major Y transitions ---");
int lastY = -999;
for (int y = 2; y < h - 2; y++)
{
    int a = (hy[y - 2] + hy[y - 1]) / 2;
    int b = (hy[y] + hy[y + 1]) / 2;
    int d = Math.Abs(b - a);
    if (d > 35 && y - lastY > 3)
    {
        Console.WriteLine($"y={y,4} img  dens {a}->{b}   mapY≈{y * sy:F0}");
        lastY = y;
    }
}

void RowExtents(string name, double mapY)
{
    int yImg = (int)(mapY / sy);
    yImg = Math.Clamp(yImg, 0, h - 1);
    int left = -1, right = -1;
    for (int x = 0; x < w; x++)
        if (Lit(x, yImg)) { left = x; break; }
    for (int x = w - 1; x >= 0; x--)
        if (Lit(x, yImg)) { right = x; break; }
    Console.WriteLine($"{name} mapY={mapY:F0} yImg={yImg}: xImg=[{left},{right}] mapX=[{left * sx:F0},{right * sx:F0}] widthMap={(right - left + 1) * sx:F0}");
}

void ColExtents(string name, double mapX)
{
    int xImg = (int)(mapX / sx);
    xImg = Math.Clamp(xImg, 0, w - 1);
    int top = -1, bot = -1;
    for (int y = 0; y < h; y++)
        if (Lit(xImg, y)) { top = y; break; }
    for (int y = h - 1; y >= 0; y--)
        if (Lit(xImg, y)) { bot = y; break; }
    Console.WriteLine($"{name} mapX={mapX:F0} xImg={xImg}: yImg=[{top},{bot}] mapY=[{top * sy:F0},{bot * sy:F0}] heightMap={(bot - top + 1) * sy:F0}");
}

Console.WriteLine("--- row extents ---");
RowExtents("west-mid", 600);
RowExtents("floor-mid", 2900);
RowExtents("nord-mid-right", 3200);
RowExtents("est-mid", 5200);

Console.WriteLine("--- col extents ---");
ColExtents("sud", 550);
ColExtents("floor", 4000);
ColExtents("right1", 7200);
ColExtents("right2", 7500);
ColExtents("right3", 7900);

// Find connected components of lit regions approximately by flood-ish row bands
Console.WriteLine("--- right strip segments at x~7400 ---");
int rx = (int)(7400 / sx);
int segStart = -1;
for (int y = 0; y < h; y++)
{
    bool lit = Lit(rx, y) || Lit(rx - 2, y) || Lit(rx + 2, y);
    if (lit && segStart < 0) segStart = y;
    if (!lit && segStart >= 0)
    {
        Console.WriteLine($"segment yImg=[{segStart},{y - 1}] mapY=[{segStart * sy:F0},{(y - 1) * sy:F0}] h={(y - segStart) * sy:F0}");
        segStart = -1;
    }
}
if (segStart >= 0)
    Console.WriteLine($"segment yImg=[{segStart},{h - 1}] mapY=[{segStart * sy:F0},{(h - 1) * sy:F0}] h={(h - segStart) * sy:F0}");
