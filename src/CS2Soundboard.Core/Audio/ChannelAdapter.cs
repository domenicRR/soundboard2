using System;
using NAudio.Wave;

namespace CS2Soundboard.Core.Audio;

/// <summary>
/// Converts an ISampleProvider between channel counts:
/// mono to all channels, N to mono (average), N to M (first channels copied, extras silent).
/// </summary>
public sealed class ChannelAdapter : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _in;
    private readonly int _out;
    private float[] _scratch = Array.Empty<float>();

    public ChannelAdapter(ISampleProvider source, int outputChannels)
    {
        _source = source;
        _in = source.WaveFormat.Channels;
        _out = outputChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outputChannels);
    }

    public WaveFormat WaveFormat { get; }

    public static ISampleProvider Wrap(ISampleProvider source, int outputChannels)
        => source.WaveFormat.Channels == outputChannels ? source : new ChannelAdapter(source, outputChannels);

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _out;
        int need = frames * _in;
        if (_scratch.Length < need) _scratch = new float[need];
        int read = _source.Read(_scratch, 0, need);
        int framesRead = read / _in;

        for (int f = 0; f < framesRead; f++)
        {
            int si = f * _in;
            int di = offset + f * _out;
            if (_in == 1)
            {
                float s = _scratch[si];
                for (int c = 0; c < _out; c++) buffer[di + c] = s;
            }
            else if (_out == 1)
            {
                float sum = 0f;
                for (int c = 0; c < _in; c++) sum += _scratch[si + c];
                buffer[di] = sum / _in;
            }
            else
            {
                for (int c = 0; c < _out; c++) buffer[di + c] = c < _in ? _scratch[si + c] : 0f;
            }
        }
        return framesRead * _out;
    }
}
