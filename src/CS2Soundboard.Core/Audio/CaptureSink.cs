using System;
using System.Threading;
using NAudio.Wave;

namespace CS2Soundboard.Core.Audio;

/// <summary>
/// Receives raw capture buffers (any PCM/float format, any channel count) on the capture thread,
/// converts them to mono float, meters them, and pushes them into a drift-tolerant buffer that the
/// output thread pulls from. An optional tap receives a copy (used for local monitoring / self-test).
/// </summary>
public sealed class CaptureSink
{
    private float[] _interleaved = Array.Empty<float>();
    private float[] _mono = Array.Empty<float>();
    private BufferedFloatProvider? _tap;
    private BufferedFloatProvider? _analysisTap;
    private long _blocks;
    private long _silentFrames;
    private long _framesReceived;

    public CaptureSink(WaveFormat captureFormat, bool provideOutput, int targetLatencyMs = 30, int maxLatencyMs = 150)
    {
        if (!SampleConverter.IsSupported(captureFormat))
            throw new NotSupportedException($"Capture format is not supported: {SampleConverter.Describe(captureFormat)}");
        CaptureFormat = captureFormat;
        if (provideOutput)
            Output = new BufferedFloatProvider(captureFormat.SampleRate, 1, targetLatencyMs, maxLatencyMs);
    }

    public WaveFormat CaptureFormat { get; }

    /// <summary>Mono stream at the capture sample rate (null if this sink only meters).</summary>
    public BufferedFloatProvider? Output { get; }

    public PeakMeter Meter { get; } = new();

    /// <summary>Optional secondary consumer (mono, capture sample rate).</summary>
    public BufferedFloatProvider? Tap
    {
        get => Volatile.Read(ref _tap);
        set => Volatile.Write(ref _tap, value);
    }

    /// <summary>Optional consumer used by diagnostics (self-test).</summary>
    public BufferedFloatProvider? AnalysisTap
    {
        get => Volatile.Read(ref _analysisTap);
        set => Volatile.Write(ref _analysisTap, value);
    }

    public long BlocksReceived => Interlocked.Read(ref _blocks);
    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    /// <summary>How long the device has been delivering exact digital zeros (privacy block / hardware mute).</summary>
    public double DigitalSilenceSeconds => (double)Interlocked.Read(ref _silentFrames) / CaptureFormat.SampleRate;

    /// <summary>Called from the capture thread.</summary>
    public void OnData(byte[] buffer, int bytes)
    {
        int samples = SampleConverter.SampleCount(bytes, CaptureFormat);
        int channels = CaptureFormat.Channels;
        int frames = samples / channels;
        if (frames <= 0) return;

        if (_interleaved.Length < samples) _interleaved = new float[samples * 2];
        if (_mono.Length < frames) _mono = new float[frames * 2];

        SampleConverter.ToFloat(buffer, frames * channels * (CaptureFormat.BitsPerSample / 8), CaptureFormat, _interleaved);
        SampleConverter.DownmixToMono(_interleaved.AsSpan(0, frames * channels), channels, _mono);
        var mono = _mono.AsSpan(0, frames);

        Meter.Update(mono);

        bool allZero = true;
        for (int i = 0; i < mono.Length; i++)
        {
            if (mono[i] != 0f) { allZero = false; break; }
        }
        if (allZero) Interlocked.Add(ref _silentFrames, frames);
        else Interlocked.Exchange(ref _silentFrames, 0);

        Output?.Write(mono);
        Tap?.Write(mono);
        AnalysisTap?.Write(mono);

        Interlocked.Add(ref _framesReceived, frames);
        Interlocked.Increment(ref _blocks);
    }
}
