using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CS2Soundboard.Core.Audio;

public sealed class AudioDecodeException : Exception
{
    public AudioDecodeException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Opens and decodes sound files and converts them to an engine format.</summary>
public static class AudioDecoder
{
    /// <summary>Extensions decoded by NAudio directly (WAV, MP3, AIFF) or NVorbis (OGG).</summary>
    public static readonly IReadOnlySet<string> PrimaryExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".ogg", ".aiff", ".aif" };

    /// <summary>Extensions decoded through Windows Media Foundation (depends on installed codecs).</summary>
    public static readonly IReadOnlySet<string> MediaFoundationExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".flac", ".m4a", ".aac", ".wma" };

    public static bool IsSupportedExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return PrimaryExtensions.Contains(ext) || MediaFoundationExtensions.Contains(ext);
    }

    /// <summary>Opens a reader. Throws <see cref="AudioDecodeException"/> with a user-friendly message on failure.</summary>
    public static WaveStream OpenReader(string path)
    {
        if (!File.Exists(path)) throw new AudioDecodeException($"File not found: {path}", new FileNotFoundException(null, path));
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            switch (ext)
            {
                case ".ogg":
                    return new VorbisWaveReader(path);
                case ".wav":
                case ".mp3":
                case ".aiff":
                case ".aif":
                    try
                    {
                        return new AudioFileReader(path);
                    }
                    catch (Exception first) when (ext == ".mp3" || ext == ".wav")
                    {
                        // Some MP3/WAV variants (e.g. odd headers, compressed WAV) are handled better by Media Foundation.
                        try { return new MediaFoundationReader(path); }
                        catch { throw first; }
                    }
                default:
                    return new MediaFoundationReader(path);
            }
        }
        catch (AudioDecodeException) { throw; }
        catch (Exception ex)
        {
            throw new AudioDecodeException($"Cannot decode '{Path.GetFileName(path)}': {Simplify(ex)}", ex);
        }
    }

    private static string Simplify(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access denied",
        IOException io => io.Message,
        System.Runtime.InteropServices.COMException => "unsupported or corrupt file (no suitable Windows codec)",
        _ => string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message,
    };

    /// <summary>Wraps a reader so it produces float samples at the requested rate and channel count.</summary>
    public static ISampleProvider ToEngineFormat(WaveStream reader, int sampleRate, int channels)
    {
        ISampleProvider sp = reader as ISampleProvider ?? reader.ToSampleProvider();
        sp = ChannelAdapter.Wrap(sp, channels);
        if (sp.WaveFormat.SampleRate != sampleRate)
            sp = new WdlResamplingSampleProvider(sp, sampleRate);
        return sp;
    }

    public static TimeSpan GetDuration(string path)
    {
        using var reader = OpenReader(path);
        return reader.TotalTime;
    }

    /// <summary>Decodes an entire file into memory in the given engine format.</summary>
    public static AudioClip DecodeToClip(string path, int sampleRate, int channels, CancellationToken ct = default)
    {
        using var reader = OpenReader(path);
        try
        {
            var sp = ToEngineFormat(reader, sampleRate, channels);
            long estimate = (long)(reader.TotalTime.TotalSeconds * sampleRate * channels) + sampleRate * channels;
            if (estimate <= 0 || estimate > int.MaxValue / 2) estimate = sampleRate * channels * 10L;
            var data = new float[estimate];
            int total = 0;
            var chunk = new float[sampleRate * channels / 4]; // 250 ms
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int n = sp.Read(chunk, 0, chunk.Length);
                if (n <= 0) break;
                if (total + n > data.Length)
                {
                    long newSize = Math.Max((long)data.Length * 2, total + n);
                    if (newSize > int.MaxValue / 2) throw new AudioDecodeException($"'{Path.GetFileName(path)}' is too large to load into memory.");
                    Array.Resize(ref data, (int)newSize);
                }
                Array.Copy(chunk, 0, data, total, n);
                total += n;
            }
            total -= total % channels;
            if (total == 0) throw new AudioDecodeException($"'{Path.GetFileName(path)}' contains no audio.");
            Array.Resize(ref data, total);
            return new AudioClip(data, sampleRate, channels, path);
        }
        catch (AudioDecodeException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new AudioDecodeException($"Error while decoding '{Path.GetFileName(path)}': {Simplify(ex)}", ex);
        }
    }
}
