using System;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace CS2Soundboard.Core.Audio;

/// <summary>Allocation-free conversion of raw capture buffers into float samples.</summary>
public static class SampleConverter
{
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");

    public static bool IsFloat(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat) return true;
        return format is WaveFormatExtensible ext && ext.Encoding == WaveFormatEncoding.Extensible && ext.SubFormat == FloatSubtype;
    }

    public static bool IsPcm(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.Pcm) return true;
        return format is WaveFormatExtensible ext && ext.Encoding == WaveFormatEncoding.Extensible && ext.SubFormat == PcmSubtype;
    }

    public static bool IsSupported(WaveFormat format)
    {
        int b = format.BitsPerSample;
        if (IsFloat(format)) return b == 32 || b == 64;
        if (IsPcm(format)) return b == 8 || b == 16 || b == 24 || b == 32;
        return false;
    }

    public static string Describe(WaveFormat f)
        => $"{f.SampleRate} Hz, {f.Channels} ch, {f.BitsPerSample}-bit {(IsFloat(f) ? "float" : IsPcm(f) ? "PCM" : f.Encoding.ToString())}";

    /// <summary>Number of float samples produced by <paramref name="bytes"/> of input.</summary>
    public static int SampleCount(int bytes, WaveFormat format) => bytes / (format.BitsPerSample / 8);

    /// <summary>Converts interleaved raw bytes to interleaved floats in [-1,1]. Returns samples written.</summary>
    public static int ToFloat(byte[] source, int bytes, WaveFormat format, Span<float> dest)
    {
        int bits = format.BitsPerSample;
        int samples = SampleCount(bytes, format);
        if (dest.Length < samples) throw new ArgumentException("Destination too small", nameof(dest));
        var src = source.AsSpan(0, samples * (bits / 8));

        if (IsFloat(format))
        {
            if (bits == 32)
            {
                MemoryMarshal.Cast<byte, float>(src).CopyTo(dest);
            }
            else if (bits == 64)
            {
                var d = MemoryMarshal.Cast<byte, double>(src);
                for (int i = 0; i < samples; i++) dest[i] = (float)d[i];
            }
            else throw new NotSupportedException($"Unsupported float format: {Describe(format)}");
        }
        else if (IsPcm(format))
        {
            switch (bits)
            {
                case 8:
                    for (int i = 0; i < samples; i++) dest[i] = (src[i] - 128) / 128f;
                    break;
                case 16:
                    var s16 = MemoryMarshal.Cast<byte, short>(src);
                    for (int i = 0; i < samples; i++) dest[i] = s16[i] / 32768f;
                    break;
                case 24:
                    for (int i = 0, j = 0; i < samples; i++, j += 3)
                    {
                        int v = src[j] | (src[j + 1] << 8) | ((sbyte)src[j + 2] << 16);
                        dest[i] = v / 8388608f;
                    }
                    break;
                case 32:
                    var s32 = MemoryMarshal.Cast<byte, int>(src);
                    for (int i = 0; i < samples; i++) dest[i] = s32[i] / 2147483648f;
                    break;
                default:
                    throw new NotSupportedException($"Unsupported PCM format: {Describe(format)}");
            }
        }
        else
        {
            throw new NotSupportedException($"Unsupported sample format: {Describe(format)}");
        }

        // Guard against NaN/Inf from a misbehaving driver.
        for (int i = 0; i < samples; i++)
        {
            if (!float.IsFinite(dest[i])) dest[i] = 0f;
        }
        return samples;
    }

    /// <summary>Averages interleaved channels into mono. Returns frames written.</summary>
    public static int DownmixToMono(ReadOnlySpan<float> interleaved, int channels, Span<float> mono)
    {
        int frames = interleaved.Length / channels;
        if (channels == 1)
        {
            interleaved.Slice(0, frames).CopyTo(mono);
            return frames;
        }
        float scale = 1f / channels;
        for (int f = 0, i = 0; f < frames; f++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++) sum += interleaved[i++];
            mono[f] = sum * scale;
        }
        return frames;
    }
}
