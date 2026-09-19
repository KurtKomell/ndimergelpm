using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace NdiMerger.Sources;

public sealed class AudioDeviceInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
}

public static class WasapiAudioCatalog
{
    public static IReadOnlyList<AudioDeviceInfo> List()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    list.Add(new AudioDeviceInfo
                    {
                        Id = device.ID,
                        DisplayName = device.FriendlyName
                    });
                }
            }
        }
        catch
        {
            // No WASAPI / empty list — UI still works.
        }

        return list
            .OrderBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// WASAPI capture → planar float chunks for NDI + peak meters for UI.
/// </summary>
public sealed class WasapiAudioCapture : IDisposable
{
    private const float PeakDecay = 0.92f;
    private const int MaxQueuedChunks = 32;
    private const float ClipThreshold = 0.999f;
    private static readonly long ClipHoldTicks = (long)(2.0 * Stopwatch.Frequency);

    private readonly object _gate = new();
    private readonly ConcurrentQueue<AudioChunk> _queue = new();
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private volatile bool _running;
    private volatile float _outputGain = 1f;
    private float _peakL;
    private float _peakR;
    private float _clipPeakL;
    private float _clipPeakR;
    private long _clipHoldUntilLQpc;
    private long _clipHoldUntilRQpc;
    private int _sampleRate = 48000;
    private int _channels = 2;
    private bool _isIeeeFloat;
    private int _bitsPerSample = 32;

    public bool IsRunning => _running;
    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public float PeakL => _peakL;
    public float PeakR => _peakR;
    /// <summary>Linear output gain applied to queued samples and meters (1 = 0 dB).</summary>
    public float OutputGain
    {
        get => _outputGain;
        set => _outputGain = Math.Clamp(value, 0f, 4f);
    }

    public void GetClipHold(out float holdL, out float holdR, out bool showL, out bool showR)
    {
        long now = Stopwatch.GetTimestamp();
        showL = now < Volatile.Read(ref _clipHoldUntilLQpc);
        showR = now < Volatile.Read(ref _clipHoldUntilRQpc);
        holdL = showL ? _clipPeakL : 0;
        holdR = showR ? _clipPeakR : 0;
    }

    public void Start(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        Stop();

        lock (_gate)
        {
            var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDevice(deviceId);
            enumerator.Dispose();

            _capture = new WasapiCapture(_device);
            var format = _capture.WaveFormat;
            _sampleRate = format.SampleRate;
            _channels = Math.Max(1, format.Channels);
            _bitsPerSample = format.BitsPerSample;
            _isIeeeFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _peakL = 0;
            _peakR = 0;
            _clipPeakL = 0;
            _clipPeakR = 0;
            Volatile.Write(ref _clipHoldUntilLQpc, 0);
            Volatile.Write(ref _clipHoldUntilRQpc, 0);
            _running = true;
            _capture.StartRecording();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            if (_capture is not null)
            {
                try { _capture.StopRecording(); } catch { /* ignore */ }
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
                _capture = null;
            }

            _device?.Dispose();
            _device = null;
            while (_queue.TryDequeue(out _)) { }
            _peakL = 0;
            _peakR = 0;
            _clipPeakL = 0;
            _clipPeakR = 0;
            Volatile.Write(ref _clipHoldUntilLQpc, 0);
            Volatile.Write(ref _clipHoldUntilRQpc, 0);
        }
    }

    /// <summary>Drain one planar-float chunk for NDI send. Returns false when empty.</summary>
    public bool TryDequeue(out AudioChunk chunk) => _queue.TryDequeue(out chunk!);

    public void TickPeakDecay()
    {
        _peakL *= PeakDecay;
        _peakR *= PeakDecay;
        if (_peakL < 0.001f) _peakL = 0;
        if (_peakR < 0.001f) _peakR = 0;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) => _running = false;

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_running || e.BytesRecorded <= 0)
            return;

        int bytesPerSample = Math.Max(1, _bitsPerSample / 8);
        int frameBytes = bytesPerSample * _channels;
        if (frameBytes <= 0)
            return;

        int samples = e.BytesRecorded / frameBytes;
        if (samples <= 0)
            return;

        int outChannels = Math.Min(2, _channels);
        var planar = new float[outChannels * samples];
        float peakL = 0;
        float peakR = 0;

        if (_isIeeeFloat && _bitsPerSample == 32)
        {
            var floats = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, floats, 0, e.BytesRecorded);
            for (int i = 0; i < samples; i++)
            {
                float l = floats[i * _channels];
                float r = _channels > 1 ? floats[i * _channels + 1] : l;
                planar[i] = l;
                if (outChannels > 1)
                    planar[samples + i] = r;
                float al = Math.Abs(l);
                float ar = Math.Abs(r);
                if (al > peakL) peakL = al;
                if (ar > peakR) peakR = ar;
            }
        }
        else if (_bitsPerSample == 16)
        {
            for (int i = 0; i < samples; i++)
            {
                int off = i * frameBytes;
                short s0 = (short)(e.Buffer[off] | (e.Buffer[off + 1] << 8));
                float l = s0 / 32768f;
                float r = l;
                if (_channels > 1 && off + 3 < e.BytesRecorded)
                {
                    short s1 = (short)(e.Buffer[off + 2] | (e.Buffer[off + 3] << 8));
                    r = s1 / 32768f;
                }

                planar[i] = l;
                if (outChannels > 1)
                    planar[samples + i] = r;
                float al = Math.Abs(l);
                float ar = Math.Abs(r);
                if (al > peakL) peakL = al;
                if (ar > peakR) peakR = ar;
            }
        }
        else if (_bitsPerSample == 24)
        {
            for (int i = 0; i < samples; i++)
            {
                int off = i * frameBytes;
                int s0 = (e.Buffer[off] << 8) | (e.Buffer[off + 1] << 16) | (e.Buffer[off + 2] << 24);
                float l = (s0 >> 8) / 8388608f;
                float r = l;
                if (_channels > 1 && off + 5 < e.BytesRecorded)
                {
                    int s1 = (e.Buffer[off + 3] << 8) | (e.Buffer[off + 4] << 16) | (e.Buffer[off + 5] << 24);
                    r = (s1 >> 8) / 8388608f;
                }

                planar[i] = l;
                if (outChannels > 1)
                    planar[samples + i] = r;
                float al = Math.Abs(l);
                float ar = Math.Abs(r);
                if (al > peakL) peakL = al;
                if (ar > peakR) peakR = ar;
            }
        }
        else if (_bitsPerSample == 32 && !_isIeeeFloat)
        {
            for (int i = 0; i < samples; i++)
            {
                int off = i * frameBytes;
                int s0 = BitConverter.ToInt32(e.Buffer, off);
                float l = s0 / 2147483648f;
                float r = l;
                if (_channels > 1 && off + 7 < e.BytesRecorded)
                {
                    int s1 = BitConverter.ToInt32(e.Buffer, off + 4);
                    r = s1 / 2147483648f;
                }

                planar[i] = l;
                if (outChannels > 1)
                    planar[samples + i] = r;
                float al = Math.Abs(l);
                float ar = Math.Abs(r);
                if (al > peakL) peakL = al;
                if (ar > peakR) peakR = ar;
            }
        }
        else
        {
            return;
        }

        float gain = _outputGain;
        if (Math.Abs(gain - 1f) > 0.0001f)
        {
            for (int i = 0; i < planar.Length; i++)
                planar[i] *= gain;
            peakL *= gain;
            peakR *= gain;
        }

        NotePeaks(peakL, peakR);

        // Drop oldest if the NDI consumer falls behind.
        while (_queue.Count >= MaxQueuedChunks && _queue.TryDequeue(out _)) { }

        _queue.Enqueue(new AudioChunk(planar, outChannels, samples, _sampleRate));
    }

    private void NotePeaks(float peakL, float peakR)
    {
        if (peakL > _peakL) _peakL = Math.Min(1f, peakL);
        if (peakR > _peakR) _peakR = Math.Min(1f, peakR);

        long until = Stopwatch.GetTimestamp() + ClipHoldTicks;
        if (peakL >= ClipThreshold)
        {
            _clipPeakL = 1f;
            Volatile.Write(ref _clipHoldUntilLQpc, until);
        }

        if (peakR >= ClipThreshold)
        {
            _clipPeakR = 1f;
            Volatile.Write(ref _clipHoldUntilRQpc, until);
        }
    }

    public void Dispose() => Stop();
}

public sealed class AudioChunk
{
    public AudioChunk(float[] planarData, int channels, int samples, int sampleRate)
    {
        PlanarData = planarData;
        Channels = channels;
        Samples = samples;
        SampleRate = sampleRate;
    }

    /// <summary>Planar float: channel0[samples] then channel1[samples] …</summary>
    public float[] PlanarData { get; }
    public int Channels { get; }
    public int Samples { get; }
    public int SampleRate { get; }
}
