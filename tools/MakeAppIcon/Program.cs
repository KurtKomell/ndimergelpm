using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

var pngPath = @"d:\lpm\ndimergerLPM\src\NdiMerger.App\Assets\performanieMAM.png";
var icoPath = @"d:\lpm\ndimergerLPM\src\NdiMerger.App\Assets\app.ico";
int[] sizes = { 16, 32, 48, 64, 128, 256 };

using var src = new Bitmap(pngPath);
var payloads = new List<byte[]>();
foreach (var size in sizes)
{
    using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.Clear(Color.Transparent);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, 0, 0, size, size);
    }

    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);
    bw.Write(40u);
    bw.Write(size);
    bw.Write(size * 2);
    bw.Write((ushort)1);
    bw.Write((ushort)32);
    bw.Write(0u);
    bw.Write(0u);
    bw.Write(0);
    bw.Write(0);
    bw.Write(0u);
    bw.Write(0u);

    for (int y = size - 1; y >= 0; y--)
    {
        for (int x = 0; x < size; x++)
        {
            var c = bmp.GetPixel(x, y);
            bw.Write(c.B);
            bw.Write(c.G);
            bw.Write(c.R);
            bw.Write(c.A);
        }
    }

    int rowBytes = ((size + 31) / 32) * 4;
    bw.Write(new byte[rowBytes * size]);
    bw.Flush();
    payloads.Add(ms.ToArray());
}

using var outMs = new MemoryStream();
using (var bw = new BinaryWriter(outMs))
{
    bw.Write((ushort)0);
    bw.Write((ushort)1);
    bw.Write((ushort)payloads.Count);
    int offset = 6 + 16 * payloads.Count;
    for (int i = 0; i < sizes.Length; i++)
    {
        byte dim = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
        bw.Write(dim);
        bw.Write(dim);
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(payloads[i].Length);
        bw.Write(offset);
        offset += payloads[i].Length;
    }
    foreach (var p in payloads) bw.Write(p);
}
File.WriteAllBytes(icoPath, outMs.ToArray());
Console.WriteLine($"Wrote {icoPath} ({new FileInfo(icoPath).Length} bytes)");
