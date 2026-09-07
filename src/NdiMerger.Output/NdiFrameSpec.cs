namespace NdiMerger.Output;

/// <summary>
/// MAM pixelmap NDI output constants.
/// Full-frame canvas is 8038×5798; zone crops may use other even widths.
/// On the wire this is Full NDI (SpeedHQ) of UYVY at <see cref="TargetFps"/>,
/// matched to encode throughput on a 10 GbE link.
/// </summary>
public static class NdiFrameSpec
{
    public const int Width = 8038;
    public const int Height = 5798;
    public const int BytesPerPixel = 4;
    public const int UyvyBytesPerPixel = 2;

    /// <summary>Compose + NDI send cadence.</summary>
    public const int TargetFps = 30;

    public static long BufferBytes => (long)Width * Height * BytesPerPixel;
    public static long UyvyBufferBytes => UyvyBufferBytesFor(Width, Height);
    public static int Stride => Width * BytesPerPixel;
    public static int UyvyStride => UyvyStrideFor(Width);

    /// <summary>10 Gbit/s Ethernet payload ceiling (bytes/s).</summary>
    public const long TargetNetworkBytesPerSecond = 10_000_000_000L / 8;

    public static int UyvyStrideFor(int width) => width * UyvyBytesPerPixel;

    public static long UyvyBufferBytesFor(int width, int height) =>
        (long)width * height * UyvyBytesPerPixel;

    public static int BgrxStrideFor(int width) => width * BytesPerPixel;

    public static long BgrxBufferBytesFor(int width, int height) =>
        (long)width * height * BytesPerPixel;

    /// <summary>Full-frame compositor canvas must match the MAM pixelmap size.</summary>
    public static void ValidateCanvasSize(int canvasWidth, int canvasHeight)
    {
        if (canvasWidth != Width || canvasHeight != Height)
        {
            throw new InvalidOperationException(
                $"Canvas must be {Width}×{Height} for NDI full-frame output, got {canvasWidth}×{canvasHeight}.");
        }

        ValidateOutputSize(canvasWidth, canvasHeight);
    }

    /// <summary>Any NDI sender size (full frame or zone crop). UYVY requires even width.</summary>
    public static void ValidateOutputSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"NDI size must be positive, got {width}×{height}.");

        if ((width & 1) != 0)
            throw new InvalidOperationException($"NDI UYVY requires even width, got {width}.");
    }

    public static string FormatBufferSizeMb() =>
        $"{UyvyBufferBytes / (1024.0 * 1024.0):0} MB/frame UYVY";

    public static string FormatBandwidthGb(double bytesPerSecond) =>
        $"{bytesPerSecond / 1_000_000_000.0:0.00} GB/s raw";
}
