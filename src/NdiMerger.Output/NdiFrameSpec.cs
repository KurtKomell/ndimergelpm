namespace NdiMerger.Output;

/// <summary>
/// MAM pixelmap full-frame NDI output dimensions (8038×5798 BGRX).
/// </summary>
public static class NdiFrameSpec
{
    public const int Width = 8038;
    public const int Height = 5798;
    public const int BytesPerPixel = 4;

    public static long BufferBytes => (long)Width * Height * BytesPerPixel;
    public static int Stride => Width * BytesPerPixel;

    public static void ValidateCanvasSize(int canvasWidth, int canvasHeight)
    {
        if (canvasWidth != Width || canvasHeight != Height)
        {
            throw new InvalidOperationException(
                $"Canvas must be {Width}×{Height} for NDI full-frame output, got {canvasWidth}×{canvasHeight}.");
        }
    }

    public static string FormatBufferSizeMb() =>
        $"{BufferBytes / (1024.0 * 1024.0):0} MB/frame";

    public const long TargetBytesPerSecond = 7_000_000_000L;

    public static string FormatBandwidthGb(double bytesPerSecond) =>
        $"{bytesPerSecond / 1_000_000_000.0:0.00} GB/s";
}
