using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CS2Soundboard.Core.Hotkeys;

namespace CS2Soundboard.ViewModels;

public sealed partial class MainViewModel
{
    public const string StopAllBinding = "cmd:stopall";
    public const string MicMuteBinding = "cmd:mutemic";
    public const string SoundboardMuteBinding = "cmd:mutesoundboard";

    public ObservableCollection<CommandHotkeyViewModel> CommandHotkeys { get; }

    private IEnumerable<IHotkeyTarget> AllHotkeyTargets => Sounds.Cast<IHotkeyTarget>().Concat(CommandHotkeys);

    private void LoadCommandHotkeys()
    {
        foreach (var cmd in CommandHotkeys)
        {
            var text = cmd.Binding switch
            {
                StopAllBinding => _s.StopAllHotkey,
                MicMuteBinding => _s.ToggleMicMuteHotkey,
                SoundboardMuteBinding => _s.ToggleSoundboardMuteHotkey,
                _ => null,
            };
            cmd.Hotkey = Hotkey.TryParse(text, out var hk) ? hk : Hotkey.None;
            cmd.HotkeyError = cmd.Hotkey.IsEmpty ? null : _hotkeys.Register(cmd.Binding, cmd.Hotkey);
            if (cmd.HotkeyError != null) AddEvent($"{cmd.DisplayName}: {cmd.HotkeyError}", StatusLevel.Warning);
        }
    }

    private void PersistHotkey(IHotkeyTarget target)
    {
        string? text = target.Hotkey.IsEmpty ? null : target.Hotkey.Serialize();
        switch (target)
        {
            case SoundItemViewModel s:
                if (text == null) _s.Hotkeys.Remove(s.Key); else _s.Hotkeys[s.Key] = text;
                break;
            case CommandHotkeyViewModel c when c.Binding == StopAllBinding:
                _s.StopAllHotkey = text;
                break;
            case CommandHotkeyViewModel c when c.Binding == MicMuteBinding:
                _s.ToggleMicMuteHotkey = text;
                break;
            case CommandHotkeyViewModel c when c.Binding == SoundboardMuteBinding:
                _s.ToggleSoundboardMuteHotkey = text;
                break;
        }
        SaveSoon();
    }

    /// <summary>Records a new combination, resolves conflicts and registers it system-wide.</summary>
    private void AssignHotkey(IHotkeyTarget target)
    {
        HotkeyDialogResult result;
        _hotkeys.Suspend(); // so pressing an existing combination is captured instead of triggering it
        try
        {
            result = _ui.CaptureHotkey($"Hotkey for \"{target.DisplayName}\"", target.Hotkey);
        }
        finally
        {
            foreach (var kv in _hotkeys.Resume())
            {
                var t = AllHotkeyTargets.FirstOrDefault(x => x.Binding == kv.Key);
                if (t != null) t.HotkeyError = kv.Value;
            }
        }

        if (result.Cancelled) return;
        if (result.Cleared)
        {
            ClearHotkey(target);
            return;
        }

        var hk = result.Hotkey;
        if (hk == target.Hotkey && !target.HasHotkeyError()) return;

        if (PushToTalkConflictFor(hk) is { } pttError)
        {
            _ui.ShowError("Hotkey uses your push-to-talk key", pttError);
            return;
        }

        var conflict = AllHotkeyTargets.FirstOrDefault(t => !ReferenceEquals(t, target) && t.Hotkey == hk);
        if (conflict != null)
        {
            if (!_ui.Confirm("Hotkey already assigned",
                    $"{hk} is already assigned to \"{conflict.DisplayName}\".\n\nMove it to \"{target.DisplayName}\"? \"{conflict.DisplayName}\" will no longer have a hotkey."))
                return;
            _hotkeys.Unregister(conflict.Binding);
            conflict.Hotkey = Hotkey.None;
            conflict.HotkeyError = null;
            PersistHotkey(conflict);
        }

        var previous = target.Hotkey;
        var error = _hotkeys.Register(target.Binding, hk);
        if (error != null)
        {
            // Keep the previous working assignment instead of saving a hotkey that does not work.
            if (!previous.IsEmpty) target.HotkeyError = _hotkeys.Register(target.Binding, previous);
            else _hotkeys.Unregister(target.Binding);
            _ui.ShowError("Hotkey not available", error);
            AddEvent(error, StatusLevel.Error);
            return;
        }

        target.Hotkey = hk;
        target.HotkeyError = null;
        PersistHotkey(target);
        AddEvent($"{hk} now triggers \"{target.DisplayName}\".");
    }

    private void ClearHotkey(IHotkeyTarget target)
    {
        _hotkeys.Unregister(target.Binding);
        target.Hotkey = Hotkey.None;
        target.HotkeyError = null;
        PersistHotkey(target);

        // A missing sound with no hotkey left has no reason to stay in the list.
        if (target is SoundItemViewModel { State: SoundState.Missing } s)
        {
            Sounds.Remove(s);
            _itemsByKey.Remove(s.Key);
        }
    }

    private void OnHotkeyPressed(string binding)
    {
        switch (binding)
        {
            case StopAllBinding:
                StopAll();
                return;
            case MicMuteBinding:
                MicMuted = !MicMuted;
                return;
            case SoundboardMuteBinding:
                SoundboardMuted = !SoundboardMuted;
                return;
        }
        if (binding.StartsWith("sound:", StringComparison.Ordinal) && _itemsByKey.TryGetValue(binding.Substring(6), out var item))
        {
            _ = PlayFromHotkeyAsync(item);
        }
    }

    private async System.Threading.Tasks.Task PlayFromHotkeyAsync(SoundItemViewModel item)
    {
        try { await PlayItemAsync(item, fromHotkey: true); }
        catch (Exception ex) { ReportException(ex); }
    }
}

internal static class HotkeyTargetExtensions
{
    public static bool HasHotkeyError(this IHotkeyTarget t) => !string.IsNullOrEmpty(t.HotkeyError);
}
