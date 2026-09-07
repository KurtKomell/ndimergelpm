using System.IO.Ports;

namespace NdiMerger.Tracking;

/// <summary>
/// Native RPLIDAR A3 driver: motor PWM, express/ultra scan, descriptor-driven reads.
/// </summary>
public sealed class RpLidarClient : IDisposable
{
    private const byte SyncByte = 0xA5;
    private const byte SyncByte2 = 0x5A;

    private readonly object _sync = new();
    private SerialPort? _port;
    private Thread? _readThread;
    private volatile bool _exit;
    private volatile bool _disposed;
    private volatile LidarScan? _latestScan;
    private volatile LidarStatus _status = new();
    private DateTime _lastRevolutionStart = DateTime.MinValue;
    private readonly List<LidarPoint> _revolutionBuffer = new(1024);
    private RpDescriptor _scanDescriptor;
    private byte[]? _prevCapsule;
    private const int MinRevPoints = 200;
    private const int MaxRevPoints = 2200;
    private const double TargetRpm = 600;

    /// <summary>A revolution outside this band is a read hiccup, not a real motor speed.</summary>
    private const double MinPlausibleRpm = 200;
    private const double MaxPlausibleRpm = 1200;
    private const double PwmDeadbandRpm = 60;

    private double _smoothedRpm;
    private ushort _motorPwm = RpLidarProtocol.DefaultMotorPwm;
    private bool _pwmSettled;
    private DateTime _lastPwmUpdate = DateTime.MinValue;

    public LidarStatus Status => _status;
    public LidarScan? LatestScan => _latestScan;

    public static IReadOnlyList<string> ListPorts() =>
        SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

    public void Connect(string portName)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RpLidarClient));

        Disconnect();

        var port = new SerialPort(portName, 256000, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 2000,
            WriteTimeout = 1000,
            DtrEnable = false,
            RtsEnable = false
        };


        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();

        _port = port;
        _exit = false;
        _prevCapsule = null;
        _smoothedRpm = 0;
        _motorPwm = RpLidarProtocol.DefaultMotorPwm;
        _lastPwmUpdate = DateTime.MinValue;
        _revolutionBuffer.Clear();
        _status = new LidarStatus
        {
            IsConnected = true,
            ComPort = portName,
            Health = LidarHealthStatus.Unknown
        };

        lock (_sync)
        {
            SendStop();
            Thread.Sleep(20);
            CheckHealth();

            ushort scanMode = TryGetTypicalScanMode();

            StartMotor();
            Thread.Sleep(2000);

            if (!TryStartScan(scanMode, out _scanDescriptor))
            {
                if (!TryStartScan(0, out _scanDescriptor) &&
                    !TryStartStandardScan(out _scanDescriptor))
                {
                    throw new InvalidOperationException("LiDAR lieferte keinen Scan-Deskriptor. Motor dreht?");
                }
            }

            _status.ScanDataType = _scanDescriptor.DataType;
        }

        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "RpLidar-Read",
            Priority = ThreadPriority.AboveNormal
        };
        _readThread.Start();
    }

    public void Disconnect()
    {
        _exit = true;
        _readThread?.Join(TimeSpan.FromSeconds(2));
        _readThread = null;

        lock (_sync)
        {
            if (_port?.IsOpen == true)
            {
                try
                {
                    SendStop();
                    SetMotorPwm(0);
                }
                catch { /* ignore shutdown errors */ }

                try { _port.Close(); } catch { /* ignore */ }
            }

            _port?.Dispose();
            _port = null;
        }

        _revolutionBuffer.Clear();
        _prevCapsule = null;
        _latestScan = null;
        _smoothedRpm = 0;
        _motorPwm = RpLidarProtocol.DefaultMotorPwm;
        _pwmSettled = false;
        _lastPwmUpdate = DateTime.MinValue;
        _status = new LidarStatus { LastError = _status.LastError };
    }

    private void ReadLoop()
    {
        var port = _port;
        if (port is null)
            return;

        while (!_exit && port.IsOpen)
        {
            try
            {
                byte[] payload;
                bool capsuleMode = _scanDescriptor.DataType is RpLidarProtocol.AnsCapsuled
                                                            or RpLidarProtocol.AnsUltraCapsuled;
                lock (_sync)
                {
                    if (_port?.IsOpen != true)
                        break;
                    payload = capsuleMode
                        ? ReadCapsule(_scanDescriptor.DataLength)
                        : ReadExact(_scanDescriptor.DataLength);
                }


                if (payload.Length == 0)
                    continue;

                ProcessPayload(payload);
            }
            catch (TimeoutException)
            {
                _status.LastError = "Read timeout – Motor/Scan aktiv?";
                Thread.Sleep(50);
            }
            catch (Exception ex) when (!_exit)
            {
                _status.LastError = ex.Message;
                _status.IsScanning = false;
                Thread.Sleep(250);
            }
        }
    }

    private void ProcessPayload(byte[] payload)
    {
        switch (_scanDescriptor.DataType)
        {
            case RpLidarProtocol.AnsStandard:
                if (RpLidarProtocol.TryParseStandard(payload, 0, out var point, out bool newRev))
                {
                    if (newRev)
                        TryPublishRevolution("s-flag-standard");
                    AddPoint(point);
                }
                break;

            case RpLidarProtocol.AnsCapsuled:
                ProcessCapsulePayload(payload, RpLidarProtocol.ParseLegacyCapsule);
                break;

            case RpLidarProtocol.AnsUltraCapsuled:
                ProcessCapsulePayload(payload, RpLidarProtocol.ParseUltraCapsule);
                break;

            default:
                _status.LastError = $"Unbekannter Scan-Typ 0x{_scanDescriptor.DataType:X2}";
                break;
        }
    }

    private void ProcessCapsulePayload(
        byte[] payload,
        Func<byte[], byte[], IReadOnlyList<LidarPoint>> parse)
    {
        if (_prevCapsule is not null)
        {
            bool newRev = RpLidarProtocol.CapsuleStartsNewRevolution(_prevCapsule, payload, out float jump);
            if (newRev)
                TryPublishRevolution("capsule-wrap");

            var points = parse(_prevCapsule, payload);
            int added = 0;
            foreach (var p in points)
            {
                if (p.DistanceMm > 0)
                    added++;
                AddPoint(p);
            }

            if (_revolutionBuffer.Count >= MaxRevPoints)
                TryPublishRevolution("rev-overflow");

        }

        _prevCapsule = payload;
    }

    private void AddPoint(LidarPoint point)
    {
        if (point.DistanceMm <= 0)
            return;
        _revolutionBuffer.Add(point);
    }

    private void TryPublishRevolution(string reason)
    {
        if (_revolutionBuffer.Count < MinRevPoints)
        {
            return;
        }

        PublishRevolution(reason);
        _revolutionBuffer.Clear();
    }

    /// <summary>
    /// Brings the motor to its nominal speed after connect and then latches off for good: every
    /// command written while the express scan streams can stall the next read for a full
    /// ReadTimeout, and the exact RPM is irrelevant once the scan is running.
    /// </summary>
    private void RegulateMotor(DateTime now)
    {
        if (_pwmSettled || _smoothedRpm <= 0)
            return;
        if (_lastPwmUpdate != DateTime.MinValue && (now - _lastPwmUpdate).TotalSeconds < 0.5)
            return;

        double error = TargetRpm - _smoothedRpm;
        if (Math.Abs(error) <= PwmDeadbandRpm)
        {
            _pwmSettled = true;
            return;
        }

        int target = Math.Clamp(_motorPwm + (int)Math.Clamp(error * 0.05, -10, 10), 250, 1023);
        _lastPwmUpdate = now;
        if (target == _motorPwm)
            return;

        _motorPwm = (ushort)target;
        lock (_sync)
        {
            try { SetMotorPwm(_motorPwm); }
            catch { /* port may be closing */ }
        }

    }

    private void PublishRevolution(string reason)
    {
        if (_revolutionBuffer.Count == 0)
            return;

        var now = DateTime.UtcNow;
        double rpm = 0;
        if (_lastRevolutionStart != DateTime.MinValue)
        {
            var dt = (now - _lastRevolutionStart).TotalSeconds;
            if (dt > 0.05)
                rpm = 60.0 / dt;
        }

        _lastRevolutionStart = now;
        var points = _revolutionBuffer.ToArray();
        float firstAngle = points.Length > 0 ? points[0].AngleDegrees : 0;
        float lastAngle = points.Length > 0 ? points[^1].AngleDegrees : 0;

        if (rpm >= MinPlausibleRpm && rpm <= MaxPlausibleRpm)
            _smoothedRpm = _smoothedRpm <= 0 ? rpm : _smoothedRpm * 0.75 + rpm * 0.25;

        RegulateMotor(now);

        _latestScan = new LidarScan
        {
            Points = points,
            Timestamp = now,
            Rpm = _smoothedRpm > 0 ? _smoothedRpm : rpm
        };

        _status.IsScanning = true;
        _status.Rpm = _latestScan.Rpm;
        _status.PointsPerRevolution = points.Length;
        _status.ScanDataType = _scanDescriptor.DataType;
        _status.LastError = null;
    }

    private void StartMotor()
    {
        if (_port is null)
            return;

        SetMotorPwm(RpLidarProtocol.DefaultMotorPwm);
    }

    private ushort TryGetTypicalScanMode()
    {
        try
        {
            SendGetLidarConf(RpLidarProtocol.ConfScanModeTypical, ReadOnlySpan<byte>.Empty);
            var desc = ReadDescriptor();
            if (desc.DataLength < 6)
                return 0;

            var payload = ReadExact(desc.DataLength);
            return BitConverter.ToUInt16(payload, 4);
        }
        catch
        {
            return 0;
        }
    }

    private bool TryStartScan(ushort mode, out RpDescriptor descriptor)
    {
        descriptor = default;
        SendExpressScan(mode);
        descriptor = ReadDescriptor();
        return descriptor.DataLength > 0;
    }

    private bool TryStartStandardScan(out RpDescriptor descriptor)
    {
        descriptor = default;
        SendCommand(RpLidarProtocol.CmdScan);
        descriptor = ReadDescriptor();
        return descriptor.DataLength > 0;
    }

    private void CheckHealth()
    {
        try
        {
            SendCommand(RpLidarProtocol.CmdGetHealth);
            var desc = ReadDescriptor();
            var payload = ReadExact(desc.DataLength);
            if (payload.Length == 0)
                return;

            _status.Health = payload[0] switch
            {
                0 => LidarHealthStatus.Good,
                1 => LidarHealthStatus.Warning,
                2 => LidarHealthStatus.Error,
                _ => LidarHealthStatus.Unknown
            };
        }
        catch
        {
            _status.Health = LidarHealthStatus.Unknown;
        }
    }

    private void SendReset() => SendCommand(RpLidarProtocol.CmdReset);
    private void SendStop() => SendCommand(RpLidarProtocol.CmdStop);

    private void SendExpressScan(ushort mode)
    {
        Span<byte> payload = stackalloc byte[5];
        payload[0] = (byte)mode;
        SendCommand(RpLidarProtocol.CmdExpressScan, payload);
    }

    private void SendGetLidarConf(uint type, ReadOnlySpan<byte> extra)
    {
        Span<byte> payload = stackalloc byte[8];
        BitConverter.TryWriteBytes(payload, type);
        extra.CopyTo(payload[4..]);
        SendCommand(RpLidarProtocol.CmdGetLidarConf, payload[..(4 + extra.Length)]);
    }

    private void SetMotorPwm(ushort pwm) =>
        SendCommand(RpLidarProtocol.CmdSetMotorPwm, [(byte)(pwm & 0xFF), (byte)(pwm >> 8)]);

    private void SendCommand(byte cmd, ReadOnlySpan<byte> payload = default)
    {
        if (_port?.IsOpen != true)
            return;

        Span<byte> packet = stackalloc byte[256];
        int len = 0;
        packet[len++] = SyncByte;
        packet[len++] = cmd;
        if (!payload.IsEmpty)
        {
            packet[len++] = (byte)payload.Length;
            payload.CopyTo(packet[len..]);
            len += payload.Length;
        }

        byte crc = 0;
        for (int i = 0; i < len; i++)
            crc ^= packet[i];
        packet[len++] = crc;

        _port.Write(packet[..len].ToArray(), 0, len);
    }

    private RpDescriptor ReadDescriptor()
    {
        var header = ReadExact(7);
        if (header[0] != SyncByte || header[1] != SyncByte2)
            throw new InvalidOperationException("Ungültiger LiDAR-Deskriptor.");

        uint raw = BitConverter.ToUInt32(header, 2);
        return new RpDescriptor
        {
            DataLength = (int)(raw & 0x3FFFFFFF),
            SendMode = (int)(raw >> 30),
            DataType = header[6]
        };
    }

    private byte[] ReadExact(int count)
    {
        if (_port?.IsOpen != true || count <= 0)
            return [];

        var buffer = new byte[count];
        ReadInto(buffer, 0, count);
        return buffer;
    }

    /// <summary>
    /// Reads one capsule, realigning on the sync nibbles and rejecting checksum failures so a
    /// slipped byte cannot corrupt start angles and fake a revolution boundary.
    /// </summary>
    private byte[] ReadCapsule(int length)
    {
        if (_port?.IsOpen != true || length < 4)
            return [];

        var buffer = new byte[length];

        while (!_exit && _port?.IsOpen == true)
        {
            ReadInto(buffer, 0, 1);
            if ((buffer[0] >> 4) != 0x0A)
                continue;

            ReadInto(buffer, 1, 1);
            if ((buffer[1] >> 4) != 0x05)
                continue;

            ReadInto(buffer, 2, length - 2);
            if (RpLidarProtocol.IsCapsuleValid(buffer))
                return buffer;
        }

        return [];
    }

    private void ReadInto(byte[] buffer, int offset, int count)
    {
        int done = 0;
        var deadline = Environment.TickCount64 + 3000;

        while (done < count)
        {
            if (_port?.IsOpen != true)
                throw new InvalidOperationException("LiDAR-Port geschlossen.");
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"LiDAR timeout after {done}/{count} bytes.");

            // A Read asking for more bytes than the port holds does not return on the first byte:
            // it parks until ReadTimeout expires while the driver queue fills up behind it.
            int available = _port.BytesToRead;
            if (available <= 0)
            {
                Thread.Sleep(1);
                continue;
            }

            int read = _port.Read(buffer, offset + done, Math.Min(count - done, available));
            if (read > 0)
                done += read;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Disconnect();
    }
}
