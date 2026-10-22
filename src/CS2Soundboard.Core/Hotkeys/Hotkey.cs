using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace CS2Soundboard.Core.Hotkeys;

public sealed record HotkeyValidation(bool IsValid, string? Error, string? Warning)
{
    public static readonly HotkeyValidation Ok = new(true, null, null);
}

/// <summary>A global hotkey: optional Ctrl/Alt/Shift modifiers plus one key.</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public const ModifierKeys AllowedModifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;

    public static readonly Hotkey None = new(ModifierKeys.None, Key.None);

    public bool IsEmpty => Key == Key.None;

    private static readonly HashSet<Key> ModifierKeySet = new()
    {
        Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt, Key.RightAlt, Key.LeftShift, Key.RightShift,
        Key.LWin, Key.RWin, Key.System, Key.None, Key.ImeProcessed, Key.DeadCharProcessed,
    };

    /// <summary>Keys that may be used without a modifier (rarely used for typing or game movement).</summary>
    private static readonly HashSet<Key> BareAllowed = new(
        Enumerable.Range((int)Key.F1, 24).Select(i => (Key)i)
        .Concat(Enumerable.Range((int)Key.NumPad0, 10).Select(i => (Key)i))
        .Concat(new[]
        {
            Key.Multiply, Key.Add, Key.Subtract, Key.Divide, Key.Decimal,
            Key.Pause, Key.Scroll, Key.Insert, Key.Home, Key.End, Key.PageUp, Key.PageDown,
        }));

    private static readonly Dictionary<Key, string> DisplayNames = new()
    {
        [Key.D0] = "0", [Key.D1] = "1", [Key.D2] = "2", [Key.D3] = "3", [Key.D4] = "4",
        [Key.D5] = "5", [Key.D6] = "6", [Key.D7] = "7", [Key.D8] = "8", [Key.D9] = "9",
        [Key.NumPad0] = "Num 0", [Key.NumPad1] = "Num 1", [Key.NumPad2] = "Num 2", [Key.NumPad3] = "Num 3",
        [Key.NumPad4] = "Num 4", [Key.NumPad5] = "Num 5", [Key.NumPad6] = "Num 6", [Key.NumPad7] = "Num 7",
        [Key.NumPad8] = "Num 8", [Key.NumPad9] = "Num 9",
        [Key.Multiply] = "Num *", [Key.Add] = "Num +", [Key.Subtract] = "Num -", [Key.Divide] = "Num /", [Key.Decimal] = "Num .",
        [Key.OemPlus] = "=", [Key.OemMinus] = "-", [Key.OemComma] = ",", [Key.OemPeriod] = ".",
        [Key.OemQuestion] = "/", [Key.OemSemicolon] = ";", [Key.OemTilde] = "`", [Key.OemOpenBrackets] = "[",
        [Key.OemPipe] = "\\", [Key.OemCloseBrackets] = "]", [Key.OemQuotes] = "'", [Key.OemBackslash] = "\\",
        [Key.Return] = "Enter", [Key.PageUp] = "PageUp", [Key.PageDown] = "PageDown", [Key.Escape] = "Esc",
        [Key.Back] = "Backspace", [Key.Scroll] = "ScrollLock", [Key.Capital] = "CapsLock", [Key.Snapshot] = "PrintScreen",
    };

    public string KeyDisplayName => DisplayNames.TryGetValue(Key, out var n) ? n : Key.ToString();

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(KeyDisplayName);
        return string.Join("+", parts);
    }

    /// <summary>Stable text form for the settings file, e.g. "Ctrl+Alt+D1".</summary>
    public string Serialize()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        var mods = ModifierKeys.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                default: return false;
            }
        }
        if (!Enum.TryParse<Key>(parts[^1], true, out var key) || key == Key.None) return false;
        hotkey = new Hotkey(mods, key);
        return true;
    }

    /// <summary>Win32 RegisterHotKey modifier flags (MOD_NOREPEAT added by the registrar).</summary>
    public uint Win32Modifiers
    {
        get
        {
            uint m = 0;
            if (Modifiers.HasFlag(ModifierKeys.Alt)) m |= 0x0001;
            if (Modifiers.HasFlag(ModifierKeys.Control)) m |= 0x0002;
            if (Modifiers.HasFlag(ModifierKeys.Shift)) m |= 0x0004;
            return m;
        }
    }

    public int VirtualKey => KeyInterop.VirtualKeyFromKey(Key);

    public static bool IsModifierKey(Key key) => ModifierKeySet.Contains(key);

    /// <summary>Checks whether a combination is usable and safe for gaming.</summary>
    public static HotkeyValidation Validate(Hotkey hk)
    {
        if (hk.IsEmpty || IsModifierKey(hk.Key))
            return new HotkeyValidation(false, "Press a non-modifier key (optionally with Ctrl, Alt and/or Shift).", null);
        if ((hk.Modifiers & ~AllowedModifiers) != 0)
            return new HotkeyValidation(false, "Only Ctrl, Alt and Shift modifiers are supported.", null);

        var m = hk.Modifiers;
        if (m == ModifierKeys.Alt && (hk.Key == Key.F4 || hk.Key == Key.Tab || hk.Key == Key.Escape || hk.Key == Key.Space))
            return new HotkeyValidation(false, $"{hk} is reserved by Windows.", null);
        if (m == ModifierKeys.Control && hk.Key == Key.Escape)
            return new HotkeyValidation(false, "Ctrl+Esc is reserved by Windows.", null);
        if (m.HasFlag(ModifierKeys.Control) && m.HasFlag(ModifierKeys.Alt) && hk.Key == Key.Delete)
            return new HotkeyValidation(false, "Ctrl+Alt+Del is reserved by Windows.", null);

        if (m == ModifierKeys.None)
        {
            if (!BareAllowed.Contains(hk.Key))
                return new HotkeyValidation(false,
                    $"\"{hk.KeyDisplayName}\" without a modifier would block that key in every application, including CS2 and chat. " +
                    "Add Ctrl, Alt or Shift, or use a function key (F1-F24) or a numpad key.", null);
            string warn = hk.Key == Key.F12
                ? "F12 alone is reserved by Windows for debuggers and may fail to register."
                : $"\"{hk.KeyDisplayName}\" alone will be captured system-wide: CS2 and other apps will no longer receive it while the soundboard runs.";
            return new HotkeyValidation(true, null, warn);
        }

        if (m == ModifierKeys.Shift && hk.Key >= Key.A && hk.Key <= Key.Z)
            return new HotkeyValidation(true, null, "Shift+letter blocks typing that capital letter in chat while the soundboard runs.");
        if (m == ModifierKeys.Shift && hk.Key >= Key.D0 && hk.Key <= Key.D9)
            return new HotkeyValidation(true, null, "Shift+number blocks typing that symbol in chat while the soundboard runs.");

        return HotkeyValidation.Ok;
    }
}
