namespace Quill.UI;

/// The whole UI: one tray icon with a four-item menu. Mirrors the macOS menu
/// bar — a feather that turns red with a running elapsed counter while
/// recording, and a status line for transcription progress.
internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _toggle;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripSeparator _statusSeparator;
    private readonly Dictionary<bool, Icon> _icons = [];

    public Action? OnToggle { get; set; }
    public Action? OnOpenFolder { get; set; }
    public Action? OnQuit { get; set; }

    public TrayController()
    {
        _toggle = new ToolStripMenuItem("Start recording", null, (_, _) => OnToggle?.Invoke());
        _status = new ToolStripMenuItem("") { Enabled = false, Visible = false };
        _statusSeparator = new ToolStripSeparator { Visible = false };

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _toggle,
            _status,
            _statusSeparator,
            new ToolStripMenuItem("Open recordings folder", null, (_, _) => OnOpenFolder?.Invoke()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit quill", null, (_, _) => OnQuit?.Invoke()),
        });

        _icon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Visible = true,
            Text = "quill",
        };
        // Left click should feel like the macOS menu bar: one click, one menu.
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _icon.ContextMenuStrip?.Show(Cursor.Position);
        };

        SetIcon(recording: false);
        Notify.UseTrayIcon(_icon);
    }

    /// Reflect recording state: icon color, menu verb, and the elapsed clock
    /// in the tooltip (`elapsed` is null when idle).
    public void Update(bool recording, string? elapsed)
    {
        _toggle.Text = recording ? "Stop recording" : "Start recording";
        _icon.Text = recording ? $"quill — recording {elapsed}" : "quill";
        SetIcon(recording);
    }

    /// Show a transcription progress line, or hide it with null.
    public void UpdateTranscription(string? line)
    {
        _status.Text = line ?? "";
        _status.Visible = line is not null;
        _statusSeparator.Visible = line is not null;
    }

    /// Both feathers are drawn once and kept. Update() runs every second
    /// while recording, and regenerating a GDI icon per tick is how a tray
    /// app quietly eats handles.
    private void SetIcon(bool recording)
    {
        if (!_icons.TryGetValue(recording, out var icon))
        {
            icon = TrayIconFactory.Feather(recording);
            _icons[recording] = icon;
        }
        if (!ReferenceEquals(_icon.Icon, icon)) _icon.Icon = icon;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();
    }
}
