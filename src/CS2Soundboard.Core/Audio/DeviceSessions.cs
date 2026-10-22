using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CS2Soundboard.Core.Audio;

/// <summary>
/// Adapts a float ISampleProvider to the byte-oriented IWaveProvider used by WasapiOut
/// without per-block allocations.
/// </summary>
internal sealed class FloatToWaveProvider : IWaveProvider
{
    private readonly ISampleProvider _source;
    private float[] _scratch = Array.Empty<float>();

    public FloatToWaveProvider(ISampleProvider source)
    {
        _source = source;
        var f = source.WaveFormat;
        WaveFormat = f.Channels <= 2
            ? WaveFormat.CreateIeeeFloatWaveFormat(f.SampleRate, f.Channels)
            : new WaveFormatExtensible(f.SampleRate, 32, f.Channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        int samples = count / 4;
        if (_scratch.Length < samples) _scratch = new float[samples];
        int n = _source.Read(_scratch, 0, samples);
        Buffer.BlockCopy(_scratch, 0, buffer, offset, n * 4);
        return n * 4;
    }
}

/// <summary>A running WASAPI shared-mode render stream that pulls from a graph. Owns the device object.</summary>
public sealed class OutputSession : IDisposable
{
    private readonly WasapiOut _out;
    private readonly MMDevice _device;
    private int _disposed;

    private OutputSession(WasapiOut output, MMDevice device, ISampleProvider graph, string deviceId, string deviceName, int latencyMs)
    {
        _out = output;
        _device = device;
        Graph = graph;
        DeviceId = deviceId;
        DeviceName = deviceName;
        LatencyMs = latencyMs;
    }

    public ISampleProvider Graph { get; }
    public WaveFormat Format => Graph.WaveFormat;
    public string DeviceId { get; }
    public string DeviceName { get; }
    public int LatencyMs { get; }

    /// <summary>Raised (on a device thread) when playback stops without being disposed by us.</summary>
    public event EventHandler<Exception?>? Faulted;

    public static OutputSession Start(MMDevice device, int latencyMs, Func<WaveFormat, ISampleProvider> graphFactory)
    {
        WaveFormat mix;
        using (var client = device.AudioClient) mix = client.MixFormat;
        var engineFormat = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
        var graph = graphFactory(engineFormat);

        var output = new WasapiOut(device, AudioClientShareMode.Shared, true, latencyMs);
        try
        {
            output.Init(new FloatToWaveProvider(graph));
            var session = new OutputSession(output, device, graph, device.ID, device.FriendlyName, latencyMs);
            output.PlaybackStopped += session.OnStopped;
            output.Play();
            return session;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        Faulted?.Invoke(this, e.Exception ?? new InvalidOperationException("The output device stopped unexpectedly."));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _out.PlaybackStopped -= OnStopped; } catch { /* ignore */ }
        try { _out.Stop(); } catch { /* device may already be gone */ }
        try { _out.Dispose(); } catch { /* ignore */ }
        try { _device.Dispose(); } catch { /* ignore */ }
    }
}

/// <summary>A running WASAPI shared-mode capture stream feeding a <see cref="CaptureSink"/>.</summary>
public sealed class CaptureSession : IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly MMDevice _device;
    private int _disposed;

    private CaptureSession(WasapiCapture capture, MMDevice device, CaptureSink sink, string deviceId, string deviceName)
    {
        _capture = capture;
        _device = device;
        Sink = sink;
        DeviceId = deviceId;
        DeviceName = deviceName;
    }

    public CaptureSink Sink { get; }
    public string DeviceId { get; }
    public string DeviceName { get; }
    public WaveFormat Format => Sink.CaptureFormat;
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public event EventHandler<Exception?>? Faulted;

    public static CaptureSession Start(MMDevice device, bool provideOutput, int bufferMs = 20, int targetLatencyMs = 30, int maxLatencyMs = 150)
    {
        var capture = new WasapiCapture(device, true, bufferMs);
        try
        {
            var sink = new CaptureSink(capture.WaveFormat, provideOutput, targetLatencyMs, maxLatencyMs);
            var session = new CaptureSession(capture, device, sink, device.ID, device.FriendlyName);
            capture.DataAvailable += (_, e) =>
            {
                try { sink.OnData(e.Buffer, e.BytesRecorded); }
                catch { /* never let a conversion error kill the capture thread */ }
            };
            capture.RecordingStopped += session.OnStopped;
            capture.StartRecording();
            return session;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        Faulted?.Invoke(this, e.Exception ?? new InvalidOperationException("The microphone stopped delivering audio."));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _capture.RecordingStopped -= OnStopped; } catch { /* ignore */ }
        try { _capture.StopRecording(); } catch { /* ignore */ }
        try { _capture.Dispose(); } catch { /* ignore */ }
        try { _device.Dispose(); } catch { /* ignore */ }
    }
}
