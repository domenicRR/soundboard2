using System;

namespace CS2Soundboard.Core.Audio;

/// <summary>A fully decoded sound in a specific engine format (interleaved float).</summary>
public sealed class AudioClip
{
    public AudioClip(float[] samples, int sampleRate, int channels, string sourcePath = "")
    {
        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
        SourcePath = sourcePath;
    }

    public float[] Samples { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public string SourcePath { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / Channels / SampleRate);
    public long SizeBytes => (long)Samples.Length * sizeof(float);

    /// <summary>Creates a sine test tone with short fades to avoid clicks.</summary>
    public static AudioClip CreateTone(int sampleRate, int channels, double frequency, double seconds, float amplitude)
    {
        int frames = (int)(sampleRate * seconds);
        var data = new float[frames * channels];
        int fade = Math.Min(frames / 2, sampleRate / 100); // 10 ms
        for (int f = 0; f < frames; f++)
        {
            float env = 1f;
            if (f < fade) env = (float)f / fade;
            else if (f > frames - fade) env = (float)(frames - f) / fade;
            float s = (float)(Math.Sin(2 * Math.PI * frequency * f / sampleRate) * amplitude * env);
            for (int c = 0; c < channels; c++) data[f * channels + c] = s;
        }
        return new AudioClip(data, sampleRate, channels, $"tone:{frequency}Hz");
    }
}
