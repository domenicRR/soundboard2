using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CS2Soundboard.Core.Hotkeys;

namespace CS2Soundboard.Views;

/// <summary>Captures a single key (no modifiers) or a middle/side mouse button.</summary>
public partial class PushToTalkKeyDialog : Window
{
    private PushToTalkKey? _candidate;

    public PushToTalkKeyDialog(PushToTalkKey? current)
    {
        InitializeComponent();
        if (current is { } c) KeyText.Text = c.ToString();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += (_, e) => e.Handled = true; // keep Alt/F10 from opening the system menu
        PreviewMouseDown += OnPreviewMouseDown;
        Loaded += (_, _) => { Activate(); Keyboard.Focus(this); };
    }

    public PushToTalkKey? Result { get; private set; }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            DialogResult = false;
            return;
        }
        Offer(PushToTalkKey.FromKey(key));
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Left/right clicks are needed to use the dialog (and are shoot/aim in CS2), so only these count.
        PttMouseButton button = e.ChangedButton switch
        {
            MouseButton.Middle => PttMouseButton.Middle,
            MouseButton.XButton1 => PttMouseButton.XButton1,
            MouseButton.XButton2 => PttMouseButton.XButton2,
            _ => PttMouseButton.None,
        };
        if (button == PttMouseButton.None) return;
        e.Handled = true;
        Offer(PushToTalkKey.FromMouse(button));
    }

    private void Offer(PushToTalkKey key)
    {
        KeyText.Text = key.ToString();
        var v = PushToTalkKey.Validate(key);
        if (!v.IsValid)
        {
            _candidate = null;
            MessageText.Text = v.Error;
            MessageText.Foreground = (Brush)FindResource("DangerBrush");
            SaveButton.IsEnabled = false;
            return;
        }
        _candidate = key;
        MessageText.Text = v.Warning ?? "Make sure this matches the push-to-talk key bound in CS2, then press Save.";
        MessageText.Foreground = (Brush)FindResource(v.Warning != null ? "WarnBrush" : "OkBrush");
        SaveButton.IsEnabled = true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Result = _candidate;
        DialogResult = _candidate != null;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
