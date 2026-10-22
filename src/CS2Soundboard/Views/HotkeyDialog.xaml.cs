using System.Windows;
using System.Windows.Input;
using CS2Soundboard.Core.Hotkeys;
using CS2Soundboard.ViewModels;

namespace CS2Soundboard.Views;

/// <summary>Captures a key combination. Global hotkeys are suspended by the caller while this is open.</summary>
public partial class HotkeyDialog : Window
{
    private Hotkey _candidate = Hotkey.None;

    public HotkeyDialog(string title, Hotkey current)
    {
        InitializeComponent();
        TitleText.Text = title;
        if (!current.IsEmpty) ComboText.Text = current.ToString();
        ClearButton.IsEnabled = !current.IsEmpty;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => { Activate(); Keyboard.Focus(this); };
    }

    public HotkeyDialogResult Result { get; private set; } = new(true, false, Hotkey.None);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers & Hotkey.AllowedModifiers;

        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            DialogResult = false;
            return;
        }

        if (Hotkey.IsModifierKey(key))
        {
            // Show the modifiers held so far.
            ComboText.Text = mods == ModifierKeys.None ? "Waiting for keys..." : DescribeMods(mods) + "+ ...";
            return;
        }

        _candidate = new Hotkey(mods, key);
        ComboText.Text = _candidate.ToString();
        var v = Hotkey.Validate(_candidate);
        if (!v.IsValid)
        {
            MessageText.Text = v.Error;
            MessageText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            SaveButton.IsEnabled = false;
        }
        else
        {
            MessageText.Text = v.Warning ?? "Press Save to use this combination.";
            MessageText.Foreground = (System.Windows.Media.Brush)FindResource(v.Warning != null ? "WarnBrush" : "OkBrush");
            SaveButton.IsEnabled = true;
        }
    }

    private static string DescribeMods(ModifierKeys mods)
    {
        var s = "";
        if (mods.HasFlag(ModifierKeys.Control)) s += "Ctrl ";
        if (mods.HasFlag(ModifierKeys.Alt)) s += "Alt ";
        if (mods.HasFlag(ModifierKeys.Shift)) s += "Shift ";
        return s;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Result = new HotkeyDialogResult(false, false, _candidate);
        DialogResult = true;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Result = new HotkeyDialogResult(false, true, Hotkey.None);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
