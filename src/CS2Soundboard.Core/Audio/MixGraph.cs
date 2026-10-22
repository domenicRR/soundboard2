using System;
using System.Threading;
using NAudio.Wave;

namespace CS2Soundboard.Core.Audio;

/// <summary>
/// The virtual-microphone mix: live mic (continuous) + sound-effect bus, then master gain,
/// peak limiter and metering. Pulled by the output device on its real-time thread.
///
///   mic (mono, engine rate) --gain/mute--+
///                                        +--> sum --> master --> limiter --> meter --> output (+ optional tap)
///   SoundBus (engine format) --vol/mute--+
///
/// The mic path is always rendered, whether or not any sound is playing.
/// </summary>
public sealed class MixGraph : ISampleProvider
{
    private readonly MixerParameters _params;
    private readonly int _channels;
    private readonly PeakLimiter _limiter;
    private ISampleProvider? _mic;
    private BufferedFloatProvider? _tap;
    private float[] _micBuffer = Array.Empty<float>();
    private float[] _busBuffer = Array.Empty<float>();
    private float _micGainCurrent;
    private float _masterCurrent;
    private long _reads;

    public MixGraph(WaveFormat engineFormat, MixerParameters parameters)
    {
        if (engineFormat.Encoding != WaveFormatEncoding.IeeeFloat) throw new ArgumentException("Engine format must be IEEE float");
        WaveFormat = engineFormat;
        _params = parameters;
        _channels = engineFormat.Channels;
        Bus = new SoundBus(engineFormat);
        _limiter = new PeakLimiter(engineFormat.SampleRate);
        _micGainCurrent = parameters.MicMuted ? 0f : parameters.MicGain;
        _masterCurrent = parameters.MasterGain;
        Bus.TargetGain = parameters.SoundboardMuted ? 0f : parameters.SoundboardGain;
    }

    public WaveFormat WaveFormat { get; }
    public SoundBus Bus { get; }
    public PeakMeter OutputMeter { get; } = new();
    public PeakMeter VoiceMeter { get; } = new();
    public float LimiterGain => _limiter.CurrentGain;
    public long ReadCalls => Interlocked.Read(ref _reads);

    /// <summary>True when a microphone source is attached.</summary>
    public bool HasMicSource => Volatile.Read(ref _mic) != null;

    /// <summary>Attach the mic chain: must be mono at the engine sample rate. Null detaches.</summary>
    public void SetMicSource(ISampleProvider? monoAtEngineRate)
    {
        if (monoAtEngineRate != null &&
            (monoAtEngineRate.WaveFormat.Channels != 1 || monoAtEngineRate.WaveFormat.SampleRate != WaveFormat.SampleRate))
            throw new ArgumentException("Mic source must be mono at the engine sample rate");
        Volatile.Write(ref _mic, monoAtEngineRate);
    }

    /// <summary>Builds the mic chain for this graph: mono capture stream, resampled to the engine rate if needed.</summary>
    public ISampleProvider BuildMicChain(ISampleProvider monoCapture)
    {
        if (monoCapture.WaveFormat.Channels != 1) throw new ArgumentException("Mic capture stream must be mono");
        return monoCapture.WaveFormat.SampleRate == WaveFormat.SampleRate
            ? monoCapture
            : new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(monoCapture, WaveFormat.SampleRate);
    }

    /// <summary>Optional copy of the final mix (engine format) for local monitoring.</summary>
    public BufferedFloatProvider? OutputTap
    {
        get => Volatile.Read(ref _tap);
        set => Volatile.Write(ref _tap, value);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _channels;
        count = frames * _channels;
        var output = buffer.AsSpan(offset, count);

        if (_micBuffer.Length < frames) _micBuffer = new float[frames];
        if (_busBuffer.Length < count) _busBuffer = new float[count];

        // 1. Live microphone (always rendered when attached).
        var mic = Volatile.Read(ref _mic);
        int micRead = 0;
        if (mic != null)
        {
            try { micRead = mic.Read(_micBuffer, 0, frames); }
            catch { micRead = 0; }
        }
        if (micRead < frames) Array.Clear(_micBuffer, micRead, frames - micRead);

        float micTarget = _params.MicMuted ? 0f : _params.MicGain;
        float micStep = (micTarget - _micGainCurrent) / Math.Max(1, frames);
        float g = _micGainCurrent;
        for (int f = 0; f < frames; f++)
        {
            g += micStep;
            float s = _micBuffer[f] * g;
            _micBuffer[f] = s;
            int i = f * _channels;
            for (int c = 0; c < _channels; c++) output[i + c] = s;
        }
        _micGainCurrent = micTarget;
        VoiceMeter.Update(_micBuffer.AsSpan(0, frames));

        // 2. Sound effects.
        Bus.TargetGain = _params.SoundboardMuted ? 0f : _params.SoundboardGain;
        Bus.Read(_busBuffer, 0, count);
        for (int i = 0; i < count; i++) output[i] += _busBuffer[i];

        // 3. Master gain (smoothed).
        float masterTarget = _params.MasterGain;
        float mStep = (masterTarget - _masterCurrent) / Math.Max(1, frames);
        float m = _masterCurrent;
        for (int f = 0; f < frames; f++)
        {
            m += mStep;
            int i = f * _channels;
            for (int c = 0; c < _channels; c++) output[i + c] *= m;
        }
        _masterCurrent = masterTarget;

        // 4. Clipping protection.
        _limiter.Process(output, _channels);

        // 5. Metering and optional monitor tap.
        OutputMeter.Update(output);
        OutputTap?.Write(output);

        Interlocked.Increment(ref _reads);
        return count;
    }
}
