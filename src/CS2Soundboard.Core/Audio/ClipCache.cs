using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CS2Soundboard.Core.Audio;

/// <summary>
/// Caches decoded clips per (file version, engine format) so hotkey triggers start instantly.
/// Files longer than <see cref="MaxCachedDuration"/> or exceeding the memory budget are not cached;
/// callers should stream them with <see cref="StreamingVoice"/> instead (result is null).
/// </summary>
public sealed class ClipCache
{
    private readonly record struct ClipKey(string Path, long Length, DateTime WriteUtc, int Rate, int Channels);

    private readonly ConcurrentDictionary<ClipKey, Lazy<Task<AudioClip?>>> _entries = new();
    private readonly SemaphoreSlim _decodeGate = new(2);
    private long _bytes;

    public ClipCache(long maxBytes = 512L * 1024 * 1024, TimeSpan? maxCachedDuration = null)
    {
        MaxBytes = maxBytes;
        MaxCachedDuration = maxCachedDuration ?? TimeSpan.FromSeconds(90);
    }

    public long MaxBytes { get; }
    public TimeSpan MaxCachedDuration { get; }
    public long CachedBytes => Interlocked.Read(ref _bytes);

    private static ClipKey MakeKey(string path, int rate, int channels)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new AudioDecodeException($"File not found: {path}", new FileNotFoundException(null, path));
        return new ClipKey(fi.FullName.ToLowerInvariant(), fi.Length, fi.LastWriteTimeUtc, rate, channels);
    }

    /// <summary>Fast path: returns a ready clip without blocking, if one is cached.</summary>
    public bool TryGetReady(string path, int rate, int channels, out AudioClip? clip)
    {
        clip = null;
        try
        {
            var key = MakeKey(path, rate, channels);
            if (_entries.TryGetValue(key, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
            {
                clip = lazy.Value.Result;
                return clip != null;
            }
        }
        catch { /* missing file etc. - caller will take slow path and report */ }
        return false;
    }

    /// <summary>
    /// Returns the decoded clip, or null when the file should be streamed instead.
    /// Throws <see cref="AudioDecodeException"/> on missing or undecodable files.
    /// </summary>
    public async Task<AudioClip?> GetOrLoadAsync(string path, int rate, int channels, CancellationToken ct = default)
    {
        var key = MakeKey(path, rate, channels);
        var lazy = _entries.GetOrAdd(key, k => new Lazy<Task<AudioClip?>>(() => LoadAsync(k, path)));
        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            // Allow a retry later (e.g. after the file is fixed).
            _entries.TryRemove(new KeyValuePair<ClipKey, Lazy<Task<AudioClip?>>>(key, lazy));
            throw;
        }
    }

    private async Task<AudioClip?> LoadAsync(ClipKey key, string path)
    {
        await _decodeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var duration = AudioDecoder.GetDuration(path);
                long estimatedBytes = (long)(duration.TotalSeconds * key.Rate * key.Channels * sizeof(float));
                if (duration > MaxCachedDuration || CachedBytes + estimatedBytes > MaxBytes)
                    return null; // stream it

                var clip = AudioDecoder.DecodeToClip(path, key.Rate, key.Channels);
                Interlocked.Add(ref _bytes, clip.SizeBytes);
                return clip;
            }).ConfigureAwait(false);
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    /// <summary>Drops cache entries for files not in <paramref name="paths"/> or whose content changed.</summary>
    public void Retain(IEnumerable<string> paths)
    {
        var keep = new HashSet<string>(paths.Select(p => Path.GetFullPath(p).ToLowerInvariant()));
        foreach (var kv in _entries.ToArray())
        {
            bool remove = !keep.Contains(kv.Key.Path);
            if (!remove)
            {
                var fi = new FileInfo(kv.Key.Path);
                remove = !fi.Exists || fi.Length != kv.Key.Length || fi.LastWriteTimeUtc != kv.Key.WriteUtc;
            }
            if (remove) Remove(kv.Key, kv.Value);
        }
    }

    /// <summary>Drops entries for formats other than the ones currently in use.</summary>
    public void RetainFormats(params (int Rate, int Channels)[] formats)
    {
        foreach (var kv in _entries.ToArray())
        {
            if (!formats.Any(f => f.Rate == kv.Key.Rate && f.Channels == kv.Key.Channels))
                Remove(kv.Key, kv.Value);
        }
    }

    private void Remove(ClipKey key, Lazy<Task<AudioClip?>> value)
    {
        if (_entries.TryRemove(new KeyValuePair<ClipKey, Lazy<Task<AudioClip?>>>(key, value)) &&
            value.IsValueCreated && value.Value.IsCompletedSuccessfully && value.Value.Result is { } clip)
        {
            Interlocked.Add(ref _bytes, -clip.SizeBytes);
        }
    }

    public void Clear()
    {
        foreach (var kv in _entries.ToArray()) Remove(kv.Key, kv.Value);
    }
}
