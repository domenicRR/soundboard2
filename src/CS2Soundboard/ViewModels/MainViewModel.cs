using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using CS2Soundboard.Core.Audio;
using CS2Soundboard.Core.Hotkeys;
using CS2Soundboard.Core.Settings;
using CS2Soundboard.Services;

namespace CS2Soundboard.ViewModels;

public sealed record HotkeyDialogResult(bool Cancelled, bool Cleared, Hotkey Hotkey);

/// <summary>UI services the view model needs from the window layer.</summary>
public interface IUiServices
{
    string? PickFolder(string? initialDirectory);
    HotkeyDialogResult CaptureHotkey(string title, Hotkey current);
    bool Confirm(string title, string message);
    void ShowError(string title, string message);
    /// <summary>Non-blocking notice: a tray balloon when the window is hidden.</summary>
    void Notify(string title, string message, bool isError);
    bool IsWindowVisible { get; }
}

public enum StatusLevel { Ok, Warning, Error }

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store;
    private readonly AppSettings _s;
    private readonly HotkeyManager _hotkeys;
    private readonly IUiServices _ui;
    private readonly MixerParameters _params = new();
    private readonly AudioService _audio;
    private readonly ClipCache _cache = new();
    private readonly PlaybackController _playback;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly Dispatcher _dispatcher;
    private int _tick;
    private bool _disposed;

    public MainViewModel(SettingsStore store, AppSettings settings, HotkeyManager hotkeys, IUiServices ui)
    {
        _store = store;
        _s = settings;
        _hotkeys = hotkeys;
        _ui = ui;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _params.MicGain = (float)_s.MicGain;
        _params.SoundboardGain = (float)_s.SoundboardVolume;
        _params.MasterGain = (float)_s.MasterVolume;
        _params.MicMuted = _s.MicMuted;
        _params.SoundboardMuted = _s.SoundboardMuted;

        _audio = new AudioService(_params, msg => Log.Info(msg));
        _audio.LocalVolume = (float)_s.LocalVolume;
        _audio.KeepLocalAlive = _s.HearSoundsLocally;
        _audio.DevicesChanged += () => _dispatcher.BeginInvoke(new Action(async () => await RefreshDevicesSafeAsync()));
        _playback = new PlaybackController(_audio, _cache);

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _uiTimer.Tick += (_, _) => OnUiTick();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        _hotkeys.Pressed += OnHotkeyPressed;

        CommandHotkeys = new ObservableCollection<CommandHotkeyViewModel>
        {
            new(StopAllBinding, "Stop all sounds", "Immediately stops every sound effect."),
            new(MicMuteBinding, "Toggle microphone mute", "Mutes/unmutes your voice in CS2. Sound effects keep working."),
            new(SoundboardMuteBinding, "Toggle soundboard mute", "Mutes/unmutes sound effects. Your voice keeps working."),
        };

        InitLibraryCommands();
        InitMixerCommands();
        InitSettingsCommands();
    }

    public string SettingsFilePath => _store.FilePath;
    public string SettingsDirectory => _store.Directory;

    // ------------------------------------------------------------------
    // Startup / shutdown
    // ------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        try
        {
            await _audio.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Audio initialization failed", ex);
            _ui.ShowError("Audio initialization failed",
                $"Windows audio could not be initialized: {ex.Message}\n\nCheck that the Windows Audio service is running and restart the application.");
        }

        await RefreshDevicesSafeAsync();
        await ApplyAudioConfigAsync();
        LoadCommandHotkeys();
        await RescanAsync();
        _uiTimer.Start();
        AddEvent("Soundboard started.");
    }

    /// <summary>Stops audio, releases devices and hotkeys, and saves settings.</summary>
    public void Shutdown()
    {
        if (_disposed) return;
        _disposed = true;
        _uiTimer.Stop();
        _saveTimer.Stop();
        try { _playback.StopAll(); } catch { /* ignore */ }
        SaveNow();
        DisposeWatcher();
        _hotkeys.Pressed -= OnHotkeyPressed;
        try { _audio.Dispose(); } catch (Exception ex) { Log.Error("Audio shutdown error", ex); }
        Log.Info("Shutdown complete.");
    }

    public void Dispose() => Shutdown();

    private void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        try
        {
            _store.Save(_s);
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
            AddEvent($"Could not save settings: {ex.Message}", StatusLevel.Error);
        }
    }

    // ------------------------------------------------------------------
    // Event log / notifications
    // ------------------------------------------------------------------

    public ObservableCollection<string> Events { get; } = new();

    private string _lastMessage = "";
    private StatusLevel _lastMessageLevel;

    public string LastMessage
    {
        get => _lastMessage;
        private set => SetProperty(ref _lastMessage, value);
    }

    public StatusLevel LastMessageLevel
    {
        get => _lastMessageLevel;
        private set => SetProperty(ref _lastMessageLevel, value);
    }

    private void AddEvent(string message, StatusLevel level = StatusLevel.Ok, bool notify = false)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        Events.Insert(0, line);
        while (Events.Count > 200) Events.RemoveAt(Events.Count - 1);
        LastMessage = message;
        LastMessageLevel = level;
        if (level == StatusLevel.Error) Log.Warn(message); else Log.Info(message);
        if (notify && !_ui.IsWindowVisible) _ui.Notify("CS2 Soundboard", message, level == StatusLevel.Error);
    }

    private void ReportException(Exception ex)
    {
        Log.Error("Command failed", ex);
        AddEvent(ex is AudioDecodeException ? ex.Message : $"Unexpected error: {ex.Message}", StatusLevel.Error, notify: true);
    }

    // ------------------------------------------------------------------
    // UI timer: meters, playback states, status
    // ------------------------------------------------------------------

    private void OnUiTick()
    {
        if (_disposed) return;
        try
        {
            UpdateMeters();
            UpdatePlaybackStates();
            if (++_tick % 8 == 0) RefreshStatus();
        }
        catch (Exception ex)
        {
            Log.Error("UI tick failed", ex);
        }
    }

    private readonly System.Collections.Generic.HashSet<SoundItemViewModel> _shownPlaying = new();

    private void UpdatePlaybackStates()
    {
        _playback.Pump();
        var playing = new System.Collections.Generic.Dictionary<SoundItemViewModel, double>();
        var previewing = new System.Collections.Generic.HashSet<SoundItemViewModel>();
        foreach (var a in _playback.Active)
        {
            if (a.Voice.IsFinished || !_itemsByKey.TryGetValue(a.Key, out var item)) continue;
            if (a.IsPreview) { previewing.Add(item); continue; }
            // Prefer the progress of the copy going to CS2 over the local copy.
            if (!a.IsLocal || !playing.ContainsKey(item)) playing[item] = a.Voice.Progress;
        }

        var touched = new System.Collections.Generic.HashSet<SoundItemViewModel>(_shownPlaying);
        touched.UnionWith(playing.Keys);
        touched.UnionWith(previewing);
        foreach (var item in touched)
        {
            bool isPlaying = playing.TryGetValue(item, out var progress);
            item.IsPlaying = isPlaying;
            item.IsPreviewing = previewing.Contains(item);
            item.Progress = isPlaying ? progress : 0;
        }
        _shownPlaying.Clear();
        _shownPlaying.UnionWith(playing.Keys);
        _shownPlaying.UnionWith(previewing);

        var names = _playback.NowPlayingNames().ToList();
        NowPlaying = names.Count == 0 ? "Nothing playing" : string.Join(", ", names);
    }

    private string _nowPlaying = "Nothing playing";
    public string NowPlaying
    {
        get => _nowPlaying;
        private set => SetProperty(ref _nowPlaying, value);
    }

    // ------------------------------------------------------------------
    // Tray integration
    // ------------------------------------------------------------------

    public event Action? TrayStateChanged;

    public string TrayTooltip => $"CS2 Soundboard - {RoutingShort}";

    public ICommand StopAllCommand { get; private set; } = null!;
    public ICommand StopCurrentCommand { get; private set; } = null!;

    public void StopAll()
    {
        _playback.StopAll();
        AddEvent("Stopped all sounds.");
    }
}
