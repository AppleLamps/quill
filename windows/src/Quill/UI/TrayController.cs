using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Quill.UI;

/// What the tray is showing. `Finalizing` is the brief window after a stop
/// while both tracks drain and meta.json is written — the session is no longer
/// capturing, but starting another one would fight it for the devices.
internal enum TrayState
{
    Idle,
    Recording,
    Finalizing,
}

/// The whole UI: one tray icon with a four-item menu. Mirrors the macOS menu
/// bar — a feather that turns red with a running elapsed counter while
/// recording, and a status line for transcription progress.
internal sealed class TrayController : IDisposable
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _toggle;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripSeparator _statusSeparator;
    private readonly Dictionary<bool, Icon> _icons = [];
    private readonly SynchronizationContext? _ui;
    private bool _recording;

    public Action? OnToggle { get; set; }
    public Action? OnOpenFolder { get; set; }
    public Action? OnQuit { get; set; }

    public TrayController()
    {
        _ui = SynchronizationContext.Current;

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
            if (e.Button == MouseButtons.Left) ShowMenu();
        };

        SetIcon(recording: false);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Notify.UseTrayIcon(_icon);
    }

    /// Reflect recording state: icon color, menu verb, and the elapsed clock
    /// in the tooltip (`elapsed` is only meaningful while recording).
    public void Update(TrayState state, string? elapsed = null)
    {
        _recording = state == TrayState.Recording;
        _toggle.Text = _recording ? "Stop recording" : "Start recording";
        // Nothing sensible to do with a click mid-drain, and re-entering
        // StartSession would open the devices the old session is still closing.
        _toggle.Enabled = state != TrayState.Finalizing;
        _icon.Text = state switch
        {
            TrayState.Recording => $"quill — recording {elapsed}",
            TrayState.Finalizing => "quill — finishing recording",
            _ => "quill",
        };
        SetIcon(_recording);
    }

    /// Show a transcription progress line, or hide it with null.
    public void UpdateTranscription(string? line)
    {
        _status.Text = line ?? "";
        _status.Visible = line is not null;
        _statusSeparator.Visible = line is not null;
    }

    /// Windows dismisses a popup only while the window owning it is in the
    /// foreground. NotifyIcon does that dance itself for right-click, but a
    /// menu we Show() by hand would otherwise linger on screen after the user
    /// clicks into another application.
    private void ShowMenu()
    {
        _menu.Show(Cursor.Position);
        SetForegroundWindow(_menu.Handle);
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

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color
            or UserPreferenceCategory.VisualStyle)
            OnUi(RedrawIcons);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => OnUi(RedrawIcons);

    /// Drop the cached feathers and redraw them. The idle color is chosen from
    /// the taskbar theme and the size from the display, so a cache that
    /// outlives either leaves an invisible icon (a white feather on a light
    /// taskbar) or a blurry one until the next restart.
    private void RedrawIcons()
    {
        var stale = _icons.Values.ToList();
        _icons.Clear();
        SetIcon(_recording);
        foreach (var icon in stale) icon.Dispose();
    }

    /// SystemEvents raises on its own thread; every field below belongs to the
    /// UI thread.
    private void OnUi(Action action)
    {
        if (_ui is { } ui) ui.Post(_ => action(), null);
        else action();
    }

    public void Dispose()
    {
        // SystemEvents holds a static, strong reference to its subscribers.
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();
    }
}
