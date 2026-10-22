using System;
using System.Collections.Concurrent;
using System.Threading;
using NAudio.Wave;

namespace CS2Soundboard.Core.Audio;

/// <summary>
/// Real-time mixer for sound-effect voices.
///
/// Control threads never touch the voice list directly: they enqueue commands that the audio
/// thread applies at the start of each block, so the render callback takes no locks.
/// Finished voices are handed back through <see cref="Finished"/> for disposal and UI updates.
/// </summary>
public sealed class SoundBus : ISampleProvider
{
    public const int MaxVoices = 64;

    private enum CommandKind { Play, StopKey, StopVoice, StopAll }

    private readonly record struct Command(CommandKind Kind, Voice? Voice, string? Key, bool StopOthersFirst, bool StopSameKeyFirst);

    private readonly Voice?[] _voices = new Voice?[MaxVoices];
    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly int _channels;
    private readonly int _fadeFrames;
    private float[] _scratch = Array.Empty<float>();
    private int _count;
    private float _currentGain;
    private float _targetGain = 1f;
    private long _blocks;

    public SoundBus(WaveFormat format)
    {
        if (format.Encoding != WaveFormatEncoding.IeeeFloat) throw new ArgumentException("SoundBus requires IEEE float format");
        WaveFormat = format;
        _channels = format.Channels;
        _fadeFrames = Math.Max(1, format.SampleRate * 8 / 1000); // 8 ms fade on stop
        _currentGain = 1f;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Voices that have ended or been stopped. Drain on a control thread and dispose.</summary>
    public ConcurrentQueue<Voice> Finished { get; } = new();

    /// <summary>Linear gain applied to the bus (smoothed per block).</summary>
    public float TargetGain
    {
        get => Volatile.Read(ref _targetGain);
        set => Volatile.Write(ref _targetGain, Math.Max(0f, value));
    }

    public int ActiveVoices => Volatile.Read(ref _count);
    public long BlocksRendered => Interlocked.Read(ref _blocks);

    public void Play(Voice voice, bool stopOthersFirst = false, bool stopSameKeyFirst = false)
    {
        ArgumentNullException.ThrowIfNull(voice);
        _commands.Enqueue(new Command(CommandKind.Play, voice, voice.Key, stopOthersFirst, stopSameKeyFirst));
    }

    public void Stop(string key) => _commands.Enqueue(new Command(CommandKind.StopKey, null, key, false, false));
    public void Stop(Voice voice) => _commands.Enqueue(new Command(CommandKind.StopVoice, voice, null, false, false));
    public void StopAll() => _commands.Enqueue(new Command(CommandKind.StopAll, null, null, false, false));

    public int Read(float[] buffer, int offset, int count)
    {
        var output = buffer.AsSpan(offset, count);
        output.Clear();
        ApplyCommands();

        if (_scratch.Length < count) _scratch = new float[count];

        for (int vi = 0; vi < _count;)
        {
            var v = _voices[vi]!;
            int n;
            bool done;
            try
            {
                n = v.Read(_scratch, 0, count);
                done = n < count;
            }
            catch
            {
                n = 0;
                done = true;
            }
            n -= n % _channels;

            if (v.FadeRemaining >= 0)
            {
                int frames = n / _channels;
                int f = 0;
                for (; f < frames; f++)
                {
                    if (v.FadeRemaining <= 0) { done = true; break; }
                    float g = (float)v.FadeRemaining / v.FadeTotal;
                    v.FadeRemaining--;
                    int i = f * _channels;
                    for (int c = 0; c < _channels; c++) _scratch[i + c] *= g;
                }
                n = f * _channels;
                if (v.FadeRemaining <= 0) done = true;
            }

            for (int j = 0; j < n; j++) output[j] += _scratch[j];

            if (done)
            {
                RemoveAt(vi);
                v.MarkFinished();
                Finished.Enqueue(v);
            }
            else
            {
                vi++;
            }
        }

        ApplyGain(output);
        Interlocked.Increment(ref _blocks);
        return count;
    }

    private void ApplyGain(Span<float> output)
    {
        float target = Volatile.Read(ref _targetGain);
        float start = _currentGain;
        int frames = output.Length / _channels;
        if (Math.Abs(target - start) < 1e-5f)
        {
            if (Math.Abs(target - 1f) > 1e-6f)
            {
                for (int i = 0; i < output.Length; i++) output[i] *= target;
            }
        }
        else
        {
            float step = frames > 0 ? (target - start) / frames : 0f;
            float g = start;
            for (int f = 0; f < frames; f++)
            {
                g += step;
                int i = f * _channels;
                for (int c = 0; c < _channels; c++) output[i + c] *= g;
            }
        }
        _currentGain = target;
    }

    private void ApplyCommands()
    {
        while (_commands.TryDequeue(out var cmd))
        {
            switch (cmd.Kind)
            {
                case CommandKind.Play:
                    if (cmd.StopOthersFirst) FadeAll();
                    else if (cmd.StopSameKeyFirst) FadeKey(cmd.Key!);
                    if (_count == MaxVoices)
                    {
                        // Steal the oldest voice.
                        var old = _voices[0]!;
                        RemoveAt(0);
                        old.MarkFinished();
                        Finished.Enqueue(old);
                    }
                    _voices[_count] = cmd.Voice;
                    Volatile.Write(ref _count, _count + 1);
                    break;
                case CommandKind.StopKey:
                    FadeKey(cmd.Key!);
                    break;
                case CommandKind.StopVoice:
                    for (int i = 0; i < _count; i++)
                        if (ReferenceEquals(_voices[i], cmd.Voice)) BeginFade(_voices[i]!);
                    break;
                case CommandKind.StopAll:
                    FadeAll();
                    break;
            }
        }
    }

    private void FadeAll()
    {
        for (int i = 0; i < _count; i++) BeginFade(_voices[i]!);
    }

    private void FadeKey(string key)
    {
        for (int i = 0; i < _count; i++)
        {
            var v = _voices[i]!;
            if (string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)) BeginFade(v);
        }
    }

    private void BeginFade(Voice v)
    {
        if (v.FadeRemaining >= 0) return;
        v.FadeTotal = _fadeFrames;
        v.FadeRemaining = _fadeFrames;
    }

    private void RemoveAt(int index)
    {
        int last = _count - 1;
        // Preserve start order (needed for oldest-voice stealing): shift down.
        for (int i = index; i < last; i++) _voices[i] = _voices[i + 1];
        _voices[last] = null;
        Volatile.Write(ref _count, last);
    }
}
