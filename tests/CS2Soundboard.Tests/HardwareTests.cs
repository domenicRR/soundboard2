using System;
using System.Linq;
using System.Threading.Tasks;
using CS2Soundboard.Core.Audio;
using NAudio.CoreAudioApi;
using Xunit;
using Xunit.Abstractions;

namespace CS2Soundboard.Tests;

/// <summary>
/// End-to-end tests against real Windows audio devices. They need VB-CABLE and a microphone and
/// are excluded from the default run. Run with:
///   dotnet test --filter Category=Hardware
/// The output goes only to "CABLE Input"; verification captures "CABLE Output". Nothing is played
/// on speakers. Tones are quiet (about -20 dBFS) and short.
/// </summary>
[Trait("Category", "Hardware")]
[Collection("Hardware")]
public class HardwareTests
{
    private readonly ITestOutputHelper _out;
    public HardwareTests(ITestOutputHelper output) => _out = output;

    private sealed record Devices(AudioDeviceInfo Mic, AudioDeviceInfo CableIn, AudioDeviceInfo CableOut);

    private static async Task<Devices> FindDevicesAsync(AudioService svc)
    {
        var lists = await svc.GetDevicesAsync();
        var cableIn = lists.Renders.FirstOrDefault(r => r.Name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("VB-CABLE 'CABLE Input' not found: hardware test cannot run.");
        var cableOut = VirtualDeviceRules.FindRecordingSide(cableIn, lists.Captures)
                       ?? throw new InvalidOperationException("'CABLE Output' not found.");
        var mic = lists.DefaultCapture is { LooksVirtual: false } d ? d : lists.Captures.FirstOrDefault(c => !c.LooksVirtual)
                  ?? throw new InvalidOperationException("No physical microphone found.");
        return new Devices(mic, cableIn, cableOut);
    }

    private static AudioConfig Config(Devices d, string? micId = null) =>
        new(micId ?? d.Mic.Id, micId == null ? d.Mic.Name : null, d.CableIn.Id, d.CableIn.Name, null, null, 30, true);

    /// <summary>Captures mic (after gain) and virtual-recording RMS over a window.</summary>
    private static async Task<(double micRms, double probeRms, double probeTone)> MeasureAsync(AudioService svc, double seconds, double toneHz = 997)
    {
        var mic = svc.MicSink!;
        var probe = svc.ProbeSink!;
        var micTap = new BufferedFloatProvider(mic.CaptureFormat.SampleRate, 1, 0, 8000, 10000);
        var probeTap = new BufferedFloatProvider(probe.CaptureFormat.SampleRate, 1, 0, 8000, 10000);
        await Task.Delay(300);
        mic.AnalysisTap = micTap;
        probe.AnalysisTap = probeTap;
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        mic.AnalysisTap = null;
        probe.AnalysisTap = null;
        var mb = new float[mic.CaptureFormat.SampleRate * 10];
        var pb = new float[probe.CaptureFormat.SampleRate * 10];
        int mn = micTap.Drain(mb), pn = probeTap.Drain(pb);
        // Skip the first 150 ms of the probe (pipeline latency).
        int skip = Math.Min(pn, probe.CaptureFormat.SampleRate * 15 / 100);
        return (SignalAnalysis.Rms(mb.AsSpan(0, mn)),
                SignalAnalysis.Rms(pb.AsSpan(skip, pn - skip)),
                SignalAnalysis.ToneAmplitude(pb.AsSpan(0, pn), probe.CaptureFormat.SampleRate, toneHz));
    }

    [Fact]
    public async Task EndToEnd_MicPassthrough_Effects_Mutes_Feedback_And_Recovery()
    {
        var p = new MixerParameters { MicGain = 4f, SoundboardGain = 0.3f, MasterGain = 1f };
        using var svc = new AudioService(p, m => _out.WriteLine("  [audio] " + m));
        await svc.InitializeAsync();
        var d = await FindDevicesAsync(svc);
        _out.WriteLine($"Mic: {d.Mic.Name} | Output: {d.CableIn.Name} | Verification: {d.CableOut.Name}");

        await svc.ApplyConfigAsync(Config(d));
        await svc.SetProbeEnabledAsync(true);
        await Task.Delay(1500);
        await svc.RefreshStatusAsync();
        var st = svc.Status;
        _out.WriteLine(st.Diagnostics);
        Assert.Equal(LinkState.Running, st.Output.State);
        Assert.Equal(LinkState.Running, st.Mic.State);
        Assert.Equal(LinkState.Running, st.Probe.State);

        // 1. Idle: no sounds playing, mixer + capture keep running.
        long reads0 = svc.Graph!.ReadCalls, blocks0 = svc.MicSink!.BlocksReceived;
        var idle = await MeasureAsync(svc, 1.5);
        Assert.Equal(0, svc.Graph!.Bus.ActiveVoices);
        Assert.True(svc.Graph.ReadCalls > reads0 + 50, "output must keep pulling audio while idle");
        Assert.True(svc.MicSink!.BlocksReceived > blocks0 + 50, "mic must keep delivering while idle");
        _out.WriteLine($"Idle: mic RMS {PeakMeter.ToDb((float)idle.micRms):0.0} dBFS, CABLE Output RMS {PeakMeter.ToDb((float)idle.probeRms):0.0} dBFS, expected about {PeakMeter.ToDb((float)(idle.micRms * 4)):0.0} dBFS");
        bool micHasSignal = idle.micRms > 3e-6; // even a quiet mic's noise floor (about -110 dBFS) is measurable
        if (micHasSignal)
        {
            double ratio = idle.probeRms / (idle.micRms * 4);
            _out.WriteLine($"Idle passthrough ratio (CABLE Output / expected): {ratio:0.00}");
            Assert.InRange(ratio, 0.5, 2.0);
        }
        else
        {
            _out.WriteLine("Mic delivers (near) digital silence; passthrough level ratio cannot be measured with this mic right now.");
        }

        // 2. Voice + effect.
        var tone = AudioClip.CreateTone(svc.Graph!.WaveFormat.SampleRate, svc.Graph.WaveFormat.Channels, 997, 2.0, 0.3f);
        svc.Graph.Bus.Play(new CachedVoice(tone, "t", "t"));
        var both = await MeasureAsync(svc, 1.2);
        _out.WriteLine($"Effect playing: tone on CABLE Output {PeakMeter.ToDb((float)both.probeTone):0.0} dBFS (expected about {PeakMeter.ToDb(0.09f):0.0})");
        Assert.InRange(both.probeTone, 0.09 * 0.6, 0.09 * 1.4);

        // 3. Mic muted: effect continues, voice removed.
        p.MicMuted = true;
        svc.Graph.Bus.Play(new CachedVoice(tone, "t2", "t2"));
        var muted = await MeasureAsync(svc, 1.2);
        _out.WriteLine($"Mic muted: tone {PeakMeter.ToDb((float)muted.probeTone):0.0} dBFS");
        Assert.InRange(muted.probeTone, 0.09 * 0.6, 0.09 * 1.4);
        p.MicMuted = false;

        // 4. Soundboard muted: effect removed, voice continues.
        p.SoundboardMuted = true;
        svc.Graph.Bus.Play(new CachedVoice(tone, "t3", "t3"));
        var sbMuted = await MeasureAsync(svc, 1.2);
        _out.WriteLine($"Soundboard muted: tone {PeakMeter.ToDb((float)sbMuted.probeTone):0.0} dBFS, voice RMS {PeakMeter.ToDb((float)sbMuted.probeRms):0.0} dBFS");
        Assert.True(sbMuted.probeTone < 0.005, "effect must be inaudible when the soundboard is muted");
        if (micHasSignal) Assert.InRange(sbMuted.probeRms / (sbMuted.micRms * 4), 0.5, 2.0);
        p.SoundboardMuted = false;

        // 5. Mic muted with no effects: CABLE Output should be (near) silent.
        svc.Graph.Bus.StopAll();
        await Task.Delay(200);
        p.MicMuted = true;
        var silent = await MeasureAsync(svc, 1.0);
        _out.WriteLine($"Mic muted, idle: CABLE Output RMS {PeakMeter.ToDb((float)silent.probeRms):0.0} dBFS (mic itself {PeakMeter.ToDb((float)silent.micRms):0.0} dBFS)");
        if (micHasSignal) Assert.True(silent.probeRms < silent.micRms * 4 * 0.1);
        p.MicMuted = false;

        // 6. Built-in self-test (the same one the UI runs).
        var report = await svc.RunSelfTestAsync();
        _out.WriteLine("Self-test report:\n" + report);
        Assert.Contains("PASS  Soundboard audio reached", report);
    }

    [Fact]
    public async Task FeedbackLoop_IsRefused()
    {
        using var svc = new AudioService(new MixerParameters(), m => _out.WriteLine("  [audio] " + m));
        await svc.InitializeAsync();
        var d = await FindDevicesAsync(svc);
        await svc.ApplyConfigAsync(Config(d, micId: d.CableOut.Id));
        await svc.RefreshStatusAsync();
        var st = svc.Status;
        _out.WriteLine("Mic status: " + st.Mic.Error);
        Assert.Equal(LinkState.Error, st.Mic.State);
        Assert.StartsWith("Feedback loop", st.Mic.Error);
        Assert.Null(svc.MicSink);                               // CABLE Output is not captured as the mic
        Assert.Equal(LinkState.Running, st.Output.State);        // sound effects still work
    }

    [Fact]
    public async Task UnavailableOutput_IsReported_WithoutFallback()
    {
        using var svc = new AudioService(new MixerParameters(), m => _out.WriteLine("  [audio] " + m));
        await svc.InitializeAsync();
        var d = await FindDevicesAsync(svc);
        await svc.ApplyConfigAsync(new AudioConfig(d.Mic.Id, d.Mic.Name, "{bogus-device-id}", "My Missing Cable", null, null, 30, true));
        await svc.RefreshStatusAsync();
        var st = svc.Status;
        _out.WriteLine("Output status: " + st.Output.Error);
        Assert.Equal(LinkState.Error, st.Output.State);
        Assert.Contains("not available", st.Output.Error);
        Assert.Null(svc.Graph); // never silently routed to another device
    }

    [Fact]
    public async Task Shutdown_ReleasesDevices_AndRestartWorks()
    {
        var d = (Devices?)null;
        for (int i = 0; i < 2; i++)
        {
            using var svc = new AudioService(new MixerParameters { MicMuted = true }, m => _out.WriteLine("  [audio] " + m));
            await svc.InitializeAsync();
            d ??= await FindDevicesAsync(svc);
            await svc.ApplyConfigAsync(Config(d));
            await Task.Delay(500);
            await svc.RefreshStatusAsync();
            Assert.Equal(LinkState.Running, svc.Status.Output.State);
            Assert.Equal(LinkState.Running, svc.Status.Mic.State);
        }
    }

    [Fact]
    public void Enumerates_Endpoints()
    {
        using var cat = new DeviceCatalog();
        foreach (var dd in cat.List(DataFlow.Capture)) _out.WriteLine($"capture: {dd.Name} | iface: {dd.InterfaceName} | virtual: {dd.LooksVirtual}");
        foreach (var dd in cat.List(DataFlow.Render)) _out.WriteLine($"render:  {dd.Name} | iface: {dd.InterfaceName} | virtual: {dd.LooksVirtual}");
        Assert.NotEmpty(cat.List(DataFlow.Render));
    }
}
