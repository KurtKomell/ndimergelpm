namespace NdiMerger.Tracking;

internal readonly struct RpDescriptor
{
    public int DataLength { get; init; }
    public int SendMode { get; init; }
    public byte DataType { get; init; }
}

internal static class RpLidarProtocol
{
    public const byte CmdStop = 0x25;
    public const byte CmdReset = 0x40;
    public const byte CmdScan = 0x20;
    public const byte CmdExpressScan = 0x82;
    public const byte CmdGetHealth = 0x52;
    public const byte CmdGetLidarConf = 0x84;
    public const byte CmdSetMotorPwm = 0xF0;

    public const byte AnsStandard = 0x81;
    public const byte AnsCapsuled = 0x82;
    public const byte AnsUltraCapsuled = 0x84;
    public const byte AnsDenseCapsuled = 0x85;

    public const uint ConfScanModeTypical = 0x0000007C;
    public const ushort DefaultMotorPwm = 660;

    public static bool TryParseStandard(byte[] data, int offset, out LidarPoint point, out bool newRevolution)
    {
        point = default;
        newRevolution = false;
        if (data.Length - offset < 5)
            return false;

        byte b0 = data[offset];
        byte b1 = data[offset + 1];
        byte b2 = data[offset + 2];
        byte b3 = data[offset + 3];
        byte b4 = data[offset + 4];

        bool s = (b0 & 0x01) != 0;
        bool notS = ((b0 >> 1) & 0x01) != 0;
        if (s == notS || (b1 & 0x01) == 0)
            return false;

        newRevolution = s;
        point = new LidarPoint
        {
            Quality = (byte)(b0 >> 2),
            AngleDegrees = ((b1 >> 1) | (b2 << 7)) / 64f,
            DistanceMm = (b3 | (b4 << 8)) / 4f
        };
        return true;
    }

    public static IReadOnlyList<LidarPoint> ParseUltraCapsule(byte[] prev, byte[] current)
    {
        if (prev.Length < 132 || current.Length < 132)
            return [];

        int prevStartQ8 = ReadStartAngleQ8(prev);
        int currStartQ8 = ReadStartAngleQ8(current);
        int diffQ8 = currStartQ8 - prevStartQ8;
        if (prevStartQ8 > currStartQ8)
            diffQ8 += 360 << 8;

        int angleIncQ16 = (diffQ8 << 3) / 3;
        int currentAngleRawQ16 = prevStartQ8 << 8;
        var points = new List<LidarPoint>(96);

        for (int pos = 0; pos < 32; pos++)
        {
            int off = 4 + pos * 4;
            int major = ((prev[off + 1] & 0x0F) << 8) + prev[off];
            int predict1 = ((prev[off + 2] & 0x3F) << 4) + ((prev[off + 1] >> 4) & 0x0F);
            int predict2 = ((prev[off + 3] & 0xFF) << 2) + ((prev[off + 2] >> 6) & 0x03);
            if ((predict1 & 0x200) != 0) predict1 |= unchecked((int)0xFFFFFC00);
            if ((predict2 & 0x200) != 0) predict2 |= unchecked((int)0xFFFFFC00);

            int major2 = pos == 31
                ? ((current[5] & 0x0F) << 8) + current[4]
                : ((prev[off + 5] & 0x0F) << 8) + prev[off + 4];

            major = VarBitScaleDecode(major, out int scale1);
            major2 = VarBitScaleDecode(major2, out int scale2);

            int base1 = major;
            int base2 = major2;
            if (major == 0 && major2 != 0)
            {
                base1 = major2;
                scale1 = scale2;
            }

            int[] distQ2 = [major << 2, 0, 0];
            if (predict1 != -512 && predict1 != 0x1FF)
                distQ2[1] = unchecked(((predict1 << scale1) + base1) << 2);
            if (predict2 != -512 && predict2 != 0x1FF)
                distQ2[2] = unchecked(((predict2 << scale2) + base2) << 2);

            for (int i = 0; i < 3; i++)
            {
                bool sync = ((currentAngleRawQ16 + angleIncQ16) % (360 << 16)) < angleIncQ16;
                int offsetAngleMeanQ16 = (int)(7.5 * Math.PI * (1 << 16) / 180.0);
                if (distQ2[i] >= 50 * 4)
                {
                    int k1 = 98361;
                    int k2 = k1 / distQ2[i];
                    offsetAngleMeanQ16 = (int)(8 * Math.PI * (1 << 16) / 180) - (k2 << 6) - (k2 * k2 * k2 / 98304);
                }

                int angleQ6 = (currentAngleRawQ16 - (int)(offsetAngleMeanQ16 * 180 / Math.PI)) >> 10;
                currentAngleRawQ16 += angleIncQ16;
                if (angleQ6 < 0) angleQ6 += 360 << 6;
                if (angleQ6 >= 360 << 6) angleQ6 -= 360 << 6;

                points.Add(new LidarPoint
                {
                    Quality = distQ2[i] > 0 ? (byte)47 : (byte)0,
                    AngleDegrees = angleQ6 / 64f,
                    DistanceMm = distQ2[i] / 4f
                });
            }
        }

        return points;
    }

    public static IReadOnlyList<LidarPoint> ParseLegacyCapsule(byte[] prev, byte[] current)
    {
        if (prev.Length < 84 || current.Length < 84)
            return [];

        int prevStartQ8 = ReadStartAngleQ8(prev);
        int currStartQ8 = ReadStartAngleQ8(current);
        int diffQ8 = currStartQ8 - prevStartQ8;
        if (prevStartQ8 > currStartQ8)
            diffQ8 += 360 << 8;

        int angleIncQ16 = diffQ8 << 3;
        int currentAngleRawQ16 = prevStartQ8 << 8;
        var points = new List<LidarPoint>(32);

        for (int pos = 0; pos < 16; pos++)
        {
            int off = 4 + pos * 5;
            int dist1 = ((prev[off] >> 2) | (prev[off + 1] << 6)) << 2;
            int dist2 = ((prev[off + 2] >> 2) | (prev[off + 3] << 6)) << 2;
            int dTheta1 = (prev[off + 4] & 0x0F) + ((prev[off] & 0x03) << 4);
            int dTheta2 = (prev[off + 4] >> 4) + ((prev[off + 2] & 0x03) << 4);

            foreach (var (distQ2, dTheta) in new[] { (dist1, dTheta1), (dist2, dTheta2) })
            {
                bool sync = ((currentAngleRawQ16 + angleIncQ16) % (360 << 16)) < angleIncQ16;
                int angleQ6 = (currentAngleRawQ16 - (dTheta << 13)) >> 10;
                currentAngleRawQ16 += angleIncQ16;
                if (angleQ6 < 0) angleQ6 += 360 << 6;
                if (angleQ6 >= 360 << 6) angleQ6 -= 360 << 6;

                points.Add(new LidarPoint
                {
                    Quality = distQ2 > 0 ? (byte)47 : (byte)0,
                    AngleDegrees = angleQ6 / 64f,
                    DistanceMm = distQ2 / 4f
                });
                _ = sync;
            }
        }

        return points;
    }

    /// <summary>Sync nibbles plus XOR checksum over the payload guard against stream slips.</summary>
    public static bool IsCapsuleValid(byte[] capsule)
    {
        if (capsule.Length < 4)
            return false;
        if ((capsule[0] >> 4) != 0x0A || (capsule[1] >> 4) != 0x05)
            return false;

        byte expected = (byte)((capsule[0] & 0x0F) | (capsule[1] << 4));
        byte actual = 0;
        for (int i = 2; i < capsule.Length; i++)
            actual ^= capsule[i];

        return expected == actual;
    }

    /// <summary>
    /// Detects the 0° crossing between consecutive capsules. Only a large backward jump counts;
    /// the hardware start flag is never set on this unit and speed jitter causes small jumps.
    /// </summary>
    public static bool CapsuleStartsNewRevolution(byte[] prev, byte[] current, out float backwardJumpDeg)
    {
        backwardJumpDeg = 0;
        if (current.Length < 4 || prev.Length < 4)
            return false;

        int prevStart = ReadStartAngleQ6(prev);
        int currStart = ReadStartAngleQ6(current);
        if (currStart >= prevStart)
            return false;

        backwardJumpDeg = (prevStart - currStart) / 64f;
        return backwardJumpDeg > 180f;
    }

    internal static int ReadStartAngleQ6(byte[] packet) =>
        packet[2] + ((packet[3] & 0x7F) << 8);

    /// <summary>Start angle in Q8, as expected by the SDK angle-increment math.</summary>
    internal static int ReadStartAngleQ8(byte[] packet) => ReadStartAngleQ6(packet) << 2;

    private static int VarBitScaleDecode(int scaled, out int scaleLevel)
    {
        ReadOnlySpan<int> bases = [3328, 1792, 1280, 512, 0];
        ReadOnlySpan<int> targets = [0x4000, 0x1000, 0x400, 0x200, 0];
        ReadOnlySpan<int> levels = [4, 3, 2, 1, 0];

        for (int i = 0; i < bases.Length; i++)
        {
            int remain = scaled - bases[i];
            if (remain >= 0)
            {
                scaleLevel = levels[i];
                return targets[i] + (remain << scaleLevel);
            }
        }

        scaleLevel = 0;
        return 0;
    }
}
