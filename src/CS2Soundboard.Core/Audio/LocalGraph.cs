using System;
using System.Threading;
using NAudio.Wave;

namespace CS2Soundboard.Core.Audio;

/// <summary>
/// Mix for the user's own speakers/headphones: previews (and optional hotkey monitoring) plus an
/// optional monitor source (physical mic, mixed output, or the virtual recording endpoint).
/// This graph is never fed back into the virtual microphone mix.
/// </summary>
public sealed class LocalGraph : ISampleProvider
{
    private readonly int _channels;
    private ISampleProvider? _monitor;
    private float[] _scratch = Array.Empty<float>();
    private float _monitorGain = 0.8f;

    public LocalGraph(WaveFormat format)
    {
        WaveFormat = format;
        _channels = format.Channels;
        Bus = new SoundBus(format);
    }

    public WaveFormat WaveFormat { get; }
    public SoundBus Bus { get; }
    public PeakMeter Meter { get; } = new();

    public float MonitorGain
    {
        get => Volatile.Read(ref _monitorGain);
        set => Volatile.Write(ref _monitorGain, Math.Clamp(value, 0f, 2f));
    }

    /// <summary>Monitor source in this graph's format, or null.</summary>
    public void SetMonitor(ISampleProvider? source)
    {
        if (source != null && (source.WaveFormat.SampleRate != WaveFormat.SampleRate || source.WaveFormat.Channels != _channels))
            throw new ArgumentException("Monitor source must match the local output format");
        Volatile.Write(ref _monitor, source);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        count -= count % _channels;
        Bus.Read(buffer, offset, count);
        var output = buffer.AsSpan(offset, count);

        var mon = Volatile.Read(ref _monitor);
        if (mon != null)
        {
            if (_scratch.Length < count) _scratch = new float[count];
            int n;
            try { n = mon.Read(_scratch, 0, count); } catch { n = 0; }
            float g = MonitorGain;
            for (int i = 0; i < n; i++) output[i] += _scratch[i] * g;
        }

        for (int i = 0; i < output.Length; i++)
        {
            float v = output[i];
            output[i] = v > 1f ? 1f : v < -1f ? -1f : float.IsNaN(v) ? 0f : v;
        }
        Meter.Update(output);
        return count;
    }
}
