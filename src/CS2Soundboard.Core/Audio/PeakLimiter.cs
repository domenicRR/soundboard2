using System;

namespace CS2Soundboard.Core.Audio;

/// <summary>
/// Simple instant-attack / smooth-release peak limiter with a hard safety clamp.
/// Prevents clipping when voice and multiple sound effects overlap.
/// </summary>
public sealed class PeakLimiter
{
    private readonly float _ceiling;
    private readonly float _releaseCoef;
    private float _gain = 1f;

    public PeakLimiter(int sampleRate, float ceilingDb = -1f, float releaseMs = 120f)
    {
        _ceiling = (float)Math.Pow(10, ceilingDb / 20.0);
        _releaseCoef = (float)(1.0 - Math.Exp(-1.0 / (releaseMs * 0.001 * sampleRate)));
    }

    public float Ceiling => _ceiling;

    /// <summary>Current gain applied by the limiter (1 = no reduction).</summary>
    public float CurrentGain => _gain;

    public void Process(Span<float> interleaved, int channels)
    {
        int frames = interleaved.Length / channels;
        float gain = _gain;
        for (int f = 0; f < frames; f++)
        {
            int i = f * channels;
            float peak = 0f;
            for (int c = 0; c < channels; c++)
            {
                float a = Math.Abs(interleaved[i + c]);
                if (a > peak) peak = a;
            }

            gain += (1f - gain) * _releaseCoef;
            if (peak * gain > _ceiling) gain = _ceiling / peak;

            for (int c = 0; c < channels; c++)
            {
                float v = interleaved[i + c] * gain;
                if (float.IsNaN(v)) v = 0f;
                else if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                interleaved[i + c] = v;
            }
        }
        if (!float.IsFinite(gain)) gain = 1f;
        _gain = gain;
    }
}
