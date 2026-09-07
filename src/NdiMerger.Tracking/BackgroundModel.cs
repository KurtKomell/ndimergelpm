namespace NdiMerger.Tracking;

/// <summary>
/// Rolling background distance model per 1° bin to suppress static walls/furniture.
/// </summary>
public sealed class BackgroundModel
{
    private const int BinCount = 360;
    private readonly float[] _background = new float[BinCount];
    private readonly bool[] _initialized = new bool[BinCount];
    private readonly Queue<(int bin, float distance)> _recent = new();
    private readonly int[] _fgStreak = new int[BinCount];
    private readonly bool[] _protected = new bool[BinCount];

    /// <summary>
    /// Scans a bin may stay foreground before it is re-baselined. Protected bins (occupied by a
    /// confirmed person) never re-baseline, so a standing person is not swallowed as furniture.
    /// </summary>
    private const int MaxForegroundStreak = 60;

    public void Reset()
    {
        Array.Clear(_background);
        Array.Clear(_initialized);
        Array.Clear(_fgStreak);
        Array.Clear(_protected);
        _recent.Clear();
    }

    public void BeginFrame() => Array.Clear(_protected);

    /// <summary>Keep bins around a confirmed person from being absorbed into the background.</summary>
    public void ProtectAngle(float angleDeg, int halfWidthBins = 12)
    {
        int center = AngleToBin(angleDeg);
        for (int d = -halfWidthBins; d <= halfWidthBins; d++)
        {
            int bin = (center + d) % BinCount;
            if (bin < 0) bin += BinCount;
            _protected[bin] = true;
            _fgStreak[bin] = 0;
        }
    }

    /// <summary>Pure query: adapting here as well as in <see cref="Learn"/> absorbed people within a second.</summary>
    public bool IsForeground(float angleDeg, float distanceMm, float marginMm)
    {
        if (distanceMm <= 0)
            return false;

        int bin = AngleToBin(angleDeg);
        if (!_initialized[bin])
        {
            _background[bin] = distanceMm;
            _initialized[bin] = true;
            return false;
        }

        if (distanceMm + marginMm >= _background[bin])
        {
            _fgStreak[bin] = 0;
            return false;
        }

        if (_protected[bin])
            return true;

        if (++_fgStreak[bin] > MaxForegroundStreak)
        {
            _background[bin] = distanceMm;
            _fgStreak[bin] = 0;
            return false;
        }

        return true;
    }

    public void Learn(float angleDeg, float distanceMm)
    {
        if (distanceMm <= 0)
            return;

        int bin = AngleToBin(angleDeg);
        if (_protected[bin])
            return;

        if (!_initialized[bin])
        {
            _background[bin] = distanceMm;
            _initialized[bin] = true;
            return;
        }

        // ~18 s time constant at 11 scans/s: static furniture settles, people stay foreground.
        _background[bin] = _background[bin] * 0.995f + distanceMm * 0.005f;
    }

    private static int AngleToBin(float angleDeg)
    {
        float a = angleDeg % 360f;
        if (a < 0) a += 360f;
        return Math.Clamp((int)a, 0, BinCount - 1);
    }
}
