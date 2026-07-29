namespace Quill;

/// Toast-style notifications through the tray icon. Windows routes balloon
/// tips from a NotifyIcon into the Action Center, which is as close as we get
/// to macOS UserNotifications without shipping a packaged app identity.
internal static class Notify
{
    private static NotifyIcon? _sink;
    private static SynchronizationContext? _ui;

    /// Register the tray icon that owns notifications, along with the UI
    /// context it must be touched from. Without it, notifications degrade to
    /// stderr — which is all `quill doctor` and friends need.
    public static void UseTrayIcon(NotifyIcon icon)
    {
        _sink = icon;
        _ui = SynchronizationContext.Current;
    }

    public static void User(string title, string body)
    {
        Console.Error.WriteLine($"{title}: {body}");
        var sink = _sink;
        if (sink is null) return;
        if (_ui is { } ui) ui.Post(_ => Balloon(sink, title, body), null);
        else Balloon(sink, title, body);
    }

    private static void Balloon(NotifyIcon icon, string title, string body)
    {
        try
        {
            icon.BalloonTipTitle = title;
            icon.BalloonTipText = body;
            icon.BalloonTipIcon = ToolTipIcon.None;
            icon.ShowBalloonTip(5000);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // The tray icon went away mid-notification; stderr already has it.
        }
    }
}
