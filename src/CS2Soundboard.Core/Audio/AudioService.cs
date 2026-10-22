using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CS2Soundboard.Core.Settings;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CS2Soundboard.Core.Audio;

public sealed record AudioConfig(
    string? MicId, string? MicName,
    string? OutputId, string? OutputName,
    string? LocalId, string? LocalName,
    int LatencyMs, bool RoutingEnabled);

public enum LinkState { Off, NotConfigured, Running, Error }

public sealed record EndpointStatus(LinkState State, string? DeviceName, string? Format, string? Error, string? Warning)
{
    public static readonly EndpointStatus Off = new(LinkState.Off, null, null, null, null);
}

public sealed record AudioStatus(
    EndpointStatus Output,
    EndpointStatus Mic,
    EndpointStatus Local,
    EndpointStatus Probe,
    IReadOnlyList<RoutingIssue> Issues,
    MonitorMode Monitor,
    bool RoutingEnabled,
    string Diagnostics)
{
    public static readonly AudioStatus Initial = new(EndpointStatus.Off, EndpointStatus.Off, EndpointStatus.Off, EndpointStatus.Off,
        Array.Empty<RoutingIssue>(), MonitorMode.Off, true, "");

    /// <summary>True when the live mic is actually being forwarded to the virtual device.</summary>
    public bool ForwardingMic => Output.State == LinkState.Running && Mic.State == LinkState.Running;
}

public sealed record DeviceLists(
    IReadOnlyList<AudioDeviceInfo> Captures,
    IReadOnlyList<AudioDeviceInfo> Renders,
    AudioDeviceInfo? DefaultCapture,
    AudioDeviceInfo? DefaultRender);

/// <summary>
/// Owns every audio device stream:
///  * virtual output (MixGraph to e.g. "CABLE Input")
///  * physical microphone capture (continuously feeding the MixGraph)
///  * local output (previews/monitoring on the user's speakers or headphones)
///  * probe capture of the virtual recording endpoint (e.g. "CABLE Output") for verification
///
/// All device work runs on a dedicated <see cref="AudioWorker"/> thread. Failures are reported in
/// <see cref="Status"/> and retried automatically; the service never substitutes a different device.
/// </summary>
public sealed class AudioService : IDisposable
{
    private readonly AudioWorker _worker = new();
    private readonly Action<string>? _log;
    private DeviceCatalog? _catalog;
    private Timer? _watchdog;

    private AudioConfig _config = new(null, null, null, null, null, null, 30, true);
    private OutputSession? _out;
    private CaptureSession? _mic;
    private OutputSession? _local;
    private CaptureSession? _probe;
    private MixGraph? _graph;
    private LocalGraph? _localGraph;
    private string? _outErr, _micErr, _localErr, _probeErr;
    private bool _micBlocked;
    private bool _localWanted, _probeWanted;
    private MonitorMode _monitor = MonitorMode.Off;
    private List<RoutingIssue> _issues = new();
    private long _lastGraphReads, _lastMicBlocks;
    private DateTime _graphProgressUtc, _micProgressUtc, _localIdleSinceUtc = DateTime.MaxValue;
    private DateTime _nextRetryUtc = DateTime.MinValue;
    private float _localVolume = 0.8f;
    private AudioStatus _status = AudioStatus.Initial;
    private bool _disposed;

    public AudioService(MixerParameters parameters, Action<string>? log = null)
    {
        Params = parameters;
        _log = log;
    }

    public MixerParameters Params { get; }

    /// <summary>Raised on the worker thread when endpoints are added/removed/changed.</summary>
    public event Action? DevicesChanged;

    // ---- Lock-free accessors for the UI / playback (any thread) ----
    public MixGraph? Graph => Volatile.Read(ref _graph);
    public LocalGraph? Local => Volatile.Read(ref _localGraph);
    public CaptureSink? MicSink => Volatile.Read(ref _mic)?.Sink;
    public CaptureSink? ProbeSink => Volatile.Read(ref _probe)?.Sink;
    public AudioStatus Status => Volatile.Read(ref _status);

    public float LocalVolume
    {
        get => Volatile.Read(ref _localVolume);
        set
        {
            Volatile.Write(ref _localVolume, Math.Clamp(value, 0f, 2f));
            var lg = Local;
            if (lg != null) { lg.Bus.TargetGain = value; lg.MonitorGain = value; }
        }
    }

    public Task InitializeAsync() => _worker.RunAsync(() =>
    {
        _catalog = new DeviceCatalog();
        try
        {
            _catalog.RegisterNotifications(new DeviceChangeNotifier(OnDeviceNotification));
        }
        catch (Exception ex)
        {
            Log($"Device notifications unavailable: {ex.Message}");
        }
        _watchdog = new Timer(_ => { if (!_disposed) _ = _worker.RunAsync(Watchdog); }, null, 1000, 1000);
        Publish();
    });

    public Task<DeviceLists> GetDevicesAsync() => _worker.RunAsync(() =>
    {
        var cat = Catalog;
        return new DeviceLists(
            cat.List(DataFlow.Capture),
            cat.List(DataFlow.Render),
            cat.GetDefault(DataFlow.Capture, Role.Communications) ?? cat.GetDefault(DataFlow.Capture, Role.Multimedia),
            cat.GetDefault(DataFlow.Render, Role.Multimedia));
    });

    private DeviceCatalog Catalog => _catalog ?? throw new InvalidOperationException("AudioService not initialized");

    /// <summary>Applies a device configuration, restarting only what changed.</summary>
    public Task ApplyConfigAsync(AudioConfig config) => _worker.RunAsync(() =>
    {
        var old = _config;
        _config = config;

        bool outputChanged = old.OutputId != config.OutputId || old.OutputName != config.OutputName
                             || old.LatencyMs != config.LatencyMs || old.RoutingEnabled != config.RoutingEnabled;
        bool micChanged = old.MicId != config.MicId || old.MicName != config.MicName || old.RoutingEnabled != config.RoutingEnabled;
        bool localChanged = old.LocalId != config.LocalId || old.LocalName != config.LocalName || old.OutputId != config.OutputId;

        if (outputChanged) { StopOutput(); StopProbe(); _outErr = null; _probeErr = null; }
        if (micChanged) { StopMic(); _micErr = null; }
        if (localChanged) { StopLocal(); _localErr = null; }

        EnsureRunning();
        Publish();
    });

    /// <summary>Forces a full restart of every stream (e.g. "Restart audio" button).</summary>
    public Task RestartAllAsync() => _worker.RunAsync(() =>
    {
        StopMic(); StopOutput(); StopLocal(); StopProbe();
        _outErr = _micErr = _localErr = _probeErr = null;
        EnsureRunning();
        Publish();
    });

    // ------------------------------------------------------------------
    // Start / stop (worker thread only)
    // ------------------------------------------------------------------

    private void EnsureRunning()
    {
        ValidateSelection();
        var cfg = _config;

        if (!cfg.RoutingEnabled)
        {
            StopMic();
            StopOutput();
        }
        else
        {
            if (_out == null) TryStartOutput();
            if (_mic == null && !_micBlocked) TryStartMic();
        }

        if (_localWanted && _local == null) TryStartLocal();
        if (_probeWanted && _probe == null) TryStartProbe();
        RebuildMonitor();
    }

    private void ValidateSelection()
    {
        var cat = Catalog;
        var cfg = _config;
        var captures = cat.List(DataFlow.Capture);
        var renders = cat.List(DataFlow.Render);
        var mic = FindInfo(captures, cfg.MicId, cfg.MicName);
        var output = FindInfo(renders, cfg.OutputId, cfg.OutputName);
        var local = cfg.LocalId != null ? FindInfo(renders, cfg.LocalId, cfg.LocalName) : cat.GetDefault(DataFlow.Render, Role.Multimedia);

        var issues = RoutingValidator.Validate(mic, output, local, captures);

        // When the configured device is merely unplugged, FindInfo returns null; don't report "not selected".
        if (output == null && (cfg.OutputId != null || cfg.OutputName != null))
            issues.RemoveAll(i => i.Message.StartsWith("No virtual output", StringComparison.Ordinal));
        if (mic == null && (cfg.MicId != null || cfg.MicName != null))
            issues.RemoveAll(i => i.Message.StartsWith("No physical microphone", StringComparison.Ordinal));

        var defRender = cat.GetDefault(DataFlow.Render, Role.Multimedia);
        if (defRender != null && output != null && defRender.Id == output.Id && cfg.LocalId != null)
            issues.Add(new RoutingIssue(false, $"Your Windows default playback device is \"{output.Name}\". Game and system sounds will be sent to CS2 teammates. Set your speakers/headphones as the Windows default playback device."));

        _micBlocked = mic != null && output != null && issues.Any(i => i.IsError && i.Message.StartsWith("Feedback loop", StringComparison.Ordinal));
        if (_micBlocked)
        {
            StopMic();
            _micErr = issues.First(i => i.Message.StartsWith("Feedback loop", StringComparison.Ordinal)).Message;
        }
        _issues = issues;
    }

    private static AudioDeviceInfo? FindInfo(IReadOnlyList<AudioDeviceInfo> list, string? id, string? name)
        => list.FirstOrDefault(d => id != null && d.Id == id)
           ?? list.FirstOrDefault(d => name != null && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    private void TryStartOutput()
    {
        var cfg = _config;
        if (cfg.OutputId == null && cfg.OutputName == null) { _outErr = null; return; }
        var dev = Catalog.FindActive(cfg.OutputId, cfg.OutputName, DataFlow.Render);
        if (dev == null)
        {
            _outErr = $"Virtual output \"{cfg.OutputName ?? cfg.OutputId}\" is not available. Make sure VB-CABLE (or your virtual cable) is installed and enabled in Windows Sound settings.";
            return;
        }
        try
        {
            var session = OutputSession.Start(dev, cfg.LatencyMs, fmt => new MixGraph(fmt, Params));
            session.Faulted += (s, ex) => _ = _worker.RunAsync(() => OnOutputFaulted(s, ex));
            _out = session;
            Volatile.Write(ref _graph, (MixGraph)session.Graph);
            _lastGraphReads = -1;
            _graphProgressUtc = DateTime.UtcNow;
            _outErr = null;
            AttachMic();
            Log($"Virtual output started: {session.DeviceName} ({SampleConverter.Describe(session.Format)}, {cfg.LatencyMs} ms)");
        }
        catch (Exception ex)
        {
            dev.Dispose();
            _outErr = $"Could not start the virtual output \"{cfg.OutputName}\": {Friendly(ex)}";
            Log(_outErr + " | " + ex);
        }
    }

    private void TryStartMic()
    {
        var cfg = _config;
        if (cfg.MicId == null && cfg.MicName == null) { _micErr = null; return; }
        var dev = Catalog.FindActive(cfg.MicId, cfg.MicName, DataFlow.Capture);
        if (dev == null)
        {
            _micErr = $"Microphone \"{cfg.MicName ?? cfg.MicId}\" is not available (unplugged or disabled). Your voice is NOT being sent. It will reconnect automatically when the device returns.";
            return;
        }
        try
        {
            var session = CaptureSession.Start(dev, provideOutput: true, bufferMs: 20, targetLatencyMs: 30, maxLatencyMs: 150);
            session.Faulted += (s, ex) => _ = _worker.RunAsync(() => OnMicFaulted(s, ex));
            Volatile.Write(ref _mic, session);
            _lastMicBlocks = -1;
            _micProgressUtc = DateTime.UtcNow;
            _micErr = null;
            AttachMic();
            Log($"Microphone started: {session.DeviceName} ({SampleConverter.Describe(session.Format)})");
        }
        catch (Exception ex)
        {
            dev.Dispose();
            _micErr = $"Could not open the microphone \"{cfg.MicName}\": {Friendly(ex)}";
            Log(_micErr + " | " + ex);
        }
    }

    /// <summary>Connects the mic's mono stream (resampled if needed) to the current mix graph.</summary>
    private void AttachMic()
    {
        var graph = _graph;
        var mic = _mic;
        if (graph == null) return;
        if (mic?.Sink.Output == null) { graph.SetMicSource(null); return; }
        graph.SetMicSource(graph.BuildMicChain(mic.Sink.Output));
    }

    private void TryStartLocal()
    {
        var cfg = _config;
        MMDevice? dev = cfg.LocalId != null || cfg.LocalName != null
            ? Catalog.FindActive(cfg.LocalId, cfg.LocalName, DataFlow.Render)
            : Catalog.GetDefaultDevice(DataFlow.Render, Role.Multimedia);
        if (dev == null)
        {
            _localErr = cfg.LocalId != null
                ? $"Listening device \"{cfg.LocalName}\" is not available."
                : "No Windows default playback device is available for previews.";
            return;
        }
        string outputId = _out?.DeviceId ?? cfg.OutputId ?? "";
        if (dev.ID == outputId)
        {
            _localErr = $"Previews/monitoring are disabled because the listening device is the virtual output \"{dev.FriendlyName}\" (that would send them to CS2). " +
                        "Choose your speakers or headphones as the listening device in the Audio Mixer tab, or change the Windows default playback device.";
            dev.Dispose();
            return;
        }
        try
        {
            float vol = LocalVolume;
            var session = OutputSession.Start(dev, Math.Max(40, cfg.LatencyMs), fmt => new LocalGraph(fmt) { MonitorGain = vol });
            ((LocalGraph)session.Graph).Bus.TargetGain = vol;
            session.Faulted += (s, ex) => _ = _worker.RunAsync(() => OnLocalFaulted(s, ex));
            _local = session;
            Volatile.Write(ref _localGraph, (LocalGraph)session.Graph);
            _localErr = null;
            _localIdleSinceUtc = DateTime.UtcNow;
            Log($"Local output started: {session.DeviceName}");
        }
        catch (Exception ex)
        {
            dev.Dispose();
            _localErr = $"Could not open the listening device: {Friendly(ex)}";
            Log(_localErr + " | " + ex);
        }
    }

    private void TryStartProbe()
    {
        var cfg = _config;
        var cat = Catalog;
        var output = FindInfo(cat.List(DataFlow.Render), cfg.OutputId, cfg.OutputName);
        if (output == null) { _probeErr = "Select an available virtual output first."; return; }
        var paired = VirtualDeviceRules.FindRecordingSide(output, cat.List(DataFlow.Capture));
        if (paired == null)
        {
            _probeErr = $"Could not find the recording side of \"{output.Name}\". For VB-CABLE it is called \"CABLE Output (VB-Audio Virtual Cable)\"; make sure it is enabled under Sound settings > Recording.";
            return;
        }
        var dev = cat.FindActive(paired.Id, paired.Name, DataFlow.Capture);
        if (dev == null) { _probeErr = $"\"{paired.Name}\" is not available."; return; }
        try
        {
            var session = CaptureSession.Start(dev, provideOutput: false, bufferMs: 20);
            session.Faulted += (s, ex) => _ = _worker.RunAsync(() => OnProbeFaulted(s, ex));
            Volatile.Write(ref _probe, session);
            _probeErr = null;
            Log($"Verification capture started: {session.DeviceName}");
        }
        catch (Exception ex)
        {
            dev.Dispose();
            _probeErr = $"Could not open \"{paired.Name}\" for verification: {Friendly(ex)}";
        }
    }

    private void StopOutput()
    {
        var o = _out;
        _out = null;
        Volatile.Write(ref _graph, null);
        if (o != null) { o.Dispose(); Log("Virtual output stopped"); }
    }

    private void StopMic()
    {
        var m = _mic;
        Volatile.Write(ref _mic, null);
        _graph?.SetMicSource(null);
        if (m != null) { m.Dispose(); Log("Microphone stopped"); }
    }

    private void StopLocal()
    {
        var l = _local;
        _local = null;
        Volatile.Write(ref _localGraph, null);
        l?.Dispose();
    }

    private void StopProbe()
    {
        var p = _probe;
        Volatile.Write(ref _probe, null);
        p?.Dispose();
    }

    // ------------------------------------------------------------------
    // Fault handling, device changes, watchdog
    // ------------------------------------------------------------------

    private void OnOutputFaulted(object? sender, Exception? ex)
    {
        if (!ReferenceEquals(sender, _out)) return;
        _outErr = $"The virtual output stopped: {Friendly(ex)}. Retrying automatically...";
        Log(_outErr);
        StopOutput();
        StopProbe();
        _nextRetryUtc = DateTime.UtcNow.AddSeconds(1);
        Publish();
    }

    private void OnMicFaulted(object? sender, Exception? ex)
    {
        if (!ReferenceEquals(sender, _mic)) return;
        _micErr = $"The microphone stopped: {Friendly(ex)}. Your voice is NOT being sent. Retrying automatically...";
        Log(_micErr);
        StopMic();
        _nextRetryUtc = DateTime.UtcNow.AddSeconds(1);
        Publish();
    }

    private void OnLocalFaulted(object? sender, Exception? ex)
    {
        if (!ReferenceEquals(sender, _local)) return;
        _localErr = $"The listening device stopped: {Friendly(ex)}";
        StopLocal();
        _nextRetryUtc = DateTime.UtcNow.AddSeconds(1);
        Publish();
    }

    private void OnProbeFaulted(object? sender, Exception? ex)
    {
        if (!ReferenceEquals(sender, _probe)) return;
        _probeErr = $"Verification capture stopped: {Friendly(ex)}";
        StopProbe();
        Publish();
    }

    private int _deviceChangePending;

    private void OnDeviceNotification(string what)
    {
        // Called on a COM thread: never call back into the audio APIs here. Debounce and hand off.
        if (Interlocked.Exchange(ref _deviceChangePending, 1) == 1) return;
        _ = Task.Delay(400).ContinueWith(_ => _worker.RunAsync(() =>
        {
            Interlocked.Exchange(ref _deviceChangePending, 0);
            if (_disposed) return;
            Log($"Audio devices changed ({what})");

            // If we follow the Windows default playback device for previews, re-open on change.
            if (_config.LocalId == null && _local != null)
            {
                var def = Catalog.GetDefault(DataFlow.Render, Role.Multimedia);
                if (def == null || def.Id != _local.DeviceId) StopLocal();
            }
            _nextRetryUtc = DateTime.MinValue;
            EnsureRunning();
            Publish();
            DevicesChanged?.Invoke();
        }));
    }

    private void Watchdog()
    {
        if (_disposed || _catalog == null) return;
        var now = DateTime.UtcNow;

        var graph = _graph;
        if (_out != null && graph != null)
        {
            long reads = graph.ReadCalls;
            if (reads != _lastGraphReads) { _lastGraphReads = reads; _graphProgressUtc = now; }
            else if (now - _graphProgressUtc > TimeSpan.FromSeconds(3))
            {
                _outErr = "The virtual output stopped requesting audio. Restarting...";
                Log(_outErr);
                StopOutput();
            }
        }

        var mic = _mic;
        if (mic != null)
        {
            long blocks = mic.Sink.BlocksReceived;
            if (blocks != _lastMicBlocks) { _lastMicBlocks = blocks; _micProgressUtc = now; }
            else if (now - _micProgressUtc > TimeSpan.FromSeconds(3))
            {
                _micErr = "The microphone stopped delivering audio (disconnected or blocked). Your voice is NOT being sent. Retrying...";
                Log(_micErr);
                StopMic();
            }
        }

        // Release the speakers when previews/monitoring have been idle for a while.
        var lg = _localGraph;
        if (_local != null && lg != null && _monitor == MonitorMode.Off)
        {
            if (lg.Bus.ActiveVoices > 0) _localIdleSinceUtc = now;
            else if (now - _localIdleSinceUtc > TimeSpan.FromSeconds(60) && !KeepLocalAlive)
            {
                StopLocal();
                _localWanted = false;
            }
        }

        bool needsRetry = (_config.RoutingEnabled && ((_out == null && (_config.OutputId ?? _config.OutputName) != null)
                                                     || (_mic == null && !_micBlocked && (_config.MicId ?? _config.MicName) != null)))
                          || (_localWanted && _local == null)
                          || (_probeWanted && _probe == null);
        if (needsRetry && now >= _nextRetryUtc)
        {
            _nextRetryUtc = now.AddSeconds(3);
            EnsureRunning();
        }
        Publish();
    }

    /// <summary>Keep the local output open (e.g. when sounds are also played locally on hotkeys).</summary>
    public bool KeepLocalAlive { get; set; }

    // ------------------------------------------------------------------
    // Local output, monitoring and probe (public API)
    // ------------------------------------------------------------------

    /// <summary>Starts the local output if needed and returns its graph (null on failure; see Status.Local).</summary>
    public Task<LocalGraph?> EnsureLocalAsync() => _worker.RunAsync(() =>
    {
        _localWanted = true;
        _localIdleSinceUtc = DateTime.UtcNow;
        if (_local == null) { TryStartLocal(); RebuildMonitor(); Publish(); }
        return _localGraph;
    });

    public Task SetMonitorModeAsync(MonitorMode mode) => _worker.RunAsync(() =>
    {
        _monitor = mode;
        if (mode != MonitorMode.Off)
        {
            _localWanted = true;
            if (_local == null) TryStartLocal();
        }
        if (mode == MonitorMode.VirtualRecording)
        {
            _probeWanted = true;
            if (_probe == null) TryStartProbe();
        }
        RebuildMonitor();
        Publish();
    });

    public Task SetProbeEnabledAsync(bool enabled) => _worker.RunAsync(() =>
    {
        _probeWanted = enabled || _monitor == MonitorMode.VirtualRecording;
        if (_probeWanted && _probe == null) TryStartProbe();
        if (!_probeWanted) { StopProbe(); _probeErr = null; }
        RebuildMonitor();
        Publish();
    });

    private void RebuildMonitor()
    {
        if (_mic != null) _mic.Sink.Tap = null;
        if (_graph != null) _graph.OutputTap = null;
        if (_probe != null) _probe.Sink.Tap = null;
        var lg = _localGraph;
        lg?.SetMonitor(null);
        if (_monitor == MonitorMode.Off || lg == null) return;

        BufferedFloatProvider? tap = null;
        switch (_monitor)
        {
            case MonitorMode.PhysicalMic when _mic != null:
                tap = new BufferedFloatProvider(_mic.Format.SampleRate, 1, 40, 200);
                _mic.Sink.Tap = tap;
                break;
            case MonitorMode.MixedOutput when _graph != null:
                tap = new BufferedFloatProvider(_graph.WaveFormat.SampleRate, _graph.WaveFormat.Channels, 40, 200);
                _graph.OutputTap = tap;
                break;
            case MonitorMode.VirtualRecording when _probe != null:
                tap = new BufferedFloatProvider(_probe.Format.SampleRate, 1, 40, 200);
                _probe.Sink.Tap = tap;
                break;
        }
        if (tap != null) lg.SetMonitor(ConvertTo(tap, lg.WaveFormat));
    }

    private static ISampleProvider ConvertTo(ISampleProvider source, WaveFormat target)
    {
        ISampleProvider p = source;
        if (p.WaveFormat.SampleRate != target.SampleRate) p = new WdlResamplingSampleProvider(p, target.SampleRate);
        return ChannelAdapter.Wrap(p, target.Channels);
    }

    // ------------------------------------------------------------------
    // Status
    // ------------------------------------------------------------------

    public Task RefreshStatusAsync() => _worker.RunAsync(Publish);

    private void Publish()
    {
        var cfg = _config;
        EndpointStatus output;
        if (!cfg.RoutingEnabled) output = new EndpointStatus(LinkState.Off, cfg.OutputName, null, "Routing is turned off.", null);
        else if (_out != null) output = new EndpointStatus(LinkState.Running, _out.DeviceName, SampleConverter.Describe(_out.Format), null, null);
        else if (cfg.OutputId == null && cfg.OutputName == null) output = new EndpointStatus(LinkState.NotConfigured, null, null, "No virtual output selected.", null);
        else output = new EndpointStatus(LinkState.Error, cfg.OutputName, null, _outErr ?? "Not running.", null);

        EndpointStatus mic;
        if (!cfg.RoutingEnabled) mic = new EndpointStatus(LinkState.Off, cfg.MicName, null, "Routing is turned off.", null);
        else if (_mic != null)
        {
            string? warn = null;
            if (_mic.Sink.DigitalSilenceSeconds > 3)
                warn = "The microphone is delivering pure digital silence. It may be muted (hardware switch or Windows), or Windows is blocking microphone access for desktop apps (Settings > Privacy & security > Microphone).";
            else if (Params.MicMuted) warn = "Microphone is muted in the soundboard.";
            mic = new EndpointStatus(LinkState.Running, _mic.DeviceName, SampleConverter.Describe(_mic.Format), null, warn);
        }
        else if (cfg.MicId == null && cfg.MicName == null) mic = new EndpointStatus(LinkState.NotConfigured, null, null, "No microphone selected.", null);
        else mic = new EndpointStatus(LinkState.Error, cfg.MicName, null, _micErr ?? "Not running.", null);

        EndpointStatus local = _local != null
            ? new EndpointStatus(LinkState.Running, _local.DeviceName, SampleConverter.Describe(_local.Format), null, null)
            : _localErr != null ? new EndpointStatus(LinkState.Error, cfg.LocalName, null, _localErr, null)
            : new EndpointStatus(LinkState.Off, cfg.LocalName, null, null, null);

        EndpointStatus probe = _probe != null
            ? new EndpointStatus(LinkState.Running, _probe.DeviceName, SampleConverter.Describe(_probe.Format), null, null)
            : _probeErr != null ? new EndpointStatus(LinkState.Error, null, null, _probeErr, null)
            : EndpointStatus.Off;

        Volatile.Write(ref _status, new AudioStatus(output, mic, local, probe, _issues.ToList(), _monitor, cfg.RoutingEnabled, BuildDiagnostics()));
    }

    private string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        if (_out != null && _graph != null)
        {
            sb.AppendLine($"Virtual output: {_out.DeviceName}");
            sb.AppendLine($"  Mix format: {SampleConverter.Describe(_out.Format)}, buffer {_out.LatencyMs} ms");
            sb.AppendLine($"  Active sound voices: {_graph.Bus.ActiveVoices}, limiter gain: {PeakMeter.ToDb(_graph.LimiterGain):0.0} dB");
        }
        else sb.AppendLine("Virtual output: not running");
        if (_mic != null)
        {
            var o = _mic.Sink.Output!;
            sb.AppendLine($"Microphone: {_mic.DeviceName}");
            sb.AppendLine($"  Capture format: {SampleConverter.Describe(_mic.Format)}{(_graph != null && _graph.WaveFormat.SampleRate != _mic.Format.SampleRate ? $" (resampled to {_graph.WaveFormat.SampleRate} Hz)" : "")}");
            sb.AppendLine($"  Jitter buffer: {o.BufferedMilliseconds:0} ms, underruns: {o.Underruns}, drift corrections: {o.DriftCorrections}, overflows: {o.OverflowDrops}");
            sb.AppendLine($"  Capture blocks received: {_mic.Sink.BlocksReceived}");
        }
        else sb.AppendLine("Microphone: not running");
        if (_out != null && _mic != null)
            sb.AppendLine($"Estimated added latency: ~{30 + 20 + _out.LatencyMs} ms (capture + jitter buffer + output buffer)");
        sb.AppendLine($"Listening device: {(_local != null ? _local.DeviceName : "closed")}");
        sb.AppendLine($"Verification capture: {(_probe != null ? _probe.DeviceName : "off")}");
        return sb.ToString();
    }

    public static string Friendly(Exception? ex)
    {
        if (ex == null) return "unknown error";
        int hr = ex is COMException com ? com.HResult : ex.HResult;
        return unchecked((uint)hr) switch
        {
            0x88890004 => "the device was removed, disabled or reconfigured",
            0x8889000A => "the device is in exclusive use by another application (disable \"Allow applications to take exclusive control\" in the device's Advanced properties, or close that app)",
            0x80070005 => "access denied. Enable \"Let desktop apps access your microphone\" in Windows Settings > Privacy & security > Microphone",
            0x88890008 => "the device does not support the requested audio format",
            0x88890010 => "the Windows Audio service is not running",
            0x80070490 => "the device was not found",
            0x88890001 => "the audio device was not initialized correctly",
            _ => ex.Message,
        };
    }

    private void Log(string message) => _log?.Invoke(message);

    // ------------------------------------------------------------------
    // Self-test
    // ------------------------------------------------------------------

    /// <summary>
    /// Verifies the real virtual microphone signal by capturing the virtual recording endpoint
    /// (e.g. "CABLE Output") while (1) a test tone is played through the soundboard bus and
    /// (2) the user speaks. Returns a human-readable report. Nothing is sent anywhere else.
    /// </summary>
    public async Task<string> RunSelfTestAsync(CancellationToken ct = default)
    {
        var report = new StringBuilder();
        var graph = Graph;
        if (graph == null)
        {
            report.AppendLine("FAIL  Virtual output is not running: " + (Status.Output.Error ?? "unknown reason"));
            return report.ToString();
        }

        bool startedProbe = false;
        if (ProbeSink == null)
        {
            await SetProbeEnabledAsync(true).ConfigureAwait(false);
            startedProbe = true;
        }
        var probe = ProbeSink;
        if (probe == null)
        {
            report.AppendLine("FAIL  Cannot open the virtual recording endpoint: " + (Status.Probe.Error ?? "unknown reason"));
            return report.ToString();
        }

        string probeName = Status.Probe.DeviceName ?? "virtual recording device";
        int probeRate = probe.CaptureFormat.SampleRate;
        var probeTap = new BufferedFloatProvider(probeRate, 1, 0, 8000, 10000);
        var micSink = MicSink;
        BufferedFloatProvider? micTap = micSink != null ? new BufferedFloatProvider(micSink.CaptureFormat.SampleRate, 1, 0, 8000, 10000) : null;

        try
        {
            probe.AnalysisTap = probeTap;
            await Task.Delay(300, ct).ConfigureAwait(false);
            var discard = new float[probeRate * 10];
            probeTap.Drain(discard);

            // ---- 1. Soundboard path ----
            const double toneHz = 997;
            const float toneAmp = 0.3f;
            var tone = AudioClip.CreateTone(graph.WaveFormat.SampleRate, graph.WaveFormat.Channels, toneHz, 0.8, toneAmp);
            graph.Bus.Play(new CachedVoice(tone, "__selftest__", "Self-test tone"));
            await Task.Delay(1300, ct).ConfigureAwait(false);
            var buf = new float[probeRate * 10];
            int n = probeTap.Drain(buf);
            double amp = SignalAnalysis.ToneAmplitude(buf.AsSpan(0, n), probeRate, toneHz);
            double expected = toneAmp * (Params.SoundboardMuted ? 0 : Params.SoundboardGain) * Params.MasterGain;
            if (expected < 0.005)
                report.AppendLine("SKIP  Soundboard path: the soundboard is muted or its volume (or master) is at zero.");
            else if (amp >= Math.Max(0.01, expected * 0.25))
                report.AppendLine($"PASS  Soundboard audio reached \"{probeName}\" (test tone at {PeakMeter.ToDb((float)amp):0} dBFS).");
            else
                report.AppendLine($"FAIL  The test tone was not detected on \"{probeName}\" (measured {PeakMeter.ToDb((float)amp):0} dBFS). The virtual output and recording device may not belong to the same cable.");

            // ---- 2. Microphone path ----
            micSink = MicSink;
            if (micSink == null || micTap == null)
            {
                report.AppendLine("FAIL  Microphone path: no microphone is running. " + (Status.Mic.Error ?? ""));
            }
            else
            {
                micSink.AnalysisTap = micTap;
                probeTap.Drain(discard);
                micTap.Drain(new float[micTap.WaveFormat.SampleRate * 10]);
                await Task.Delay(2000, ct).ConfigureAwait(false);

                var micBuf = new float[micTap.WaveFormat.SampleRate * 10];
                int mn = micTap.Drain(micBuf);
                int pn = probeTap.Drain(buf);
                float micPeak = SignalAnalysis.Peak(micBuf.AsSpan(0, mn));
                float probePeak = SignalAnalysis.Peak(buf.AsSpan(0, pn));
                float expectedVoice = micPeak * Params.MicGain * Params.MasterGain;

                if (mn == 0)
                    report.AppendLine("FAIL  Microphone path: the microphone delivered no audio during the test.");
                else if (micSink.DigitalSilenceSeconds > 1.5)
                    report.AppendLine("FAIL  Microphone path: the microphone delivers pure digital silence (muted, or blocked by Windows privacy settings).");
                else if (Params.MicMuted)
                    report.AppendLine("SKIP  Microphone path: the microphone is muted in the soundboard (sound effects are unaffected).");
                else if (expectedVoice < 0.02)
                    report.AppendLine($"INCONCLUSIVE  Microphone is running (peak {PeakMeter.ToDb(micPeak):0} dBFS) but was too quiet to verify. Speak continuously during the test, or raise the microphone gain.");
                else if (probePeak >= Math.Min(expectedVoice, 0.89f) * 0.3f)
                    report.AppendLine($"PASS  Your live microphone reached \"{probeName}\" (peak {PeakMeter.ToDb(probePeak):0} dBFS).");
                else
                    report.AppendLine($"FAIL  Microphone audio is in the mixer (peak {PeakMeter.ToDb(expectedVoice):0} dBFS) but was not detected on \"{probeName}\" (peak {PeakMeter.ToDb(probePeak):0} dBFS).");
            }

            report.AppendLine();
            report.AppendLine($"In CS2, select \"{probeName}\" as the microphone (or set it as the Windows default recording device).");
        }
        catch (OperationCanceledException)
        {
            report.AppendLine("Test cancelled.");
        }
        finally
        {
            probe.AnalysisTap = null;
            if (micSink != null) micSink.AnalysisTap = null;
            if (startedProbe) await SetProbeEnabledAsync(false).ConfigureAwait(false);
        }
        return report.ToString();
    }

    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _worker.Invoke(() =>
        {
            _disposed = true;
            _watchdog?.Dispose();
            StopMic();
            StopOutput();
            StopLocal();
            StopProbe();
            _catalog?.Dispose();
            _catalog = null;
            Log("Audio service shut down; all devices released.");
        }, TimeSpan.FromSeconds(5));
        _disposed = true;
        _worker.Dispose();
    }
}
