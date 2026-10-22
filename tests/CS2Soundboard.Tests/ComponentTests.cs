using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CS2Soundboard.Core.Audio;
using CS2Soundboard.Core.Hotkeys;
using CS2Soundboard.Core.Library;
using CS2Soundboard.Core.Settings;
using NAudio.Wave;
using Xunit;

namespace CS2Soundboard.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs2sb-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class BufferTests
{
    [Fact]
    public void RingBuffer_WrapsAndPreservesOrder()
    {
        var rb = new FloatRingBuffer(8);
        var outBuf = new float[8];
        float next = 0, expect = 0;
        for (int round = 0; round < 50; round++)
        {
            var data = Enumerable.Range(0, 5).Select(_ => next++).ToArray();
            Assert.Equal(5, rb.Write(data));
            int n = rb.Read(outBuf);
            Assert.Equal(5, n);
            for (int i = 0; i < n; i++) Assert.Equal(expect++, outBuf[i]);
        }
    }

    [Fact]
    public void RingBuffer_DropsWhenFull_AlignedToFrames()
    {
        var rb = new FloatRingBuffer(8);
        Assert.Equal(6, rb.Write(new float[7], alignment: 2));
        Assert.Equal(2, rb.Write(new float[5], alignment: 2));
        Assert.Equal(0, rb.Write(new float[2], alignment: 2));
    }

    [Fact]
    public void BufferedProvider_AlwaysReturnsFullCount_AndBoundsLatency()
    {
        var p = new BufferedFloatProvider(48000, 1, targetLatencyMs: 20, maxLatencyMs: 60);
        var buf = new float[480];
        // Empty: silence, still full count.
        Assert.Equal(480, p.Read(buf, 0, 480));
        Assert.All(buf, v => Assert.Equal(0f, v));

        // Producer faster than consumer (clock drift): latency must stay bounded.
        var block = Enumerable.Repeat(0.5f, 480).ToArray();
        for (int i = 0; i < 500; i++)
        {
            p.Write(block);
            if (i % 10 != 0) p.Read(buf, 0, 480); // consumer skips every 10th block
        }
        Assert.True(p.BufferedMilliseconds <= 70, $"buffered {p.BufferedMilliseconds} ms");
        Assert.True(p.DriftCorrections > 0);
    }

    [Fact]
    public void Converter_HandlesPcm16And24AndFloat()
    {
        var pcm16 = new WaveFormat(48000, 16, 1);
        var bytes = Synth.Encode(4, pcm16, f => f * 0.25f);
        var dst = new float[4];
        SampleConverter.ToFloat(bytes, bytes.Length, pcm16, dst);
        Assert.Equal(0.5f, dst[2], 3);

        var pcm24 = new WaveFormat(48000, 24, 1);
        bytes = Synth.Encode(4, pcm24, f => -f * 0.25f);
        SampleConverter.ToFloat(bytes, bytes.Length, pcm24, dst);
        Assert.Equal(-0.75f, dst[3], 4);

        var ext = new WaveFormatExtensible(48000, 32, 2); // float subtype
        Assert.True(SampleConverter.IsFloat(ext));
    }

    [Fact]
    public void CaptureSink_DetectsDigitalSilence()
    {
        var sink = new CaptureSink(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), true);
        var silent = new byte[480 * 4];
        for (int i = 0; i < 400; i++) sink.OnData(silent, silent.Length); // 4 s
        Assert.True(sink.DigitalSilenceSeconds > 3.9);
        var noisy = Synth.Encode(480, sink.CaptureFormat, f => 0.001f);
        sink.OnData(noisy, noisy.Length);
        Assert.Equal(0, sink.DigitalSilenceSeconds);
    }
}

public class DecodingTests
{
    private static void WriteSineWav(string path, int rate, int channels, double seconds, double hz = 440)
    {
        using var w = new WaveFileWriter(path, new WaveFormat(rate, 16, channels));
        int frames = (int)(rate * seconds);
        for (int f = 0; f < frames; f++)
        {
            float s = (float)(0.5 * Math.Sin(2 * Math.PI * hz * f / rate));
            for (int c = 0; c < channels; c++) w.WriteSample(s);
        }
    }

    [Fact]
    public void Wav_IsDecodedAndConvertedToEngineFormat()
    {
        using var dir = new TempDir();
        var path = dir.File("beep.wav");
        WriteSineWav(path, 22050, 1, 1.0);
        var clip = AudioDecoder.DecodeToClip(path, 48000, 2);
        Assert.Equal(48000, clip.SampleRate);
        Assert.Equal(2, clip.Channels);
        Assert.InRange(clip.Duration.TotalSeconds, 0.98, 1.02);
        var left = Enumerable.Range(0, clip.Samples.Length / 2).Select(i => clip.Samples[i * 2]).ToArray();
        Assert.InRange(SignalAnalysis.ToneAmplitude(left.AsSpan(4800, 38400), 48000, 440, 4096), 0.45, 0.55);
    }

    [Fact]
    public void Mp3_IsDecoded_WhenWindowsEncoderAvailable()
    {
        using var dir = new TempDir();
        var wav = dir.File("src.wav");
        var mp3 = dir.File("beep.mp3");
        WriteSineWav(wav, 44100, 2, 1.0);
        try
        {
            using var reader = new WaveFileReader(wav);
            MediaFoundationEncoder.EncodeToMp3(reader, mp3, 128000);
        }
        catch (Exception ex)
        {
            // No MP3 encoder on this machine: this test cannot be performed here.
            Assert.Fail("MP3 encoder unavailable, could not create a test MP3: " + ex.Message);
        }
        var clip = AudioDecoder.DecodeToClip(mp3, 48000, 2);
        Assert.InRange(clip.Duration.TotalSeconds, 0.9, 1.2);
        var left = Enumerable.Range(0, clip.Samples.Length / 2).Select(i => clip.Samples[i * 2]).ToArray();
        Assert.True(SignalAnalysis.ToneAmplitude(left.AsSpan(9600, 24000), 48000, 440, 4096) > 0.3);
    }

    [Fact]
    public void CorruptAndMissingFiles_RaiseFriendlyErrors()
    {
        using var dir = new TempDir();
        var bad = dir.File("broken.wav");
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        var ex1 = Assert.Throws<AudioDecodeException>(() => AudioDecoder.DecodeToClip(bad, 48000, 2));
        Assert.Contains("broken.wav", ex1.Message);

        var badOgg = dir.File("broken.ogg");
        File.WriteAllText(badOgg, "this is not an ogg file");
        Assert.Throws<AudioDecodeException>(() => AudioDecoder.DecodeToClip(badOgg, 48000, 2));

        var ex2 = Assert.Throws<AudioDecodeException>(() => AudioDecoder.OpenReader(dir.File("nope.mp3")));
        Assert.Contains("not found", ex2.Message);
    }

    [Fact]
    public async Task Cache_StreamsLongFiles_AndStreamingVoicePlaysToEnd()
    {
        using var dir = new TempDir();
        var path = dir.File("long.wav");
        WriteSineWav(path, 48000, 2, 3.0);
        var cache = new ClipCache(maxCachedDuration: TimeSpan.FromSeconds(1));
        Assert.Null(await cache.GetOrLoadAsync(path, 48000, 2)); // too long: stream it

        var voice = await StreamingVoice.StartAsync(path, 48000, 2, "long", "long");
        var buf = new float[960];
        long total = 0;
        for (int i = 0; i < 1000; i++)
        {
            int n = voice.Read(buf, 0, buf.Length);
            total += n;
            if (n < buf.Length) break;
            await Task.Delay(1);
        }
        voice.Dispose();
        Assert.InRange(total / 2.0 / 48000, 2.9, 3.2);
    }

    [Fact]
    public async Task Cache_ReusesClips_AndInvalidatesChangedFiles()
    {
        using var dir = new TempDir();
        var path = dir.File("a.wav");
        WriteSineWav(path, 48000, 1, 0.5);
        var cache = new ClipCache();
        var c1 = await cache.GetOrLoadAsync(path, 48000, 2);
        Assert.True(cache.TryGetReady(path, 48000, 2, out var c2));
        Assert.Same(c1, c2);

        WriteSineWav(path, 48000, 1, 0.25);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.False(cache.TryGetReady(path, 48000, 2, out _));
        var c3 = await cache.GetOrLoadAsync(path, 48000, 2);
        Assert.InRange(c3!.Duration.TotalSeconds, 0.24, 0.26);
    }

    [Fact]
    public void Scanner_FindsSupportedFiles_AndReportsMissingFolder()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("sub"));
        File.WriteAllText(dir.File("a.wav"), "");
        File.WriteAllText(dir.File("b.MP3"), "");
        File.WriteAllText(dir.File("notes.txt"), "");
        File.WriteAllText(Path.Combine(dir.File("sub"), "c.ogg"), "");

        var flat = SoundLibraryScanner.Scan(dir.Path, false);
        Assert.Equal(new[] { "a.wav", "b.MP3" }, flat.Files.Select(f => f.RelativePath).ToArray());
        var deep = SoundLibraryScanner.Scan(dir.Path, true);
        Assert.Contains(deep.Files, f => f.RelativePath == Path.Combine("sub", "c.ogg"));

        Assert.NotNull(SoundLibraryScanner.Scan(dir.File("missing"), true).Error);
        Assert.NotNull(SoundLibraryScanner.Scan(null, true).Error);
    }
}

public class SettingsAndHotkeyTests
{
    [Fact]
    public void Settings_RoundTrip()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Path);
        var s = new AppSettings
        {
            SoundDirectory = @"C:\Sounds",
            MicDeviceId = "{mic}",
            OutputDeviceId = "{out}",
            MicGain = 1.5,
            SoundboardVolume = 0.4,
            MasterVolume = 0.9,
            MicMuted = true,
            PlaybackMode = PlaybackMode.NoDuplicates,
            MinimizeToTray = false,
            StopAllHotkey = "Ctrl+Alt+End",
        };
        s.Hotkeys["sub\\Boom.wav"] = "Ctrl+Alt+D1";
        store.Save(s);

        var (loaded, warning) = store.Load();
        Assert.Null(warning);
        Assert.Equal(@"C:\Sounds", loaded.SoundDirectory);
        Assert.Equal("{mic}", loaded.MicDeviceId);
        Assert.Equal(1.5, loaded.MicGain);
        Assert.True(loaded.MicMuted);
        Assert.Equal(PlaybackMode.NoDuplicates, loaded.PlaybackMode);
        Assert.False(loaded.MinimizeToTray);
        Assert.Equal("Ctrl+Alt+D1", loaded.Hotkeys["SUB\\boom.WAV"]); // case-insensitive keys
    }

    [Fact]
    public void Settings_CorruptFile_FallsBackWithWarningAndBackup()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Path);
        File.WriteAllText(store.FilePath, "{ this is not json");
        var (loaded, warning) = store.Load();
        Assert.NotNull(warning);
        Assert.NotNull(loaded);
        Assert.Single(Directory.GetFiles(dir.Path, "settings.corrupt-*.json"));
    }

    [Fact]
    public void Settings_OutOfRangeValues_AreClamped()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Path);
        File.WriteAllText(store.FilePath, "{\"MicGain\": 99, \"OutputLatencyMs\": 5, \"SoundboardVolume\": -3}");
        var (s, _) = store.Load();
        Assert.Equal(4, s.MicGain);
        Assert.Equal(0, s.SoundboardVolume);
        Assert.Equal(30, s.OutputLatencyMs);
    }

    [Theory]
    [InlineData("Ctrl+Alt+D1", "Ctrl+Alt+1")]
    [InlineData("Shift+F5", "Shift+F5")]
    [InlineData("NumPad7", "Num 7")]
    [InlineData("Ctrl+Shift+OemPlus", "Ctrl+Shift+=")]
    public void Hotkey_ParseSerializeDisplay(string text, string display)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(display, hk.ToString());
        Assert.True(Hotkey.TryParse(hk.Serialize(), out var again));
        Assert.Equal(hk, again);
    }

    [Fact]
    public void Hotkey_Validation()
    {
        Assert.False(Hotkey.Validate(new Hotkey(ModifierKeys.None, Key.W)).IsValid);      // would block movement
        Assert.False(Hotkey.Validate(new Hotkey(ModifierKeys.Alt, Key.F4)).IsValid);      // reserved
        Assert.False(Hotkey.Validate(new Hotkey(ModifierKeys.Control, Key.LeftShift)).IsValid);
        Assert.True(Hotkey.Validate(new Hotkey(ModifierKeys.Control | ModifierKeys.Alt, Key.D1)).IsValid);
        var bare = Hotkey.Validate(new Hotkey(ModifierKeys.None, Key.NumPad1));
        Assert.True(bare.IsValid);
        Assert.NotNull(bare.Warning);
        Assert.Equal(0x0002u | 0x0001u, new Hotkey(ModifierKeys.Control | ModifierKeys.Alt, Key.A).Win32Modifiers);
        Assert.False(Hotkey.TryParse("Ctrl+Banana", out _));
    }
}

public class RoutingTests
{
    private static readonly AudioDeviceInfo CableIn = new("r1", "CABLE Input (VB-Audio Virtual Cable)", "VB-Audio Virtual Cable", false, true);
    private static readonly AudioDeviceInfo Cable16 = new("r2", "CABLE In 16ch (VB-Audio Virtual Cable)", "VB-Audio Virtual Cable", false, true);
    private static readonly AudioDeviceInfo CableOut = new("c1", "CABLE Output (VB-Audio Virtual Cable)", "VB-Audio Virtual Cable", true, true);
    private static readonly AudioDeviceInfo RealMic = new("c2", "Microphone (HyperX)", "HyperX", true, true);
    private static readonly AudioDeviceInfo Speakers = new("r3", "Speakers (Realtek)", "Realtek", false, true);
    private static readonly AudioDeviceInfo VmIn = new("r4", "Voicemeeter Input (VB-Audio Voicemeeter VAIO)", "VB-Audio Voicemeeter VAIO", false, true);
    private static readonly AudioDeviceInfo VmOutB1 = new("c3", "Voicemeeter Out B1 (VB-Audio Voicemeeter VAIO)", "VB-Audio Voicemeeter VAIO", true, true);
    private static readonly AudioDeviceInfo VmOutput = new("c4", "Voicemeeter Output (VB-Audio Voicemeeter VAIO)", "VB-Audio Voicemeeter VAIO", true, true);

    private static readonly AudioDeviceInfo[] Captures = { CableOut, RealMic, VmOutB1, VmOutput };

    [Fact]
    public void ValidSetup_HasNoErrors()
    {
        var issues = RoutingValidator.Validate(RealMic, CableIn, Speakers, Captures);
        Assert.DoesNotContain(issues, i => i.IsError);
    }

    [Fact]
    public void CableOutputAsMic_IsAFeedbackLoop()
    {
        var issues = RoutingValidator.Validate(CableOut, CableIn, Speakers, Captures);
        Assert.Contains(issues, i => i.IsError && i.Message.StartsWith("Feedback loop"));
        issues = RoutingValidator.Validate(CableOut, Cable16, Speakers, Captures);
        Assert.Contains(issues, i => i.IsError && i.Message.StartsWith("Feedback loop"));
    }

    [Fact]
    public void MonitoringIntoTheVirtualOutput_IsBlocked()
    {
        var issues = RoutingValidator.Validate(RealMic, CableIn, CableIn, Captures);
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("listening device"));
    }

    [Fact]
    public void PhysicalOutput_IsWarned()
    {
        var issues = RoutingValidator.Validate(RealMic, Speakers, null, Captures);
        Assert.Contains(issues, i => !i.IsError && i.Message.Contains("does not look like a virtual"));
    }

    [Fact]
    public void RecordingSide_IsFoundByName()
    {
        Assert.Equal(CableOut, VirtualDeviceRules.FindRecordingSide(CableIn, Captures));
        Assert.Equal(CableOut, VirtualDeviceRules.FindRecordingSide(Cable16, Captures));
        Assert.Equal(VmOutput, VirtualDeviceRules.FindRecordingSide(VmIn, Captures));
        Assert.Null(VirtualDeviceRules.FindRecordingSide(Speakers, Captures));
    }
}

public class PlaybackControllerTests
{
    private static string MakeWav(TempDir dir, string name, double seconds)
    {
        var path = dir.File(name);
        using var w = new WaveFileWriter(path, new WaveFormat(48000, 16, 1));
        for (int i = 0; i < 48000 * seconds; i++) w.WriteSample(0.1f);
        return path;
    }

    private static (PlaybackController ctl, MixGraph graph, LocalGraph local) Make(ClipCache cache)
    {
        var p = new MixerParameters();
        var graph = new MixGraph(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), p);
        var local = new LocalGraph(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
        var ctl = new PlaybackController(() => graph, () => local, () => Task.FromResult<LocalGraph?>(local), cache);
        return (ctl, graph, local);
    }

    private static void Render(ISampleProvider g, int blocks = 1)
    {
        var buf = new float[960];
        for (int i = 0; i < blocks; i++) g.Read(buf, 0, buf.Length);
    }

    [Fact]
    public async Task Trigger_PlaysIntoMix_AndOptionallyLocally()
    {
        using var dir = new TempDir();
        var path = MakeWav(dir, "x.wav", 0.5);
        var (ctl, graph, local) = Make(new ClipCache());
        Assert.Null(await ctl.TriggerAsync("x.wav", path, "x", PlaybackMode.Overlap, false, hearLocally: true));
        Render(graph); Render(local);
        Assert.Equal(1, graph.Bus.ActiveVoices);
        Assert.Equal(1, local.Bus.ActiveVoices);
        Assert.True(ctl.IsPlaying("x.wav"));
    }

    [Fact]
    public async Task RetriggerStops_TogglesSound()
    {
        using var dir = new TempDir();
        var path = MakeWav(dir, "x.wav", 2);
        var (ctl, graph, _) = Make(new ClipCache());
        await ctl.TriggerAsync("x.wav", path, "x", PlaybackMode.Overlap, true, false);
        Render(graph);
        await ctl.TriggerAsync("x.wav", path, "x", PlaybackMode.Overlap, true, false);
        Render(graph, 5);
        ctl.Pump();
        Assert.Equal(0, graph.Bus.ActiveVoices);
        Assert.False(ctl.IsPlaying("x.wav"));
    }

    [Fact]
    public async Task NoOutput_ReportsErrorInsteadOfSilentlyFailing()
    {
        using var dir = new TempDir();
        var path = MakeWav(dir, "x.wav", 0.2);
        var ctl = new PlaybackController(() => null, () => null, () => Task.FromResult<LocalGraph?>(null), new ClipCache());
        var err = await ctl.TriggerAsync("x.wav", path, "x", PlaybackMode.Overlap, false, false);
        Assert.Contains("not running", err);
    }

    [Fact]
    public async Task Preview_UsesLocalOnly_AndStopAllStopsEverything()
    {
        using var dir = new TempDir();
        var path = MakeWav(dir, "x.wav", 2);
        var (ctl, graph, local) = Make(new ClipCache());
        await ctl.PreviewAsync("x.wav", path, "x");
        await ctl.TriggerAsync("x.wav", path, "x", PlaybackMode.Overlap, false, false);
        Render(graph); Render(local);
        Assert.Equal(1, graph.Bus.ActiveVoices);
        Assert.Equal(1, local.Bus.ActiveVoices);
        ctl.StopAll();
        Render(graph, 5); Render(local, 5);
        ctl.Pump();
        Assert.Empty(ctl.Active);
    }
}
