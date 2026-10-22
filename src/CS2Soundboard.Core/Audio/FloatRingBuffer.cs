using System;
using System.Threading;

namespace CS2Soundboard.Core.Audio;

/// <summary>
/// Lock-free single-producer / single-consumer ring buffer of float samples.
/// The producer (e.g. a capture callback) calls <see cref="Write"/>; the consumer
/// (e.g. the output render callback) calls <see cref="Read"/> / <see cref="Discard"/>.
/// No allocations after construction.
/// </summary>
public sealed class FloatRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private long _writePos; // only modified by producer
    private long _readPos;  // only modified by consumer

    public FloatRingBuffer(int minimumCapacity)
    {
        if (minimumCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(minimumCapacity));
        int cap = 1;
        while (cap < minimumCapacity) cap <<= 1;
        _buffer = new float[cap];
        _mask = cap - 1;
    }

    public int Capacity => _buffer.Length;

    /// <summary>Samples currently available to the consumer.</summary>
    public int Count => (int)(Volatile.Read(ref _writePos) - Volatile.Read(ref _readPos));

    /// <summary>
    /// Producer: writes as many samples as fit, rounded down to a multiple of <paramref name="alignment"/>.
    /// Returns the number of samples written (the remainder is dropped).
    /// </summary>
    public int Write(ReadOnlySpan<float> data, int alignment = 1)
    {
        long w = _writePos;
        long r = Volatile.Read(ref _readPos);
        int free = Capacity - (int)(w - r);
        int n = Math.Min(free, data.Length);
        if (alignment > 1) n -= n % alignment;
        if (n <= 0) return 0;

        int idx = (int)(w & _mask);
        int first = Math.Min(n, Capacity - idx);
        data.Slice(0, first).CopyTo(_buffer.AsSpan(idx, first));
        if (n > first) data.Slice(first, n - first).CopyTo(_buffer.AsSpan(0, n - first));
        Volatile.Write(ref _writePos, w + n);
        return n;
    }

    /// <summary>Consumer: reads up to dest.Length samples. Returns samples read.</summary>
    public int Read(Span<float> dest)
    {
        long r = _readPos;
        long w = Volatile.Read(ref _writePos);
        int n = Math.Min((int)(w - r), dest.Length);
        if (n <= 0) return 0;

        int idx = (int)(r & _mask);
        int first = Math.Min(n, Capacity - idx);
        _buffer.AsSpan(idx, first).CopyTo(dest);
        if (n > first) _buffer.AsSpan(0, n - first).CopyTo(dest.Slice(first));
        Volatile.Write(ref _readPos, r + n);
        return n;
    }

    /// <summary>Consumer: skips up to <paramref name="count"/> samples.</summary>
    public int Discard(int count)
    {
        long r = _readPos;
        long w = Volatile.Read(ref _writePos);
        int n = Math.Min((int)(w - r), count);
        if (n <= 0) return 0;
        Volatile.Write(ref _readPos, r + n);
        return n;
    }

    /// <summary>Consumer: discards everything currently buffered.</summary>
    public void Clear() => Volatile.Write(ref _readPos, Volatile.Read(ref _writePos));
}
