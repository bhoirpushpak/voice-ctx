using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace VoiceCtx.App.Services;

/// <summary>Notification-area icon with Show / On-Off / Exit menu. Icon turns gray when the effect is off.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _toggleItem;
    private readonly Icon _onIcon = LoadIcon("Assets/VoiceCtx.ico");
    private readonly Icon _offIcon = LoadIcon("Assets/VoiceCtxOff.ico");
    private bool _balloonShown;

    public TrayIcon()
    {
        _toggleItem = new Forms.ToolStripMenuItem("Voice effect on", null, (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty));
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Show Voice CTX", null, (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty))
        {
            Font = new Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold),
        });
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _onIcon,
            Text = "Voice CTX",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ShowRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? ShowRequested;
    public event EventHandler? ToggleRequested;
    public event EventHandler? ExitRequested;

    public void Update(bool isOn, string effectName)
    {
        _notifyIcon.Icon = isOn ? _onIcon : _offIcon;
        _toggleItem.Checked = isOn;
        string text = $"Voice CTX — {(isOn ? effectName : "Off")}";
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    /// <summary>Tells the user once per session that closing the window keeps the app running.</summary>
    public void NotifyStillRunning()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        _notifyIcon.ShowBalloonTip(3000, "Voice CTX is still running",
            "Your voice keeps flowing to the virtual cable. Use the tray icon to reopen or exit.", Forms.ToolTipIcon.Info);
    }

    private static Icon LoadIcon(string resourcePath)
    {
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/{resourcePath}"))
            ?? throw new InvalidOperationException($"Missing resource {resourcePath}");
        using var stream = info.Stream;
        return new Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _onIcon.Dispose();
        _offIcon.Dispose();
    }
}
