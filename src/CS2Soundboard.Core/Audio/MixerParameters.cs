using System;
using System.Threading;

namespace CS2Soundboard.Core.Audio;

/// <summary>
/// Thread-safe mixer controls shared between the UI and the audio thread.
/// Survives device restarts (graphs are rebuilt, parameters are not).
/// </summary>
public sealed class MixerParameters
{
    private float _micGain = 1f;
    private float _soundboardGain = 0.8f;
    private float _masterGain = 1f;
    private int _micMuted;
    private int _soundboardMuted;

    public const float MaxMicGain = 4f;        // +12 dB
    public const float MaxSoundboardGain = 2f; // +6 dB
    public const float MaxMasterGain = 2f;     // +6 dB

    public float MicGain
    {
        get => Volatile.Read(ref _micGain);
        set => Volatile.Write(ref _micGain, Math.Clamp(value, 0f, MaxMicGain));
    }

    public float SoundboardGain
    {
        get => Volatile.Read(ref _soundboardGain);
        set => Volatile.Write(ref _soundboardGain, Math.Clamp(value, 0f, MaxSoundboardGain));
    }

    public float MasterGain
    {
        get => Volatile.Read(ref _masterGain);
        set => Volatile.Write(ref _masterGain, Math.Clamp(value, 0f, MaxMasterGain));
    }

    public bool MicMuted
    {
        get => Volatile.Read(ref _micMuted) == 1;
        set => Volatile.Write(ref _micMuted, value ? 1 : 0);
    }

    public bool SoundboardMuted
    {
        get => Volatile.Read(ref _soundboardMuted) == 1;
        set => Volatile.Write(ref _soundboardMuted, value ? 1 : 0);
    }
}
