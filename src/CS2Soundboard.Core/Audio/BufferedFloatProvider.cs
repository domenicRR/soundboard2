using System;
using System.Threading;
using NAudio.Wave;

namespace CS2Soundboard.Core.Audio;

/// <summary>
/// Jitter/drift-tolerant bridge between a push source (capture device running on its own clock)
/// and a pull consumer (output device running on a different clock).
///
/// * Never returns fewer samples than requested: underruns are filled with silence so the
///   downstream mixer keeps running even if the source stalls or disappears.
/// * Keeps latency bounded: if the buffer grows beyond the maximum latency (source clock faster
///   than sink clock), it skips back to the target latency.
/// * After an underrun it re-primes to the target latency to avoid continuous crackle.
/// </summary>
public sealed class BufferedFloatProvider : ISampleProvider
{
    private readonly FloatRingBuffer _ring;
    private readonly int _channels;
    private readonly int _targetSamples;
    private readonly int _maxSamples;
    private bool _primed;

    private long _underruns;
    private long _driftCorrections;
    private long _overflowDrops;

    public BufferedFloatProvider(int sampleRate, int channels, int targetLatencyMs = 30, int maxLatencyMs = 150, int capacityMs = 1000)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _channels = channels;
        _targetSamples = Align(MsToSamples(sampleRate, channels, targetLatencyMs));
        _maxSamples = Math.Max(_targetSamples + channels, Align(MsToSamples(sampleRate, channels, maxLatencyMs)));
        _ring = new FloatRingBuffer(Math.Max(_maxSamples * 2, MsToSamples(sampleRate, channels, capacityMs)));
    }

    public WaveFormat WaveFormat { get; }

    public long Underruns => Interlocked.Read(ref _underruns);
    public long DriftCorrections => Interlocked.Read(ref _driftCorrections);
    public long OverflowDrops => Interlocked.Read(ref _overflowDrops);
    public double BufferedMilliseconds => _ring.Count * 1000.0 / (WaveFormat.SampleRate * _channels);

    /// <summary>Producer side. Data must be interleaved whole frames.</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        int written = _ring.Write(interleaved, _channels);
        if (written < interleaved.Length) Interlocked.Increment(ref _overflowDrops);
    }

    /// <summary>Consumer side (real-time thread). Always returns <paramref name="count"/>.</summary>
    public int Read(float[] buffer, int offset, int count)
    {
        var dest = buffer.AsSpan(offset, count);
        int available = _ring.Count;

        if (!_primed)
        {
            if (available < _targetSamples)
            {
                dest.Clear();
                return count;
            }
            _primed = true;
        }

        if (available > _maxSamples)
        {
            int drop = available - _targetSamples;
            drop -= drop % _channels;
            _ring.Discard(drop);
            Interlocked.Increment(ref _driftCorrections);
        }

        int n = _ring.Read(dest);
        if (n < count)
        {
            dest.Slice(n).Clear();
            Interlocked.Increment(ref _underruns);
            _primed = false;
        }
        return count;
    }

    /// <summary>
    /// Consumer side, non-real-time use (diagnostics/self-test): drains everything buffered
    /// into <paramref name="dest"/> without priming logic. Returns samples read.
    /// </summary>
    public int Drain(Span<float> dest) => _ring.Read(dest);

    private int Align(int samples) => samples - samples % _channels;

    private static int MsToSamples(int rate, int channels, int ms) => (int)((long)rate * ms / 1000) * channels;
}
