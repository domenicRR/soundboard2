using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using CS2Soundboard.Core.Hotkeys;

namespace CS2Soundboard.Services;

/// <summary>
/// System-wide hotkeys via Win32 RegisterHotKey on a hidden message-only window.
///
/// * Works while another application (e.g. CS2, including when it runs elevated or full screen)
///   has focus and while this app is minimized or hidden in the tray.
/// * MOD_NOREPEAT suppresses keyboard auto-repeat, so holding a key triggers once.
/// * Only the registered combinations are intercepted; all other input is untouched
///   (no low-level keyboard hook is installed).
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private sealed class Entry
    {
        public int Id;
        public Hotkey Hotkey;
        public bool Registered;
        public string? Error;
    }

    private readonly HwndSource _source;
    private readonly Dictionary<string, Entry> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _ids = new();
    private int _nextId = 1;
    private bool _suspended;
    private bool _disposed;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("CS2SoundboardHotkeys")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: message-only window
            WindowStyle = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Raised on the UI thread with the binding name.</summary>
    public event Action<string>? Pressed;

    public bool IsSuspended => _suspended;

    /// <summary>Registers (or replaces) a binding. Returns null on success, otherwise a user-facing error.</summary>
    public string? Register(string binding, Hotkey hotkey)
    {
        Unregister(binding);
        if (hotkey.IsEmpty) return null;
        var entry = new Entry { Id = _nextId++, Hotkey = hotkey };
        _bindings[binding] = entry;
        _ids[entry.Id] = binding;
        if (_suspended) return null;
        return TryRegister(entry);
    }

    public void Unregister(string binding)
    {
        if (!_bindings.Remove(binding, out var entry)) return;
        _ids.Remove(entry.Id);
        if (entry.Registered) UnregisterHotKey(_source.Handle, entry.Id);
    }

    public string? GetError(string binding) => _bindings.TryGetValue(binding, out var e) ? e.Error : null;

    public IEnumerable<string> Bindings => _bindings.Keys.ToList();

    /// <summary>Temporarily releases all hotkeys (e.g. while the user records a new combination).</summary>
    public void Suspend()
    {
        if (_suspended) return;
        _suspended = true;
        foreach (var e in _bindings.Values)
        {
            if (e.Registered) UnregisterHotKey(_source.Handle, e.Id);
            e.Registered = false;
        }
    }

    /// <summary>Re-registers all hotkeys. Returns bindings that failed with their errors.</summary>
    public Dictionary<string, string> Resume()
    {
        _suspended = false;
        var failures = new Dictionary<string, string>();
        foreach (var kv in _bindings)
        {
            var err = TryRegister(kv.Value);
            if (err != null) failures[kv.Key] = err;
        }
        return failures;
    }

    private string? TryRegister(Entry e)
    {
        if (RegisterHotKey(_source.Handle, e.Id, e.Hotkey.Win32Modifiers | ModNoRepeat, (uint)e.Hotkey.VirtualKey))
        {
            e.Registered = true;
            e.Error = null;
            return null;
        }
        int code = Marshal.GetLastWin32Error();
        e.Registered = false;
        e.Error = code == ErrorHotkeyAlreadyRegistered
            ? $"{e.Hotkey} is already in use by another application or reserved by Windows. Choose a different combination or close the other application."
            : $"{e.Hotkey} could not be registered: {new Win32Exception(code).Message}";
        Log.Warn($"Hotkey registration failed: {e.Hotkey} ({code})");
        return e.Error;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            int id = wParam.ToInt32();
            if (_ids.TryGetValue(id, out var binding))
            {
                handled = true;
                try { Pressed?.Invoke(binding); }
                catch (Exception ex) { Log.Error($"Hotkey handler failed for {binding}", ex); }
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var e in _bindings.Values)
        {
            if (e.Registered) UnregisterHotKey(_source.Handle, e.Id);
        }
        _bindings.Clear();
        _ids.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
