using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CS2Soundboard.Core.Audio;
using NAudio.Wave;
using Xunit;

namespace CS2Soundboard.Tests;

/// <summary>
/// Offline simulations of the real-time pipeline: a synthetic "microphone" is pushed through
/// CaptureSink (as the WASAPI capture thread would) while the MixGraph is pulled block by block
/// (as the WASAPI render thread would). Tones at distinct frequencies identify voice vs effects.
/// </summary>
public class MixerScenarioTests
{
    private const double VoiceHz = 300;
    private const double EffectAHz = 1000;
    private const double EffectBHz = 1700;
    private const float VoiceAmp = 0.2f;
    private const float EffectAmp = 0.2f;

    private sealed class Rig
    {
        public readonly MixerParameters Params = new() { MicGain = 1f, SoundboardGain = 1f, MasterGain = 1f };
        public readonly MixGraph Graph;
        public readonly CaptureSink Mic;
        private readonly WaveFormat _micFormat;
        private readonly int _engineRate;
        private readonly int _engineChannels;
        private long _micFrame;
        public bool MicConnected = true;

        public Rig(int engineRate = 48000, int engineChannels = 2, WaveFormat? micFormat = null)
        {
            _engineRate = engineRate;
            _engineChannels = engineChannels;
            _micFormat = micFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
            Graph = new MixGraph(WaveFormat.CreateIeeeFloatWaveFormat(engineRate, engineChannels), Params);
            Mic = new CaptureSink(_micFormat, provideOutput: true);
            Graph.SetMicSource(Graph.BuildMicChain(Mic.Output!));
            // Prime the jitter buffer like a real capture stream would before the first render.
            for (int i = 0; i < 4; i++) PushMic();
        }

        private void PushMic()
        {
            if (!MicConnected) return;
            int frames = _micFormat.SampleRate / 100; // 10 ms
            var bytes = Synth.Encode(frames, _micFormat, f => (float)(VoiceAmp * Math.Sin(2 * Math.PI * VoiceHz * (_micFrame + f) / _micFormat.SampleRate)));
            _micFrame += frames;
            Mic.OnData(bytes, bytes.Length);
        }

        /// <summary>Runs the pipeline and returns channel 0 of the output.</summary>
        public float[] Run(double seconds, Action<int>? beforeBlock = null)
        {
            int blocks = (int)(seconds * 100);
            int framesPerBlock = _engineRate / 100;
            var block = new float[framesPerBlock * _engineChannels];
            var result = new List<float>(blocks * framesPerBlock);
            for (int b = 0; b < blocks; b++)
            {
                beforeBlock?.Invoke(b);
                PushMic();
                int n = Graph.Read(block, 0, block.Length);
                Assert.Equal(block.Length, n); // the mixer must never stall
                for (int f = 0; f < framesPerBlock; f++) result.Add(block[f * _engineChannels]);
            }
            return result.ToArray();
        }

        public CachedVoice Tone(double hz, double seconds, string key)
            => new(AudioClip.CreateTone(_engineRate, _engineChannels, hz, seconds, EffectAmp), key, key);
    }

    private static double Amp(float[] x, int rate, double hz, double fromSec, double toSec)
    {
        int a = (int)(fromSec * rate), b = Math.Min(x.Length, (int)(toSec * rate));
        return SignalAnalysis.ToneAmplitude(x.AsSpan(a, b - a), rate, hz, 4096);
    }

    [Fact]
    public void Idle_MicIsForwardedContinuously()
    {
        var rig = new Rig();
        var output = rig.Run(1.0);
        double voice = Amp(output, 48000, VoiceHz, 0.2, 1.0);
        Assert.InRange(voice, VoiceAmp * 0.85, VoiceAmp * 1.15);
        Assert.Equal(0, rig.Graph.Bus.ActiveVoices);
    }

    [Fact]
    public void VoiceAndEffect_AreBothPresent()
    {
        var rig = new Rig();
        var output = rig.Run(1.2, b => { if (b == 20) rig.Graph.Bus.Play(rig.Tone(EffectAHz, 0.8, "a")); });
        Assert.InRange(Amp(output, 48000, VoiceHz, 0.3, 0.9), VoiceAmp * 0.85, VoiceAmp * 1.15);
        Assert.InRange(Amp(output, 48000, EffectAHz, 0.3, 0.9), EffectAmp * 0.85, EffectAmp * 1.15);
        // Before the effect started, only the voice is present.
        Assert.True(Amp(output, 48000, EffectAHz, 0.0, 0.18) < 0.01);
    }

    [Fact]
    public void OverlappingEffects_KeepVoiceAudible()
    {
        var rig = new Rig();
        var output = rig.Run(1.2, b =>
        {
            if (b == 10) rig.Graph.Bus.Play(rig.Tone(EffectAHz, 1.0, "a"));
            if (b == 20) rig.Graph.Bus.Play(rig.Tone(EffectBHz, 0.9, "b"));
        });
        Assert.InRange(Amp(output, 48000, VoiceHz, 0.3, 1.0), VoiceAmp * 0.85, VoiceAmp * 1.15);
        Assert.InRange(Amp(output, 48000, EffectAHz, 0.3, 1.0), EffectAmp * 0.85, EffectAmp * 1.15);
        Assert.InRange(Amp(output, 48000, EffectBHz, 0.3, 1.0), EffectAmp * 0.85, EffectAmp * 1.15);
    }

    [Fact]
    public void MicMuted_EffectsStillPlay()
    {
        var rig = new Rig();
        rig.Params.MicMuted = true;
        var output = rig.Run(1.0, b => { if (b == 5) rig.Graph.Bus.Play(rig.Tone(EffectAHz, 0.9, "a")); });
        Assert.True(Amp(output, 48000, VoiceHz, 0.2, 0.9) < 0.002, "voice must be silent when the mic is muted");
        Assert.InRange(Amp(output, 48000, EffectAHz, 0.2, 0.9), EffectAmp * 0.85, EffectAmp * 1.15);
    }

    [Fact]
    public void SoundboardMuted_VoiceStillPasses()
    {
        var rig = new Rig();
        rig.Params.SoundboardMuted = true;
        var output = rig.Run(1.0, b => { if (b == 5) rig.Graph.Bus.Play(rig.Tone(EffectAHz, 0.9, "a")); });
        Assert.InRange(Amp(output, 48000, VoiceHz, 0.2, 0.9), VoiceAmp * 0.85, VoiceAmp * 1.15);
        Assert.True(Amp(output, 48000, EffectAHz, 0.2, 0.9) < 0.002, "effects must be silent when the soundboard is muted");
    }

    [Fact]
    public void LoudOverlap_IsLimitedWithoutClipping()
    {
        var rig = new Rig();
        rig.Params.MicGain = 4f;
        rig.Params.SoundboardGain = 2f;
        rig.Params.MasterGain = 2f;
        var output = rig.Run(1.0, b =>
        {
            if (b == 5)
            {
                rig.Graph.Bus.Play(rig.Tone(EffectAHz, 0.9, "a"));
                rig.Graph.Bus.Play(rig.Tone(EffectBHz, 0.9, "b"));
                rig.Graph.Bus.Play(rig.Tone(EffectAHz, 0.9, "a2"));
            }
        });
        float peak = SignalAnalysis.Peak(output);
        Assert.True(peak <= 0.8913f + 1e-4f, $"peak {peak} exceeds the -1 dBFS ceiling");
        Assert.True(peak > 0.5f);
        Assert.True(Amp(output, 48000, VoiceHz, 0.2, 0.9) > 0.02, "voice must remain audible under limiting");
    }

    [Fact]
    public void DifferentSampleRatesAndFormats_AreConverted()
    {
        // 44.1 kHz stereo 16-bit PCM microphone into a 48 kHz stereo engine.
        var rig = new Rig(48000, 2, new WaveFormat(44100, 16, 2));
        var output = rig.Run(1.2, b => { if (b == 5) rig.Graph.Bus.Play(rig.Tone(EffectAHz, 1.0, "a")); });
        Assert.InRange(Amp(output, 48000, VoiceHz, 0.3, 1.1), VoiceAmp * 0.8, VoiceAmp * 1.2);
        Assert.InRange(Amp(output, 48000, EffectAHz, 0.3, 1.0), EffectAmp * 0.85, EffectAmp * 1.15);
    }

    [Fact]
    public void Mono24BitMic_IntoSixChannelEngine()
    {
        var rig = new Rig(48000, 6, new WaveFormat(48000, 24, 1));
        var output = rig.Run(0.8);
        Assert.InRange(Amp(output, 48000, VoiceHz, 0.2, 0.8), VoiceAmp * 0.85, VoiceAmp * 1.15);
    }

    [Fact]
    public void MicDisconnect_MixerKeepsRunning_AndRecovers()
    {
        var rig = new Rig();
        var output = rig.Run(1.5, b =>
        {
            if (b == 40) rig.MicConnected = false;
            if (b == 80) rig.MicConnected = true;
            if (b == 50) rig.Graph.Bus.Play(rig.Tone(EffectAHz, 0.2, "a"));
        });
        Assert.True(Amp(output, 48000, VoiceHz, 0.45, 0.78) < 0.002);           // gap is silent, not garbage
        Assert.True(Amp(output, 48000, EffectAHz, 0.5, 0.7) > EffectAmp * 0.8);  // effects work during the gap
        Assert.True(Amp(output, 48000, VoiceHz, 1.0, 1.5) > VoiceAmp * 0.8);     // voice is back
    }

    [Fact]
    public void StopPreviousMode_StopsEarlierSound()
    {
        var rig = new Rig();
        var output = rig.Run(1.2, b =>
        {
            if (b == 5) rig.Graph.Bus.Play(rig.Tone(EffectAHz, 1.1, "a"));
            if (b == 40) rig.Graph.Bus.Play(rig.Tone(EffectBHz, 0.6, "b"), stopOthersFirst: true);
        });
        Assert.True(Amp(output, 48000, EffectAHz, 0.1, 0.38) > EffectAmp * 0.8);
        Assert.True(Amp(output, 48000, EffectAHz, 0.45, 0.95) < 0.003, "first sound must stop");
        Assert.True(Amp(output, 48000, EffectBHz, 0.45, 0.95) > EffectAmp * 0.8);
    }

    [Fact]
    public void NoDuplicatesMode_RestartsInsteadOfStacking()
    {
        var rig = new Rig();
        int maxVoices = 0;
        rig.Run(0.6, b =>
        {
            if (b == 5 || b == 10 || b == 15) rig.Graph.Bus.Play(rig.Tone(EffectAHz, 1.0, "same"), stopSameKeyFirst: true);
            maxVoices = Math.Max(maxVoices, rig.Graph.Bus.ActiveVoices);
        });
        rig.Run(0.05, b => maxVoices = Math.Max(maxVoices, rig.Graph.Bus.ActiveVoices));
        Assert.Equal(1, rig.Graph.Bus.ActiveVoices);
        Assert.True(maxVoices <= 2, "only a fading copy may briefly coexist"); // old copy fades over 8 ms
    }

    [Fact]
    public void OutputTap_ReceivesExactMix_ForMonitoring()
    {
        var rig = new Rig();
        var tap = new BufferedFloatProvider(48000, 2, 0, 2000, 3000);
        rig.Graph.OutputTap = tap;
        rig.Run(0.5);
        var buf = new float[48000 * 2];
        int n = tap.Drain(buf);
        Assert.True(n > 48000 * 2 * 0.45);
        var left = new float[n / 2];
        for (int i = 0; i < left.Length; i++) left[i] = buf[i * 2];
        Assert.InRange(SignalAnalysis.ToneAmplitude(left.AsSpan(9600), 48000, VoiceHz, 4096), VoiceAmp * 0.85, VoiceAmp * 1.15);
    }
}

internal static class Synth
{
    /// <summary>Encodes a mono signal into the given capture format (duplicated across channels).</summary>
    public static byte[] Encode(int frames, WaveFormat fmt, Func<int, float> signal)
    {
        int ch = fmt.Channels;
        int bps = fmt.BitsPerSample / 8;
        var bytes = new byte[frames * ch * bps];
        for (int f = 0; f < frames; f++)
        {
            float v = signal(f);
            for (int c = 0; c < ch; c++)
            {
                int o = (f * ch + c) * bps;
                if (SampleConverter.IsFloat(fmt))
                {
                    MemoryMarshal.Write(bytes.AsSpan(o), in v);
                }
                else if (fmt.BitsPerSample == 16)
                {
                    short s = (short)Math.Clamp(v * 32767, short.MinValue, short.MaxValue);
                    MemoryMarshal.Write(bytes.AsSpan(o), in s);
                }
                else if (fmt.BitsPerSample == 24)
                {
                    int s = (int)(v * 8388607);
                    bytes[o] = (byte)s;
                    bytes[o + 1] = (byte)(s >> 8);
                    bytes[o + 2] = (byte)(s >> 16);
                }
                else throw new NotSupportedException();
            }
        }
        return bytes;
    }
}
