using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace CS2Soundboard.Services;

/// <summary>Windows notification-area icon with quick actions.</summary>
public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _muteMic;
    private readonly WinForms.ToolStripMenuItem _muteSoundboard;
    private readonly Icon _appIcon;

    public TrayService()
    {
        _appIcon = CreateAppIcon();
        var menu = new WinForms.ContextMenuStrip();
        var open = new WinForms.ToolStripMenuItem("Open CS2 Soundboard", null, (_, _) => OpenRequested?.Invoke());
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Stop all sounds", null, (_, _) => StopAllRequested?.Invoke()));
        _muteMic = new WinForms.ToolStripMenuItem("Mute microphone", null, (_, _) => ToggleMicRequested?.Invoke());
        _muteSoundboard = new WinForms.ToolStripMenuItem("Mute soundboard", null, (_, _) => ToggleSoundboardRequested?.Invoke());
        menu.Items.Add(_muteMic);
        menu.Items.Add(_muteSoundboard);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke()));

        _icon = new WinForms.NotifyIcon
        {
            Icon = _appIcon,
            Text = "CS2 Soundboard",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke(); };
    }

    public event Action? OpenRequested;
    public event Action? StopAllRequested;
    public event Action? ToggleMicRequested;
    public event Action? ToggleSoundboardRequested;
    public event Action? ExitRequested;

    public Icon AppIcon => _appIcon;

    public void Update(bool micMuted, bool soundboardMuted, string tooltip)
    {
        _muteMic.Checked = micMuted;
        _muteSoundboard.Checked = soundboardMuted;
        // NotifyIcon.Text is limited to 127 characters.
        _icon.Text = tooltip.Length > 127 ? tooltip.Substring(0, 124) + "..." : tooltip;
    }

    public void ShowBalloon(string title, string text, bool isError = false)
    {
        try
        {
            _icon.ShowBalloonTip(4000, title, text, isError ? WinForms.ToolTipIcon.Error : WinForms.ToolTipIcon.Info);
        }
        catch { /* balloon notifications may be disabled */ }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Draws a simple app icon (no external resource files needed).</summary>
    private static Icon CreateAppIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(232, 135, 30));
            using var path = new GraphicsPath();
            path.AddArc(1, 1, 10, 10, 180, 90);
            path.AddArc(21, 1, 10, 10, 270, 90);
            path.AddArc(21, 21, 10, 10, 0, 90);
            path.AddArc(1, 21, 10, 10, 90, 90);
            path.CloseFigure();
            g.FillPath(bg, path);
            using var white = new SolidBrush(Color.White);
            // speaker body
            g.FillRectangle(white, 7, 12, 5, 8);
            g.FillPolygon(white, new[] { new Point(11, 12), new Point(17, 7), new Point(17, 25), new Point(11, 20) });
            using var pen = new Pen(Color.White, 2.2f);
            g.DrawArc(pen, 15, 10, 8, 12, -50, 100);
            g.DrawArc(pen, 15, 6, 13, 20, -50, 100);
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
