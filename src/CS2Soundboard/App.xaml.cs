using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using CS2Soundboard.Core.Settings;
using CS2Soundboard.Services;
using CS2Soundboard.ViewModels;
using CS2Soundboard.Views;

namespace CS2Soundboard;

public partial class App : Application
{
    private const string MutexName = @"Local\CS2Soundboard.SingleInstance";
    private const string ShowEventName = @"Local\CS2Soundboard.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private HotkeyManager? _hotkeys;
    private TrayService? _tray;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance: two copies would fight over hotkeys and audio devices.
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* first instance not ready */ }
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                if (_exiting) return;
                Dispatcher.BeginInvoke(new Action(() => _window?.ShowFromTray()));
            }
        }) { IsBackground = true, Name = "Single-instance listener" };
        listener.Start();

        var store = new SettingsStore();
        Log.Init(store.Directory);
        Log.Info($"Starting CS2 Soundboard {typeof(App).Assembly.GetName().Version} (settings: {store.FilePath})");

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show($"An unexpected error occurred:\n\n{args.Exception.Message}\n\nThe application will keep running. Details were written to the log file.",
                "CS2 Soundboard", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        SessionEnding += (_, _) => ExitApplication();

        var (settings, loadWarning) = store.Load();

        _hotkeys = new HotkeyManager();
        _tray = new TrayService();
        _window = new MainWindow();
        _window.Icon = Imaging.CreateBitmapSourceFromHIcon(_tray.AppIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

        var ui = new UiServices(_window, _tray);
        _vm = new MainViewModel(store, settings, _hotkeys, ui);
        _window.DataContext = _vm;
        _window.ExitRequested += ExitApplication;
        _window.HiddenToTray += () => _tray.ShowBalloon("CS2 Soundboard is still running",
            "Microphone passthrough and hotkeys keep working. Use the tray icon to open the window or exit.");

        _tray.OpenRequested += () => _window.ShowFromTray();
        _tray.StopAllRequested += () => _vm.StopAll();
        _tray.ToggleMicRequested += () => _vm.MicMuted = !_vm.MicMuted;
        _tray.ToggleSoundboardRequested += () => _vm.SoundboardMuted = !_vm.SoundboardMuted;
        _tray.ExitRequested += ExitApplication;
        _vm.TrayStateChanged += () => _tray.Update(_vm.MicMuted, _vm.SoundboardMuted, _vm.TrayTooltip);

        bool startHidden = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)) || settings.StartMinimized;
        if (!startHidden)
        {
            _window.Show();
        }
        else if (!settings.MinimizeToTray)
        {
            _window.WindowState = WindowState.Minimized;
            _window.Show();
        }

        if (loadWarning != null)
            MessageBox.Show(loadWarning, "CS2 Soundboard settings", MessageBoxButton.OK, MessageBoxImage.Warning);

        try
        {
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Initialization failed", ex);
            MessageBox.Show($"Initialization failed: {ex.Message}", "CS2 Soundboard", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Stops audio, releases devices and hotkeys, saves settings and exits.</summary>
    public void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("Exiting...");
        try { _vm?.Shutdown(); } catch (Exception ex) { Log.Error("VM shutdown failed", ex); }
        try { _hotkeys?.Dispose(); } catch (Exception ex) { Log.Error("Hotkey cleanup failed", ex); }
        try { _tray?.Dispose(); } catch { /* ignore */ }
        if (_window != null && !_window.AllowClose)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        try { _showEvent?.Set(); _showEvent?.Dispose(); } catch { /* ignore */ }
        try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); } catch { /* ignore */ }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting && _vm != null) ExitApplication();
        base.OnExit(e);
    }
}
