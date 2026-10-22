using System;
using System.Windows.Input;

namespace CS2Soundboard.Core.Hotkeys;

public enum PttDevice { Keyboard, Mouse }

public enum PttMouseButton { None, Middle, XButton1, XButton2 }

/// <summary>The key or mouse button bound to push-to-talk in the game (e.g. "V" or "Mouse 4").</summary>
public readonly record struct PushToTalkKey(PttDevice Device, Key Key, PttMouseButton Mouse)
{
    public static PushToTalkKey FromKey(Key key) => new(PttDevice.Keyboard, key, PttMouseButton.None);
    public static PushToTalkKey FromMouse(PttMouseButton button) => new(PttDevice.Mouse, Key.None, button);

    public bool IsKeyboard => Device == PttDevice.Keyboard;

    public int VirtualKey => IsKeyboard ? KeyInterop.VirtualKeyFromKey(Key) : 0;

    public override string ToString() => Device switch
    {
        PttDevice.Keyboard => new Hotkey(ModifierKeys.None, Key).KeyDisplayName switch
        {
            "LeftCtrl" => "Left Ctrl",
            "RightCtrl" => "Right Ctrl",
            "LeftAlt" => "Left Alt",
            "RightAlt" => "Right Alt",
            "LeftShift" => "Left Shift",
            "RightShift" => "Right Shift",
            var n => n,
        },
        _ => Mouse switch
        {
            PttMouseButton.Middle => "Middle mouse button",
            PttMouseButton.XButton1 => "Mouse 4 (back)",
            PttMouseButton.XButton2 => "Mouse 5 (forward)",
            _ => "?",
        },
    };

    public string Serialize() => IsKeyboard ? $"Key:{Key}" : $"Mouse:{Mouse}";

    public static bool TryParse(string? text, out PushToTalkKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        if (parts[0].Equals("Key", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<Key>(parts[1], true, out var k) && Validate(FromKey(k)).IsValid)
        {
            key = FromKey(k);
            return true;
        }
        if (parts[0].Equals("Mouse", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<PttMouseButton>(parts[1], true, out var b) && b != PttMouseButton.None)
        {
            key = FromMouse(b);
            return true;
        }
        return false;
    }

    public static HotkeyValidation Validate(PushToTalkKey key)
    {
        if (key.Device == PttDevice.Mouse)
            return key.Mouse == PttMouseButton.None
                ? new HotkeyValidation(false, "Only the middle button and the side buttons (Mouse 4/5) can be used.", null)
                : HotkeyValidation.Ok;

        switch (key.Key)
        {
            case Key.None:
            case Key.System:
            case Key.ImeProcessed:
            case Key.DeadCharProcessed:
                return new HotkeyValidation(false, "Press the key you use for push-to-talk in CS2.", null);
            case Key.Escape:
            case Key.LWin:
            case Key.RWin:
            case Key.Apps:
            case Key.Snapshot:
                return new HotkeyValidation(false, $"{FromKey(key.Key)} cannot be used as a push-to-talk key.", null);
            case Key.Enter:
            case Key.Tab:
            case Key.Back:
                return new HotkeyValidation(true, null, $"{FromKey(key.Key)} is unusual for push-to-talk. Make sure it matches your CS2 voice key.");
        }
        return HotkeyValidation.Ok;
    }

    /// <summary>True when a soundboard hotkey would swallow this push-to-talk key.</summary>
    public bool ConflictsWith(Hotkey hotkey) => IsKeyboard && !hotkey.IsEmpty && hotkey.Key == Key;
}
