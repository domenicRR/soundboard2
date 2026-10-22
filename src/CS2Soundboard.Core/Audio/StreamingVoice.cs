using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace CS2Soundboard.Core.Audio;

/// <summary>
/// Plays long files without loading them fully into memory. A background thread decodes into a
/// ring buffer; the audio thread only copies from that buffer (no disk I/O on the audio thread).
/// </summary>
public sealed class StreamingVoice : Voice
{
    private readonly FloatRingBuffer _ring;
    private readonly int _channels;
    private readonly CancellationTokenSource _cts = new();
    private readonly long _totalSamples;
    private long _played;
    private volatile bool _eof;
    private volatile string? _error;

    private StreamingVoice(string key, string displayName, TimeSpan duration, int sampleRate, int channels)
        : base(key, displayName, duration)
    {
        _channels = channels;
        _ring = new FloatRingBuffer(sampleRate * channels * 2); // ~2 s
        _totalSamples = (long)(duration.TotalSeconds * sampleRate * channels);
    }

    public override string? Error => _error;

    public override double Progress => _totalSamples <= 0 ? 0 : Math.Min(1.0, (double)Interlocked.Read(ref _played) / _totalSamples);

    /// <summary>Opens the file, starts decoding and waits until a short pre-buffer is ready.</summary>
    public static async Task<StreamingVoice> StartAsync(string path, int sampleRate, int channels, string key, string displayName)
    {
        var reader = AudioDecoder.OpenReader(path);
        var voice = new StreamingVoice(key, displayName, reader.TotalTime, sampleRate, channels);
        ISampleProvider sp;
        try { sp = AudioDecoder.ToEngineFormat(reader, sampleRate, channels); }
        catch { reader.Dispose(); throw; }

        var thread = new Thread(() => voice.Pump(reader, sp, sampleRate))
        {
            IsBackground = true,
            Name = "Soundboard stream decoder",
            Priority = ThreadPriority.AboveNormal,
        };
        thread.Start();

        int prebuffer = sampleRate * channels / 4; // 250 ms
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!voice._eof && voice._ring.Count < prebuffer && DateTime.UtcNow < deadline)
            await Task.Delay(5).ConfigureAwait(false);

        if (voice._error != null && voice._ring.Count == 0)
        {
            voice.Dispose();
            throw new AudioDecodeException(voice._error);
        }
        return voice;
    }

    private void Pump(WaveStream reader, ISampleProvider sp, int sampleRate)
    {
        var token = _cts.Token;
        var chunk = new float[sampleRate * _channels / 20]; // 50 ms
        try
        {
            while (!token.IsCancellationRequested)
            {
                int n = sp.Read(chunk, 0, chunk.Length);
                if (n <= 0) break;
                n -= n % _channels;
                int offset = 0;
                while (offset < n && !token.IsCancellationRequested)
                {
                    int w = _ring.Write(chunk.AsSpan(offset, n - offset), _channels);
                    offset += w;
                    if (w == 0) Thread.Sleep(10);
                }
            }
        }
        catch (Exception ex)
        {
            _error = $"Playback error in '{DisplayName}': {ex.Message}";
        }
        finally
        {
            _eof = true;
            try { reader.Dispose(); } catch { /* ignore */ }
        }
    }

    public override int Read(float[] buffer, int offset, int count)
    {
        int n = _ring.Read(buffer.AsSpan(offset, count));
        Interlocked.Add(ref _played, n);
        if (n < count)
        {
            if (_eof && _ring.Count == 0) return n; // finished
            // Decoder fell behind: pad with silence and keep going.
            Array.Clear(buffer, offset + n, count - n);
            return count;
        }
        return count;
    }

    public override void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
    }
}
