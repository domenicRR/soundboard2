using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CS2Soundboard.Core.Settings;
using NAudio.Wave;

namespace CS2Soundboard.Core.Audio;

public sealed class ActiveVoice
{
    public ActiveVoice(Voice voice, SoundBus bus, bool isLocal, bool isPreview)
    {
        Voice = voice;
        Bus = bus;
        IsLocal = isLocal;
        IsPreview = isPreview;
    }

    public Voice Voice { get; }
    public SoundBus Bus { get; }
    public bool IsLocal { get; }
    public bool IsPreview { get; }
    public string Key => Voice.Key;
}

/// <summary>
/// Starts, stops and tracks sound voices on the virtual-mic bus and the local bus.
/// Intended to be used from a single (UI) thread; the audio threads are only reached through
/// the lock-free bus command queues.
/// </summary>
public sealed class PlaybackController
{
    private readonly Func<MixGraph?> _graph;
    private readonly Func<Task<LocalGraph?>> _ensureLocal;
    private readonly Func<LocalGraph?> _local;
    private readonly ClipCache _cache;
    private readonly List<ActiveVoice> _active = new();

    public PlaybackController(Func<MixGraph?> graph, Func<LocalGraph?> local, Func<Task<LocalGraph?>> ensureLocal, ClipCache cache)
    {
        _graph = graph;
        _local = local;
        _ensureLocal = ensureLocal;
        _cache = cache;
    }

    public PlaybackController(AudioService audio, ClipCache cache)
        : this(() => audio.Graph, () => audio.Local, audio.EnsureLocalAsync, cache) { }

    public IReadOnlyList<ActiveVoice> Active => _active;

    public bool IsPlaying(string key) => _active.Any(a => !a.IsPreview && !a.Voice.IsFinished && Same(a.Key, key));
    public bool IsPreviewing(string key) => _active.Any(a => a.IsPreview && !a.Voice.IsFinished && Same(a.Key, key));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Plays a sound into the virtual microphone mix (and optionally on local speakers).
    /// Returns a user-facing error, or null on success.
    /// </summary>
    public async Task<string?> TriggerAsync(string key, string path, string name, PlaybackMode mode, bool retriggerStops, bool hearLocally)
    {
        if (retriggerStops && IsPlaying(key))
        {
            Stop(key);
            return null;
        }

        string? error = null;
        var graph = _graph();
        if (graph == null)
        {
            error = "Not sent to CS2: the virtual microphone output is not running (see the Audio Mixer tab).";
        }
        else
        {
            var voice = await CreateVoiceAsync(path, graph.WaveFormat, key, name).ConfigureAwait(true);
            if (!ReferenceEquals(graph, _graph()))
            {
                voice.Dispose(); // engine restarted while loading
                error = "The audio engine restarted while the sound was loading. Try again.";
            }
            else
            {
                graph.Bus.Play(voice, mode == PlaybackMode.StopPrevious, mode == PlaybackMode.NoDuplicates);
                _active.Add(new ActiveVoice(voice, graph.Bus, false, false));
            }
        }

        if (hearLocally)
        {
            var local = _local() ?? await _ensureLocal().ConfigureAwait(true);
            if (local != null)
            {
                var lv = await CreateVoiceAsync(path, local.WaveFormat, key, name).ConfigureAwait(true);
                // On the local bus, StopPrevious/NoDuplicates only affect non-preview copies via key/all semantics.
                local.Bus.Play(lv, mode == PlaybackMode.StopPrevious, mode == PlaybackMode.NoDuplicates);
                _active.Add(new ActiveVoice(lv, local.Bus, true, false));
            }
        }
        return error;
    }

    /// <summary>Plays (or stops, if already previewing) a sound on local speakers only.</summary>
    public async Task<string?> PreviewAsync(string key, string path, string name)
    {
        var existing = _active.Where(a => a.IsPreview && !a.Voice.IsFinished && Same(a.Key, key)).ToList();
        if (existing.Count > 0)
        {
            foreach (var a in existing) a.Bus.Stop(a.Voice);
            return null;
        }
        var local = await _ensureLocal().ConfigureAwait(true);
        if (local == null) return null; // caller reads the status error
        var voice = await CreateVoiceAsync(path, local.WaveFormat, key, name).ConfigureAwait(true);
        local.Bus.Play(voice);
        _active.Add(new ActiveVoice(voice, local.Bus, true, true));
        return null;
    }

    /// <summary>Plays a short tone into the virtual microphone mix (diagnostics).</summary>
    public string? PlayTestTone()
    {
        var graph = _graph();
        if (graph == null) return "The virtual microphone output is not running.";
        var tone = AudioClip.CreateTone(graph.WaveFormat.SampleRate, graph.WaveFormat.Channels, 440, 0.7, 0.3f);
        var voice = new CachedVoice(tone, "__testtone__", "Test tone");
        graph.Bus.Play(voice);
        _active.Add(new ActiveVoice(voice, graph.Bus, false, false));
        return null;
    }

    private async Task<Voice> CreateVoiceAsync(string path, WaveFormat format, string key, string name)
    {
        if (_cache.TryGetReady(path, format.SampleRate, format.Channels, out var ready) && ready != null)
            return new CachedVoice(ready, key, name);

        var clip = await _cache.GetOrLoadAsync(path, format.SampleRate, format.Channels).ConfigureAwait(true);
        if (clip != null) return new CachedVoice(clip, key, name);
        return await StreamingVoice.StartAsync(path, format.SampleRate, format.Channels, key, name).ConfigureAwait(true);
    }

    public void Stop(string key)
    {
        foreach (var a in _active.Where(a => !a.IsPreview && Same(a.Key, key)))
            a.Bus.Stop(a.Voice);
    }

    public void StopAll()
    {
        _graph()?.Bus.StopAll();
        _local()?.Bus.StopAll();
    }

    /// <summary>Stops the most recently started (non-preview) sound.</summary>
    public string? StopLast()
    {
        var last = _active.LastOrDefault(a => !a.IsPreview && !a.Voice.IsFinished && a.Key != "__testtone__");
        if (last == null) return null;
        Stop(last.Key);
        return last.Voice.DisplayName;
    }

    /// <summary>
    /// Housekeeping: disposes finished voices and forgets voices whose bus no longer exists
    /// (device restarted). Returns true if anything changed.
    /// </summary>
    public bool Pump()
    {
        var graphBus = _graph()?.Bus;
        var localBus = _local()?.Bus;
        DrainFinished(graphBus);
        DrainFinished(localBus);

        int removed = 0;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var a = _active[i];
            bool orphan = !ReferenceEquals(a.Bus, graphBus) && !ReferenceEquals(a.Bus, localBus);
            if (a.Voice.IsFinished || orphan)
            {
                if (orphan) a.Voice.Dispose();
                _active.RemoveAt(i);
                removed++;
            }
        }
        return removed > 0;
    }

    private static void DrainFinished(SoundBus? bus)
    {
        if (bus == null) return;
        while (bus.Finished.TryDequeue(out var v)) v.Dispose();
    }

    /// <summary>The most recent non-preview sound still playing, for "Now playing" displays.</summary>
    public IEnumerable<string> NowPlayingNames()
        => _active.Where(a => !a.IsPreview && !a.IsLocal && !a.Voice.IsFinished && a.Voice.FadeRemaining < 0)
                  .Select(a => a.Voice.DisplayName)
                  .Distinct();
}
