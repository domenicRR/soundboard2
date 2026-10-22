using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CS2Soundboard.Core.Audio;
using CS2Soundboard.Core.Settings;
using CS2Soundboard.Services;

namespace CS2Soundboard.ViewModels;

public sealed partial class MainViewModel
{
    private bool _suppressDeviceApply;
    private DeviceOption? _selectedMic;
    private DeviceOption? _selectedOutput;
    private DeviceOption? _selectedLocal;
    private bool _vbCableDetected = true;
    private string _virtualRecordingName = "CABLE Output (VB-Audio Virtual Cable)";
    private IReadOnlyList<AudioDeviceInfo> _captureInfos = Array.Empty<AudioDeviceInfo>();
    private IReadOnlyList<AudioDeviceInfo> _renderInfos = Array.Empty<AudioDeviceInfo>();

    public ObservableCollection<DeviceOption> CaptureDevices { get; } = new();
    public ObservableCollection<DeviceOption> OutputDevices { get; } = new();
    public ObservableCollection<DeviceOption> LocalDevices { get; } = new();

    public ICommand RefreshDevicesCommand { get; private set; } = null!;
    public ICommand RestartAudioCommand { get; private set; } = null!;
    public ICommand PlayTestToneCommand { get; private set; } = null!;
    public ICommand SelfTestCommand { get; private set; } = null!;
    public ICommand OpenUrlCommand { get; private set; } = null!;
    public ICommand OpenSettingsFolderCommand { get; private set; } = null!;
    public ICommand ToggleMicMuteCommand { get; private set; } = null!;
    public ICommand ToggleSoundboardMuteCommand { get; private set; } = null!;

    private void InitMixerCommands()
    {
        RefreshDevicesCommand = new AsyncCommand(_ => RefreshDevicesSafeAsync(), ReportException);
        RestartAudioCommand = new AsyncCommand(async _ =>
        {
            AddEvent("Restarting audio devices...");
            await _audio.RestartAllAsync();
            await RefreshDevicesSafeAsync();
            RefreshStatus();
        }, ReportException);
        PlayTestToneCommand = new RelayCommand(() =>
        {
            var err = _playback.PlayTestTone();
            AddEvent(err ?? "Playing a 440 Hz test tone into the virtual microphone (watch the CS2 mic test or the 'Virtual recording device' meter).",
                err == null ? StatusLevel.Ok : StatusLevel.Error);
        });
        SelfTestCommand = new AsyncCommand(_ => RunSelfTestAsync(), ReportException);
        OpenUrlCommand = new RelayCommand(p => OpenShell(p as string));
        OpenSettingsFolderCommand = new RelayCommand(() => OpenShell(_store.Directory));
        ToggleMicMuteCommand = new RelayCommand(() => MicMuted = !MicMuted);
        ToggleSoundboardMuteCommand = new RelayCommand(() => SoundboardMuted = !SoundboardMuted);
    }

    private void OpenShell(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            if (target.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo("control.exe", target) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddEvent($"Could not open {target}: {ex.Message}", StatusLevel.Error);
        }
    }

    // ------------------------------------------------------------------
    // Devices
    // ------------------------------------------------------------------

    public DeviceOption? SelectedMic
    {
        get => _selectedMic;
        set
        {
            if (!SetProperty(ref _selectedMic, value) || _suppressDeviceApply || value == null) return;
            _s.MicDeviceId = value.Id;
            _s.MicDeviceName = value.Name;
            SaveSoon();
            WarnIfFeedback();
            _ = ApplyAudioConfigAsync();
        }
    }

    public DeviceOption? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (!SetProperty(ref _selectedOutput, value) || _suppressDeviceApply || value == null) return;
            _s.OutputDeviceId = value.Id;
            _s.OutputDeviceName = value.Name;
            SaveSoon();
            UpdateVirtualRecordingName();
            WarnIfFeedback();
            _ = ApplyAudioConfigAsync();
        }
    }

    public DeviceOption? SelectedLocal
    {
        get => _selectedLocal;
        set
        {
            if (!SetProperty(ref _selectedLocal, value) || _suppressDeviceApply || value == null) return;
            _s.LocalDeviceId = value.Id;
            _s.LocalDeviceName = value.Name;
            SaveSoon();
            WarnIfFeedback();
            _ = ApplyAudioConfigAsync();
        }
    }

    public bool VbCableDetected
    {
        get => _vbCableDetected;
        private set => SetProperty(ref _vbCableDetected, value);
    }

    /// <summary>The recording endpoint CS2 should use (recording side of the selected virtual output).</summary>
    public string VirtualRecordingName
    {
        get => _virtualRecordingName;
        private set => SetProperty(ref _virtualRecordingName, value);
    }

    private async Task RefreshDevicesSafeAsync()
    {
        if (_disposed) return;
        DeviceLists lists;
        try
        {
            lists = await _audio.GetDevicesAsync();
        }
        catch (Exception ex)
        {
            AddEvent($"Could not list audio devices: {ex.Message}", StatusLevel.Error);
            return;
        }

        _captureInfos = lists.Captures;
        _renderInfos = lists.Renders;
        VbCableDetected = lists.Renders.Any(r => r.IsVbCable) || lists.Captures.Any(c => c.IsVbCable);

        // First run: preselect the Windows default microphone (if physical) and VB-CABLE Input (if installed).
        if (!_s.DevicesInitialized)
        {
            if (_s.MicDeviceId == null)
            {
                // Prefer the Windows default recording device, unless it is itself virtual.
                var defMic = lists.DefaultCapture is { LooksVirtual: false } d ? d : lists.Captures.FirstOrDefault(c => !c.LooksVirtual);
                if (defMic != null)
                {
                    _s.MicDeviceId = defMic.Id;
                    _s.MicDeviceName = defMic.Name;
                    AddEvent($"Using \"{defMic.Name}\" as your physical microphone. You can change it in the Audio Mixer tab.");
                }
            }
            _s.DevicesInitialized = true;
            SaveSoon();
        }
        if (_s.OutputDeviceId == null && _s.OutputDeviceName == null)
        {
            var cable = lists.Renders.FirstOrDefault(r => r.IsVbCable && r.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
            if (cable != null)
            {
                _s.OutputDeviceId = cable.Id;
                _s.OutputDeviceName = cable.Name;
                SaveSoon();
                AddEvent($"Detected VB-CABLE: using \"{cable.Name}\" as the virtual microphone output.");
            }
        }

        _suppressDeviceApply = true;
        try
        {
            Fill(CaptureDevices, new DeviceOption(null, null, "(No microphone - sound effects only)", true),
                lists.Captures, _s.MicDeviceId, _s.MicDeviceName, d => d.LooksVirtual ? "  [virtual device - not a physical mic]" : "");
            SelectedMic = Pick(CaptureDevices, _s.MicDeviceId, _s.MicDeviceName);

            Fill(OutputDevices, new DeviceOption(null, null, "(Not selected)", true),
                lists.Renders, _s.OutputDeviceId, _s.OutputDeviceName, d => d.IsVbCable ? "  [VB-CABLE - recommended]" : d.LooksVirtual ? "  [virtual]" : "  [physical - not recommended]");
            SelectedOutput = Pick(OutputDevices, _s.OutputDeviceId, _s.OutputDeviceName);

            var defName = lists.DefaultRender?.Name ?? "none";
            Fill(LocalDevices, new DeviceOption(null, null, $"Windows default playback device ({defName})", true),
                lists.Renders, _s.LocalDeviceId, _s.LocalDeviceName, d => d.LooksVirtual ? "  [virtual - not recommended]" : "");
            SelectedLocal = Pick(LocalDevices, _s.LocalDeviceId, _s.LocalDeviceName);
        }
        finally
        {
            _suppressDeviceApply = false;
        }
        UpdateVirtualRecordingName();
    }

    private static void Fill(ObservableCollection<DeviceOption> target, DeviceOption noneOption, IEnumerable<AudioDeviceInfo> devices,
        string? savedId, string? savedName, Func<AudioDeviceInfo, string> suffix)
    {
        var list = new List<DeviceOption> { noneOption };
        list.AddRange(devices.Select(d => new DeviceOption(d.Id, d.Name, d.Name + suffix(d), true)));
        bool savedPresent = savedId == null && savedName == null
                            || list.Any(o => o.Id == savedId && savedId != null)
                            || list.Any(o => savedName != null && string.Equals(o.Name, savedName, StringComparison.OrdinalIgnoreCase));
        if (!savedPresent)
            list.Add(new DeviceOption(savedId, savedName, $"{savedName ?? savedId}  [NOT AVAILABLE - disconnected or disabled]", false));

        target.Clear();
        foreach (var o in list) target.Add(o);
    }

    private static DeviceOption? Pick(IEnumerable<DeviceOption> options, string? id, string? name)
    {
        var list = options.ToList();
        if (id == null && name == null) return list.FirstOrDefault();
        return list.FirstOrDefault(o => o.Id != null && o.Id == id)
               ?? list.FirstOrDefault(o => o.Name != null && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault();
    }

    private void UpdateVirtualRecordingName()
    {
        var output = _renderInfos.FirstOrDefault(r => r.Id == _s.OutputDeviceId);
        var paired = output == null ? null : VirtualDeviceRules.FindRecordingSide(output, _captureInfos);
        VirtualRecordingName = paired?.Name ?? "CABLE Output (VB-Audio Virtual Cable)";
    }

    private void WarnIfFeedback()
    {
        var mic = _captureInfos.FirstOrDefault(c => c.Id == _s.MicDeviceId);
        var output = _renderInfos.FirstOrDefault(r => r.Id == _s.OutputDeviceId);
        var local = _s.LocalDeviceId == null ? null : _renderInfos.FirstOrDefault(r => r.Id == _s.LocalDeviceId);
        var errors = RoutingValidator.Validate(mic, output, local, _captureInfos).Where(i => i.IsError && !i.Message.StartsWith("No virtual output", StringComparison.Ordinal)).ToList();
        foreach (var e in errors)
        {
            _ui.ShowError("Unsafe audio routing", e.Message);
            AddEvent(e.Message, StatusLevel.Error);
        }
    }

    private Task ApplyAudioConfigAsync()
        => _audio.ApplyConfigAsync(new AudioConfig(
            _s.MicDeviceId, _s.MicDeviceName,
            _s.OutputDeviceId, _s.OutputDeviceName,
            _s.LocalDeviceId, _s.LocalDeviceName,
            _s.OutputLatencyMs, _s.RoutingEnabled))
            .ContinueWith(t =>
            {
                if (t.Exception != null) _dispatcher.BeginInvoke(new Action(() => ReportException(t.Exception.GetBaseException())));
                else _dispatcher.BeginInvoke(new Action(RefreshStatus));
            });

    // ------------------------------------------------------------------
    // Gains and mutes
    // ------------------------------------------------------------------

    public double MicGainPercent
    {
        get => Math.Round(_s.MicGain * 100);
        set
        {
            _s.MicGain = Math.Clamp(value / 100.0, 0, 4);
            _params.MicGain = (float)_s.MicGain;
            OnPropertyChanged();
            SaveSoon();
        }
    }

    public double SoundboardVolumePercent
    {
        get => Math.Round(_s.SoundboardVolume * 100);
        set
        {
            _s.SoundboardVolume = Math.Clamp(value / 100.0, 0, 2);
            _params.SoundboardGain = (float)_s.SoundboardVolume;
            OnPropertyChanged();
            SaveSoon();
        }
    }

    public double MasterVolumePercent
    {
        get => Math.Round(_s.MasterVolume * 100);
        set
        {
            _s.MasterVolume = Math.Clamp(value / 100.0, 0, 2);
            _params.MasterGain = (float)_s.MasterVolume;
            OnPropertyChanged();
            SaveSoon();
        }
    }

    public double LocalVolumePercent
    {
        get => Math.Round(_s.LocalVolume * 100);
        set
        {
            _s.LocalVolume = Math.Clamp(value / 100.0, 0, 2);
            _audio.LocalVolume = (float)_s.LocalVolume;
            OnPropertyChanged();
            SaveSoon();
        }
    }

    public bool MicMuted
    {
        get => _s.MicMuted;
        set
        {
            if (_s.MicMuted == value) return;
            _s.MicMuted = value;
            _params.MicMuted = value;
            OnPropertyChanged();
            SaveSoon();
            AddEvent(value ? "Microphone muted (sound effects still play)." : "Microphone unmuted.", value ? StatusLevel.Warning : StatusLevel.Ok);
            RefreshStatus();
        }
    }

    public bool SoundboardMuted
    {
        get => _s.SoundboardMuted;
        set
        {
            if (_s.SoundboardMuted == value) return;
            _s.SoundboardMuted = value;
            _params.SoundboardMuted = value;
            OnPropertyChanged();
            SaveSoon();
            AddEvent(value ? "Soundboard muted (your voice still goes through)." : "Soundboard unmuted.", value ? StatusLevel.Warning : StatusLevel.Ok);
            RefreshStatus();
        }
    }

    public bool RoutingEnabled
    {
        get => _s.RoutingEnabled;
        set
        {
            if (_s.RoutingEnabled == value) return;
            _s.RoutingEnabled = value;
            OnPropertyChanged();
            SaveSoon();
            AddEvent(value ? "Audio routing enabled." : "Audio routing disabled: nothing is sent to the virtual microphone.", value ? StatusLevel.Ok : StatusLevel.Warning);
            _ = ApplyAudioConfigAsync();
        }
    }

    public IReadOnlyList<int> LatencyOptions { get; } = new[] { 10, 20, 30, 50, 80, 120 };

    public int OutputLatencyMs
    {
        get => _s.OutputLatencyMs;
        set
        {
            if (_s.OutputLatencyMs == value) return;
            _s.OutputLatencyMs = value;
            OnPropertyChanged();
            SaveSoon();
            _ = ApplyAudioConfigAsync();
        }
    }

    // ------------------------------------------------------------------
    // Meters
    // ------------------------------------------------------------------

    private double _micDb = -120, _outDb = -120, _probeDb = -120;
    private double _micLevel, _outputLevel, _probeLevel;
    private string _micLevelText = "", _outputLevelText = "", _probeLevelText = "";

    public double MicLevel { get => _micLevel; private set => SetProperty(ref _micLevel, value); }
    public double OutputLevel { get => _outputLevel; private set => SetProperty(ref _outputLevel, value); }
    public double ProbeLevel { get => _probeLevel; private set => SetProperty(ref _probeLevel, value); }
    public string MicLevelText { get => _micLevelText; private set => SetProperty(ref _micLevelText, value); }
    public string OutputLevelText { get => _outputLevelText; private set => SetProperty(ref _outputLevelText, value); }
    public string ProbeLevelText { get => _probeLevelText; private set => SetProperty(ref _probeLevelText, value); }

    private void UpdateMeters()
    {
        Meter(_audio.MicSink?.Meter, ref _micDb, v => MicLevel = v, t => MicLevelText = t);
        Meter(_audio.Graph?.OutputMeter, ref _outDb, v => OutputLevel = v, t => OutputLevelText = t);
        Meter(_audio.ProbeSink?.Meter, ref _probeDb, v => ProbeLevel = v, t => ProbeLevelText = t);
    }

    private static void Meter(PeakMeter? meter, ref double shownDb, Action<double> setLevel, Action<string> setText)
    {
        double db = meter == null ? -120 : PeakMeter.ToDb(meter.ReadAndReset());
        shownDb = Math.Max(db, shownDb - 1.5); // ~45 dB/s fall-off
        setLevel(Math.Clamp((shownDb + 60) / 60, 0, 1));
        setText(meter == null ? "off" : shownDb <= -60 ? "< -60 dB" : $"{shownDb:0} dB");
    }

    // ------------------------------------------------------------------
    // Status
    // ------------------------------------------------------------------

    private AudioStatus? _lastStatus;
    private string _routingHeadline = "Starting audio...";
    private string _routingShort = "starting";
    private StatusLevel _routingLevel = StatusLevel.Warning;
    private string _outputStatus = "", _micStatus = "", _localStatus = "", _probeStatus = "", _issuesText = "", _diagnostics = "";

    public string RoutingHeadline { get => _routingHeadline; private set => SetProperty(ref _routingHeadline, value); }
    public string RoutingShort { get => _routingShort; private set { if (SetProperty(ref _routingShort, value)) OnPropertyChanged(nameof(TrayTooltip)); } }
    public StatusLevel RoutingLevel { get => _routingLevel; private set => SetProperty(ref _routingLevel, value); }
    public string OutputStatus { get => _outputStatus; private set => SetProperty(ref _outputStatus, value); }
    public string MicStatus { get => _micStatus; private set => SetProperty(ref _micStatus, value); }
    public string LocalStatus { get => _localStatus; private set => SetProperty(ref _localStatus, value); }
    public string ProbeStatus { get => _probeStatus; private set => SetProperty(ref _probeStatus, value); }
    public string IssuesText { get => _issuesText; private set { if (SetProperty(ref _issuesText, value)) OnPropertyChanged(nameof(HasIssues)); } }
    public bool HasIssues => !string.IsNullOrEmpty(_issuesText);
    public string Diagnostics { get => _diagnostics; private set => SetProperty(ref _diagnostics, value); }

    private void RefreshStatus()
    {
        if (_disposed) return;
        var st = _audio.Status;
        var prev = _lastStatus;
        _lastStatus = st;

        OutputStatus = Describe(st.Output, "Virtual output");
        MicStatus = Describe(st.Mic, "Microphone");
        LocalStatus = Describe(st.Local, "Listening device");
        ProbeStatus = st.Probe.State switch
        {
            LinkState.Running => $"Listening to {st.Probe.DeviceName}",
            LinkState.Error => st.Probe.Error ?? "Error",
            _ => "Off (enable verification to measure what CS2 receives)",
        };
        IssuesText = string.Join("\n", st.Issues.Select(i => (i.IsError ? "ERROR: " : "Warning: ") + i.Message));
        Diagnostics = st.Diagnostics;

        string outName = st.Output.DeviceName ?? "virtual output";
        if (!st.RoutingEnabled)
        {
            RoutingLevel = StatusLevel.Error;
            RoutingHeadline = "Routing is OFF: nothing is being sent to CS2.";
            RoutingShort = "routing off";
        }
        else if (st.Output.State != LinkState.Running)
        {
            RoutingLevel = StatusLevel.Error;
            RoutingHeadline = "NOT sending audio to CS2: " + (st.Output.Error ?? "virtual output not running.");
            RoutingShort = "NOT sending to CS2";
        }
        else if (st.Mic.State != LinkState.Running)
        {
            RoutingLevel = StatusLevel.Error;
            RoutingHeadline = "Your microphone is NOT being forwarded (sound effects only): " + (st.Mic.Error ?? "");
            RoutingShort = "mic NOT forwarded";
        }
        else if (st.Mic.Warning != null)
        {
            RoutingLevel = StatusLevel.Warning;
            RoutingHeadline = $"Forwarding to {outName}. {st.Mic.Warning}";
            RoutingShort = MicMuted ? "live (mic muted)" : "live (check mic)";
        }
        else
        {
            RoutingLevel = StatusLevel.Ok;
            RoutingHeadline = $"Live: microphone + soundboard → {outName}" + (SoundboardMuted ? " (soundboard muted)" : "");
            RoutingShort = SoundboardMuted ? "live (soundboard muted)" : "live";
        }

        // Announce transitions (also as tray balloons while hidden).
        if (prev != null)
        {
            if (prev.Output.State == LinkState.Running && st.Output.State == LinkState.Error)
                AddEvent(st.Output.Error ?? "Virtual output stopped.", StatusLevel.Error, notify: true);
            else if (prev.Output.State == LinkState.Error && st.Output.State == LinkState.Running)
                AddEvent($"Virtual output running again ({st.Output.DeviceName}).", StatusLevel.Ok, notify: true);
            if (prev.Mic.State == LinkState.Running && st.Mic.State == LinkState.Error)
                AddEvent(st.Mic.Error ?? "Microphone stopped.", StatusLevel.Error, notify: true);
            else if (prev.Mic.State == LinkState.Error && st.Mic.State == LinkState.Running)
                AddEvent($"Microphone connected ({st.Mic.DeviceName}).", StatusLevel.Ok, notify: true);
            if (prev.Mic.Warning == null && st.Mic.Warning != null && !MicMuted)
                AddEvent(st.Mic.Warning, StatusLevel.Warning, notify: true);
            if (prev.Local.State != LinkState.Error && st.Local.State == LinkState.Error)
                AddEvent(st.Local.Error ?? "Listening device error.", StatusLevel.Warning);
        }
        else
        {
            if (st.Output.State == LinkState.Error) AddEvent(st.Output.Error ?? "Virtual output not running.", StatusLevel.Error);
            if (st.Mic.State == LinkState.Error) AddEvent(st.Mic.Error ?? "Microphone not running.", StatusLevel.Error);
        }

        PreloadIfFormatChanged();
        TrayStateChanged?.Invoke();
    }

    private static string Describe(EndpointStatus s, string what) => s.State switch
    {
        LinkState.Running => $"Running: {s.DeviceName}  ({s.Format})" + (s.Warning != null ? "\n" + s.Warning : ""),
        LinkState.Error => "Error: " + (s.Error ?? "unknown"),
        LinkState.NotConfigured => s.Error ?? $"{what} not selected.",
        _ => s.Error ?? "Off",
    };

    // ------------------------------------------------------------------
    // Monitoring, verification and self-test
    // ------------------------------------------------------------------

    public IReadOnlyList<EnumOption<MonitorMode>> MonitorModes { get; } = new[]
    {
        new EnumOption<MonitorMode>(MonitorMode.Off, "Off", "Do not monitor anything."),
        new EnumOption<MonitorMode>(MonitorMode.PhysicalMic, "My physical microphone (raw input)", "Hear your microphone exactly as captured, before gain/mute and mixing."),
        new EnumOption<MonitorMode>(MonitorMode.MixedOutput, "Mixed output (voice + sounds sent to the cable)", "Hear the mix the soundboard sends to the virtual output."),
        new EnumOption<MonitorMode>(MonitorMode.VirtualRecording, "Virtual recording device (exactly what CS2 receives)", "Captures the virtual recording endpoint (e.g. CABLE Output) and plays it on your listening device."),
    };

    private EnumOption<MonitorMode>? _selectedMonitor;

    public EnumOption<MonitorMode> SelectedMonitorMode
    {
        get => _selectedMonitor ??= MonitorModes[0];
        set
        {
            if (value == null || !SetProperty(ref _selectedMonitor, value)) return;
            if (value.Value != MonitorMode.Off)
                AddEvent("Monitoring enabled. Use headphones: monitoring through speakers can be picked up by your microphone and cause echo.", StatusLevel.Warning);
            _ = _audio.SetMonitorModeAsync(value.Value).ContinueWith(_ => _dispatcher.BeginInvoke(new Action(RefreshStatus)));
        }
    }

    private bool _verifyEnabled;

    /// <summary>Captures the virtual recording endpoint to show what CS2 actually receives.</summary>
    public bool VerifyEnabled
    {
        get => _verifyEnabled;
        set
        {
            if (!SetProperty(ref _verifyEnabled, value)) return;
            _ = _audio.SetProbeEnabledAsync(value).ContinueWith(_ => _dispatcher.BeginInvoke(new Action(RefreshStatus)));
        }
    }

    private bool _selfTestRunning;
    private string _selfTestReport = "";

    public bool SelfTestRunning { get => _selfTestRunning; private set => SetProperty(ref _selfTestRunning, value); }
    public string SelfTestReport { get => _selfTestReport; private set => SetProperty(ref _selfTestReport, value); }

    private async Task RunSelfTestAsync()
    {
        SelfTestRunning = true;
        SelfTestReport = "Running... keep speaking into your microphone for about 4 seconds.";
        try
        {
            var report = await _audio.RunSelfTestAsync();
            SelfTestReport = report;
            AddEvent("Routing self-test finished.");
        }
        finally
        {
            SelfTestRunning = false;
            RefreshStatus();
        }
    }

    // ------------------------------------------------------------------
    // Settings tab
    // ------------------------------------------------------------------

    public IReadOnlyList<EnumOption<PlaybackMode>> PlaybackModes { get; } = new[]
    {
        new EnumOption<PlaybackMode>(PlaybackMode.Overlap, "Overlap", "Sounds play on top of each other, including multiple copies of the same sound."),
        new EnumOption<PlaybackMode>(PlaybackMode.StopPrevious, "Stop previous", "Triggering a sound stops everything that is currently playing."),
        new EnumOption<PlaybackMode>(PlaybackMode.NoDuplicates, "Overlap, no duplicates", "Different sounds overlap; re-triggering a playing sound restarts it instead of adding a second copy."),
    };

    public EnumOption<PlaybackMode> SelectedPlaybackMode
    {
        get => PlaybackModes.First(m => m.Value == _s.PlaybackMode);
        set
        {
            if (value == null || value.Value == _s.PlaybackMode) return;
            _s.PlaybackMode = value.Value;
            OnPropertyChanged();
            SaveSoon();
        }
    }

    public bool RetriggerStops
    {
        get => _s.RetriggerStops;
        set { if (_s.RetriggerStops == value) return; _s.RetriggerStops = value; OnPropertyChanged(); SaveSoon(); }
    }

    public bool HearSoundsLocally
    {
        get => _s.HearSoundsLocally;
        set
        {
            if (_s.HearSoundsLocally == value) return;
            _s.HearSoundsLocally = value;
            _audio.KeepLocalAlive = value;
            OnPropertyChanged();
            SaveSoon();
        }
    }

    public bool MinimizeToTray
    {
        get => _s.MinimizeToTray;
        set { if (_s.MinimizeToTray == value) return; _s.MinimizeToTray = value; OnPropertyChanged(); SaveSoon(); }
    }

    public bool CloseToTray
    {
        get => _s.CloseToTray;
        set { if (_s.CloseToTray == value) return; _s.CloseToTray = value; OnPropertyChanged(); SaveSoon(); }
    }

    public bool StartMinimized
    {
        get => _s.StartMinimized;
        set { if (_s.StartMinimized == value) return; _s.StartMinimized = value; OnPropertyChanged(); SaveSoon(); }
    }

    public bool StartWithWindows
    {
        get => _s.StartWithWindows;
        set
        {
            if (_s.StartWithWindows == value) return;
            var err = StartupRegistration.SetEnabled(value);
            if (err != null)
            {
                _ui.ShowError("Start with Windows", err);
                OnPropertyChanged();
                return;
            }
            _s.StartWithWindows = value;
            OnPropertyChanged();
            SaveSoon();
        }
    }

    private void InitSettingsCommands()
    {
        // Keep the registry entry in sync with the saved preference (e.g. after the exe moved).
        if (_s.StartWithWindows) StartupRegistration.SetEnabled(true);
    }
}
