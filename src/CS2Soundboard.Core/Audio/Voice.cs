using System;
using System.Threading;

namespace CS2Soundboard.Core.Audio;

/// <summary>
/// A single playing instance of a sound, already in the bus's output format
/// (sample rate and channel count). Read is called on the real-time audio thread.
/// </summary>
public abstract class Voice : IDisposable
{
    private int _finished;

    protected Voice(string key, string displayName, TimeSpan duration)
    {
        Key = key;
        DisplayName = displayName;
        Duration = duration;
        StartedUtc = DateTime.UtcNow;
    }

    /// <summary>Identifies the sound (e.g. its relative path). Used for duplicate detection and stop-by-key.</summary>
    public string Key { get; }
    public string DisplayName { get; }
    public TimeSpan Duration { get; }
    public DateTime StartedUtc { get; }

    // Fade-out state, owned by the bus (audio thread only).
    internal int FadeRemaining = -1;
    internal int FadeTotal;

    public bool IsFinished => Volatile.Read(ref _finished) == 1;
    internal void MarkFinished() => Volatile.Write(ref _finished, 1);

    /// <summary>Error raised while producing audio (e.g. decoder failure in a streaming voice).</summary>
    public virtual string? Error => null;

    /// <summary>Playback progress in [0,1] (approximate, for UI).</summary>
    public abstract double Progress { get; }

    /// <summary>Fills interleaved samples. Returning fewer than <paramref name="count"/> means the voice has ended.</summary>
    public abstract int Read(float[] buffer, int offset, int count);

    public virtual void Dispose() { }
}

/// <summary>Plays a fully decoded clip from memory. No I/O or allocation on the audio thread.</summary>
public sealed class CachedVoice : Voice
{
    private readonly AudioClip _clip;
    private int _position;

    public CachedVoice(AudioClip clip, string key, string displayName)
        : base(key, displayName, clip.Duration)
    {
        _clip = clip;
    }

    public override double Progress => _clip.Samples.Length == 0 ? 1 : (double)Volatile.Read(ref _position) / _clip.Samples.Length;

    public override int Read(float[] buffer, int offset, int count)
    {
        int remaining = _clip.Samples.Length - _position;
        int n = Math.Min(remaining, count);
        if (n > 0)
        {
            Array.Copy(_clip.Samples, _position, buffer, offset, n);
            Volatile.Write(ref _position, _position + n);
        }
        return n;
    }
}
