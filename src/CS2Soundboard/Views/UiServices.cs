using System.Windows;
using CS2Soundboard.Core.Hotkeys;
using CS2Soundboard.Services;
using CS2Soundboard.ViewModels;

namespace CS2Soundboard.Views;

public sealed class UiServices : IUiServices
{
    private readonly MainWindow _window;
    private readonly TrayService _tray;

    public UiServices(MainWindow window, TrayService tray)
    {
        _window = window;
        _tray = tray;
    }

    public bool IsWindowVisible => _window.IsVisible && _window.WindowState != WindowState.Minimized;

    private Window? Owner => _window.IsVisible ? _window : null;

    public string? PickFolder(string? initialDirectory)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the folder that contains your sound files",
            InitialDirectory = System.IO.Directory.Exists(initialDirectory) ? initialDirectory : "",
        };
        bool? ok = Owner != null ? dlg.ShowDialog(Owner) : dlg.ShowDialog();
        return ok == true ? dlg.FolderName : null;
    }

    public HotkeyDialogResult CaptureHotkey(string title, Hotkey current)
    {
        var dlg = new HotkeyDialog(title, current);
        if (Owner != null) dlg.Owner = Owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? dlg.Result : new HotkeyDialogResult(true, false, Hotkey.None);
    }

    public bool Confirm(string title, string message)
    {
        var r = Owner != null
            ? MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return r == MessageBoxResult.Yes;
    }

    public void ShowError(string title, string message)
    {
        if (Owner != null) MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void Notify(string title, string message, bool isError)
    {
        if (!IsWindowVisible) _tray.ShowBalloon(title, message, isError);
    }
}
