using System;
using System.ComponentModel;
using System.Windows;
using CS2Soundboard.ViewModels;

namespace CS2Soundboard.Views;

public partial class MainWindow : Window
{
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += OnStateChanged;
        Closing += OnClosing;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>Raised when the user asked to quit (closing without close-to-tray).</summary>
    public event Action? ExitRequested;

    /// <summary>Raised the first time the window is hidden to the tray.</summary>
    public event Action? HiddenToTray;

    public bool AllowClose { get; set; }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && Vm?.MinimizeToTray == true)
            HideToTray();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose) return;
        if (Vm?.CloseToTray == true)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        // Let the window close, then run the full application shutdown.
        AllowClose = true;
        Dispatcher.BeginInvoke(new Action(() => ExitRequested?.Invoke()));
    }

    public void HideToTray()
    {
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            HiddenToTray?.Invoke();
        }
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // bring to front reliably
        Topmost = false;
        Focus();
    }
}
