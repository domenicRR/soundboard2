using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CS2Soundboard.Core.Hotkeys;
using CS2Soundboard.Services;

namespace CS2Soundboard.ViewModels;

/// <summary>Automatic push-to-talk: holds the game's voice key while soundboard sounds play.</summary>
public sealed partial class MainViewModel
{
    private PushToTalkService _ptt = null!;
    private bool _pttHolding;

    public ICommand AssignPushToTalkKeyCommand { get; private set; } = null!;

    private void InitPushToTalk()
    {
        _ptt = new PushToTalkService();
        _ptt.Controller.HoldingChanged += holding => _dispatcher.BeginInvoke(new Action(() => PushToTalkHolding = holding));
        AssignPushToTalkKeyCommand = new RelayCommand(AssignPushToTalkKey);
        ApplyPushToTalkConfig();
    }

    private PushToTalkKey? ConfiguredPushToTalkKey
        => PushToTalkKey.TryParse(_s.PushToTalkKey, out var k) ? k : null;

    private void ApplyPushToTalkConfig()
    {
        _ptt.Configure(_s.AutoPushToTalk ? ConfiguredPushToTalkKey : null);
        OnPropertyChanged(nameof(PushToTalkHookWarning));
    }

    /// <summary>The sound/command whose hotkey uses the push-to-talk key itself (that hotkey would swallow it).</summary>
    private IHotkeyTarget? FindPushToTalkConflict(PushToTalkKey key)
        => AllHotkeyTargets.FirstOrDefault(t => key.ConflictsWith(t.Hotkey));

    public bool AutoPushToTalk
    {
        get => _s.AutoPushToTalk;
        set
        {
            if (_s.AutoPushToTalk == value) return;
            if (value)
            {
                if (ConfiguredPushToTalkKey is not { } key)
                {
                    _ui.ShowError("Automatic push-to-talk", "Choose your CS2 push-to-talk key first.");
                    OnPropertyChanged();
                    return;
                }
                if (FindPushToTalkConflict(key) is { } conflict)
                {
                    _ui.ShowError("Automatic push-to-talk",
                        $"The hotkey of \"{conflict.DisplayName}\" ({conflict.Hotkey}) uses {key}, your push-to-talk key. " +
                        "That hotkey would block the key from reaching CS2. Change that hotkey first.");
                    OnPropertyChanged();
                    return;
                }
            }
            _s.AutoPushToTalk = value;
            OnPropertyChanged();
            SaveSoon();
            ApplyPushToTalkConfig();
            AddEvent(value
                ? $"Automatic push-to-talk on: {ConfiguredPushToTalkKey} is held while sounds play{(_s.PushToTalkOnlyWhenCs2Focused ? " (only when CS2 is the active window)" : "")}."
                : "Automatic push-to-talk off.");
        }
    }

    public string PushToTalkKeyText => ConfiguredPushToTalkKey?.ToString() ?? "Not set";

    public IReadOnlyList<int> PushToTalkPressDelayOptions { get; } = new[] { 0, 50, 100, 150, 200, 300, 500 };
    public IReadOnlyList<int> PushToTalkReleaseDelayOptions { get; } = new[] { 0, 150, 300, 500, 750, 1000, 1500 };

    public int PushToTalkPressDelayMs
    {
        get => _s.PushToTalkPressDelayMs;
        set { if (_s.PushToTalkPressDelayMs == value) return; _s.PushToTalkPressDelayMs = value; OnPropertyChanged(); SaveSoon(); }
    }

    public int PushToTalkReleaseDelayMs
    {
        get => _s.PushToTalkReleaseDelayMs;
        set { if (_s.PushToTalkReleaseDelayMs == value) return; _s.PushToTalkReleaseDelayMs = value; OnPropertyChanged(); SaveSoon(); }
    }

    public bool PushToTalkOnlyWhenCs2Focused
    {
        get => _s.PushToTalkOnlyWhenCs2Focused;
        set { if (_s.PushToTalkOnlyWhenCs2Focused == value) return; _s.PushToTalkOnlyWhenCs2Focused = value; OnPropertyChanged(); SaveSoon(); }
    }

    public bool PushToTalkHolding
    {
        get => _pttHolding;
        private set { if (SetProperty(ref _pttHolding, value)) OnPropertyChanged(nameof(PushToTalkStatus)); }
    }

    public string PushToTalkStatus => _pttHolding ? $"Holding push-to-talk ({ConfiguredPushToTalkKey})" : "";

    public string PushToTalkHookWarning => _s.AutoPushToTalk && !_ptt.HookActive
        ? "The input observer could not be installed. Automatic push-to-talk works, but pressing your own push-to-talk key during a sound may cut it off."
        : "";

    private void AssignPushToTalkKey()
    {
        PushToTalkKey? result;
        _hotkeys.Suspend();
        try
        {
            result = _ui.CapturePushToTalkKey(ConfiguredPushToTalkKey);
        }
        finally
        {
            foreach (var kv in _hotkeys.Resume())
            {
                var t = AllHotkeyTargets.FirstOrDefault(x => x.Binding == kv.Key);
                if (t != null) t.HotkeyError = kv.Value;
            }
        }
        if (result is not { } key) return;

        if (FindPushToTalkConflict(key) is { } conflict)
        {
            _ui.ShowError("Push-to-talk key conflict",
                $"The hotkey of \"{conflict.DisplayName}\" ({conflict.Hotkey}) uses {key}. It would block your push-to-talk key from reaching CS2. " +
                "Change or clear that hotkey first, or choose a different push-to-talk key.");
            return;
        }

        _s.PushToTalkKey = key.Serialize();
        OnPropertyChanged(nameof(PushToTalkKeyText));
        SaveSoon();
        ApplyPushToTalkConfig();
        AddEvent($"Push-to-talk key set to {key}.");
    }

    /// <summary>Returns an error message if this hotkey would swallow the push-to-talk key.</summary>
    private string? PushToTalkConflictFor(Hotkey hotkey)
    {
        if (!_s.AutoPushToTalk || ConfiguredPushToTalkKey is not { } key || !key.ConflictsWith(hotkey)) return null;
        return $"{hotkey} uses {key}, which is your push-to-talk key. Registering it would stop {key} from reaching CS2. Choose a combination with a different key.";
    }

    private bool ShouldUsePushToTalk(SoundItemViewModel item)
    {
        if (!_s.AutoPushToTalk || ConfiguredPushToTalkKey == null) return false;
        if (_audio.Graph == null || !_s.RoutingEnabled || _s.SoundboardMuted) return false; // nothing would be heard
        if (_s.RetriggerStops && _playback.IsPlaying(item.Key)) return false;                 // this press stops the sound
        if (_s.PushToTalkOnlyWhenCs2Focused && !PushToTalkService.IsForegroundProcess("cs2")) return false;
        return true;
    }

    /// <summary>Runs <paramref name="start"/> with the push-to-talk key held (if enabled and applicable).</summary>
    private async Task<T> WithPushToTalkAsync<T>(SoundItemViewModel item, Func<Task<T>> start)
    {
        if (!ShouldUsePushToTalk(item)) return await start();
        bool freshlyPressed = _ptt.Controller.Acquire();
        try
        {
            if (freshlyPressed && _s.PushToTalkPressDelayMs > 0)
                await Task.Delay(_s.PushToTalkPressDelayMs); // let CS2 open the microphone first
            return await start();
        }
        finally
        {
            _ptt.Controller.EndPending();
        }
    }

    private void UpdatePushToTalk()
        => _ptt.Controller.Update(_playback.HasActiveMixVoices(), TimeSpan.FromMilliseconds(_s.PushToTalkReleaseDelayMs));
}
