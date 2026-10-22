using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using CS2Soundboard.Core.Hotkeys;

namespace CS2Soundboard.Services;

/// <summary>
/// Windows implementation of automatic push-to-talk:
///  * injects the push-to-talk key as a hardware scan code (what games read), or a mouse button;
///  * runs an observe-only low-level keyboard/mouse hook on a dedicated thread that watches only the
///    push-to-talk key, so the user's own presses and the automatic hold cooperate (see PushToTalkController).
/// The hook never blocks any other input.
/// </summary>
public sealed class PushToTalkService : IPushToTalkInjector, IDisposable
{
    /// <summary>Marks our own injected events so the hook can ignore them.</summary>
    private static readonly IntPtr InjectedMarker = new(0x43533250); // "CS2P"

    private static PushToTalkService? _instance;

    private readonly object _hookGate = new();
    private Thread? _hookThread;
    private uint _hookThreadId;
    private PushToTalkKey? _hookedKey;
    private LowLevelProc? _kbProc;
    private LowLevelProc? _mouseProc;
    private bool _disposed;

    public PushToTalkService()
    {
        Controller = new PushToTalkController(this);
        _instance = this;
    }

    public PushToTalkController Controller { get; }

    /// <summary>True when the input hook is active (protects against the user's own key presses).</summary>
    public bool HookActive { get; private set; }

    /// <summary>Sets (or clears) the push-to-talk key and (re)installs the observing hook.</summary>
    public void Configure(PushToTalkKey? key)
    {
        Controller.Key = key;
        lock (_hookGate)
        {
            if (_hookedKey == key && (key == null || _hookThread != null)) return;
            StopHook();
            _hookedKey = key;
            if (key != null) StartHook();
        }
    }

    /// <summary>Releases the key if it is held. Safe to call from any thread, including crash handlers.</summary>
    public static void EmergencyRelease()
    {
        try { _instance?.Controller.ReleaseNow(); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------
    // Foreground check
    // ------------------------------------------------------------------

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>True if the foreground window belongs to a process with the given name (e.g. "cs2").</summary>
    public static bool IsForegroundProcess(string processName)
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            return string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Injection
    // ------------------------------------------------------------------

    public void Press(PushToTalkKey key) => Send(key, down: true);
    public void Release(PushToTalkKey key) => Send(key, down: false);

    private static void Send(PushToTalkKey key, bool down)
    {
        var input = new INPUT[1];
        if (key.IsKeyboard)
        {
            uint vk = (uint)key.VirtualKey;
            uint sc = MapVirtualKey(vk, MapvkVkToVscEx);
            uint flags = KeyeventfScancode;
            if ((sc & 0xFF00) == 0xE000 || (sc & 0xFF00) == 0xE100) flags |= KeyeventfExtendedkey;
            if (!down) flags |= KeyeventfKeyup;
            input[0].type = InputKeyboard;
            input[0].u.ki = new KEYBDINPUT
            {
                wVk = sc == 0 ? (ushort)vk : (ushort)0,
                wScan = (ushort)(sc & 0xFF),
                dwFlags = sc == 0 ? (flags & ~KeyeventfScancode) : flags,
                dwExtraInfo = InjectedMarker,
            };
        }
        else
        {
            uint flags; uint data = 0;
            switch (key.Mouse)
            {
                case PttMouseButton.Middle: flags = down ? 0x0020u : 0x0040u; break;
                case PttMouseButton.XButton1: flags = down ? 0x0080u : 0x0100u; data = 1; break;
                case PttMouseButton.XButton2: flags = down ? 0x0080u : 0x0100u; data = 2; break;
                default: return;
            }
            input[0].type = InputMouse;
            input[0].u.mi = new MOUSEINPUT { dwFlags = flags, mouseData = data, dwExtraInfo = InjectedMarker };
        }

        uint sent = SendInput(1, input, Marshal.SizeOf<INPUT>());
        if (sent != 1) Log.Warn($"Push-to-talk: SendInput failed for {key} ({(down ? "down" : "up")}), error {Marshal.GetLastWin32Error()}");
    }

    // ------------------------------------------------------------------
    // Observe-only low-level hook (dedicated thread with its own message loop)
    // ------------------------------------------------------------------

    private void StartHook()
    {
        var key = _hookedKey!.Value;
        using var ready = new ManualResetEventSlim();
        _kbProc = KeyboardProc;
        _mouseProc = MouseProc;
        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            IntPtr module = GetModuleHandle(null);
            IntPtr hook = key.IsKeyboard
                ? SetWindowsHookEx(WhKeyboardLl, _kbProc, module, 0)
                : SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);
            HookActive = hook != IntPtr.Zero;
            if (!HookActive) Log.Warn($"Push-to-talk: input hook unavailable (error {Marshal.GetLastWin32Error()}); automatic push-to-talk still works.");
            ready.Set();
            if (hook == IntPtr.Zero) return;
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            UnhookWindowsHookEx(hook);
        })
        {
            IsBackground = true,
            Name = "Push-to-talk input observer",
            Priority = ThreadPriority.AboveNormal,
        };
        _hookThread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void StopHook()
    {
        if (_hookThread == null) return;
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _hookThread.Join(TimeSpan.FromSeconds(2));
        _hookThread = null;
        _hookThreadId = 0;
        HookActive = false;
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _hookedKey is { IsKeyboard: true } key)
        {
            int vk = Marshal.ReadInt32(lParam, 0);
            IntPtr extra = Marshal.ReadIntPtr(lParam, 16);
            if (vk == key.VirtualKey && extra != InjectedMarker)
            {
                int msg = wParam.ToInt32();
                bool swallow = msg is WmKeydown or WmSyskeydown
                    ? Controller.OnUserDown()
                    : msg is WmKeyup or WmSyskeyup && Controller.OnUserUp();
                if (swallow) return new IntPtr(1);
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _hookedKey is { IsKeyboard: false } key)
        {
            int msg = wParam.ToInt32();
            IntPtr extra = Marshal.ReadIntPtr(lParam, 24);
            if (extra != InjectedMarker)
            {
                bool? isDown = null;
                if (key.Mouse == PttMouseButton.Middle)
                {
                    if (msg == WmMbuttondown) isDown = true;
                    else if (msg == WmMbuttonup) isDown = false;
                }
                else if (msg == WmXbuttondown || msg == WmXbuttonup)
                {
                    int button = (Marshal.ReadInt32(lParam, 8) >> 16) & 0xFFFF;
                    if ((button == 1 && key.Mouse == PttMouseButton.XButton1) || (button == 2 && key.Mouse == PttMouseButton.XButton2))
                        isDown = msg == WmXbuttondown;
                }
                if (isDown is { } d)
                {
                    bool swallow = d ? Controller.OnUserDown() : Controller.OnUserUp();
                    if (swallow) return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Controller.ReleaseNow();
        lock (_hookGate) StopHook();
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    // ------------------------------------------------------------------
    // Win32
    // ------------------------------------------------------------------

    private const int WhKeyboardLl = 13, WhMouseLl = 14;
    private const int WmKeydown = 0x100, WmKeyup = 0x101, WmSyskeydown = 0x104, WmSyskeyup = 0x105;
    private const int WmMbuttondown = 0x207, WmMbuttonup = 0x208, WmXbuttondown = 0x20B, WmXbuttonup = 0x20C;
    private const uint WmQuit = 0x0012;
    private const uint InputMouse = 0, InputKeyboard = 1;
    private const uint KeyeventfExtendedkey = 0x1, KeyeventfKeyup = 0x2, KeyeventfScancode = 0x8;
    private const uint MapvkVkToVscEx = 4;

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
