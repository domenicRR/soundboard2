using System;
using System.Threading;

namespace CS2Soundboard.Core.Audio;

/// <summary>Peak-hold meter written by the audio thread and polled (read and reset) by the UI.</summary>
public sealed class PeakMeter
{
    private float _peak;

    public void Update(ReadOnlySpan<float> samples)
    {
        float p = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float a = Math.Abs(samples[i]);
            if (a > p) p = a;
        }
        if (p > Volatile.Read(ref _peak)) Volatile.Write(ref _peak, p);
    }

    public float ReadAndReset() => Interlocked.Exchange(ref _peak, 0f);

    public static double ToDb(float linear) => linear <= 1e-6f ? -120 : 20 * Math.Log10(linear);
}
