using System;

namespace CS2Soundboard.Core.Audio;

public static class SignalAnalysis
{
    /// <summary>
    /// Estimates the amplitude of a sine at <paramref name="frequency"/> using the Goertzel algorithm.
    /// Returns the largest estimate over consecutive windows (robust to the tone occupying only part of the data).
    /// </summary>
    public static double ToneAmplitude(ReadOnlySpan<float> mono, int sampleRate, double frequency, int window = 2048)
    {
        double best = 0;
        for (int start = 0; start + window <= mono.Length; start += window / 2)
        {
            best = Math.Max(best, Goertzel(mono.Slice(start, window), sampleRate, frequency));
        }
        if (mono.Length < window && mono.Length > 64) best = Goertzel(mono, sampleRate, frequency);
        return best;
    }

    private static double Goertzel(ReadOnlySpan<float> x, int sampleRate, double frequency)
    {
        // Exact-frequency Goertzel with a Hann window: no bin rounding (scalloping) error,
        // and low leakage from other tones/voice.
        int n = x.Length;
        double w = 2 * Math.PI * frequency / sampleRate;
        double coeff = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0, windowSum = 0;
        for (int i = 0; i < n; i++)
        {
            double h = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
            windowSum += h;
            double s0 = x[i] * h + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
        return 2 * Math.Sqrt(Math.Max(0, power)) / windowSum;
    }

    public static float Peak(ReadOnlySpan<float> x)
    {
        float p = 0;
        foreach (var v in x) p = Math.Max(p, Math.Abs(v));
        return p;
    }

    public static double Rms(ReadOnlySpan<float> x)
    {
        if (x.Length == 0) return 0;
        double sum = 0;
        foreach (var v in x) sum += v * v;
        return Math.Sqrt(sum / x.Length);
    }
}
