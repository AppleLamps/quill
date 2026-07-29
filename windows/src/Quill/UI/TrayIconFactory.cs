using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Quill.UI;

/// Draws quill's feather at runtime. Generating the icon beats shipping .ico
/// resources: one code path scales to any DPI, and the recording state is the
/// same glyph in red, so the tray never flickers between mismatched art.
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// The tray follows the taskbar theme, not the app theme, so a white
    /// feather disappears on a light taskbar.
    private static Color IdleColor()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var lightTaskbar = key?.GetValue("SystemUsesLightTheme") as int? == 1;
        return lightTaskbar ? Color.FromArgb(255, 32, 32, 32) : Color.White;
    }

    /// The notification area asks for a small-icon-sized bitmap, which scales
    /// with DPI. Never go below 32 even when Windows asks for 16: the shell
    /// downscales a detailed glyph better than this path draws a cramped one.
    private static int PreferredSize => Math.Max(32, SystemInformation.SmallIconSize.Width);

    public static Icon Feather(bool recording) => Feather(recording, PreferredSize);

    public static Icon Feather(bool recording, int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var color = recording ? Color.FromArgb(255, 236, 68, 68) : IdleColor();
            using var pen = new Pen(color, Math.Max(1.5f, size / 16f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            using var brush = new SolidBrush(color);

            var s = size / 32f;
            // Shaft, tip at top-right down to the quill point at bottom-left.
            g.DrawLine(pen, 25 * s, 5 * s, 8 * s, 27 * s);

            // Vane: a teardrop hanging off the shaft, clipped by the barb cuts.
            using var vane = new GraphicsPath();
            vane.AddBezier(25 * s, 5 * s, 10 * s, 6 * s, 6 * s, 16 * s, 11 * s, 23 * s);
            vane.AddBezier(11 * s, 23 * s, 20 * s, 22 * s, 25 * s, 15 * s, 25 * s, 5 * s);
            g.FillPath(brush, vane);

            using var cut = new Pen(Color.Transparent, Math.Max(1f, size / 24f));
            g.CompositingMode = CompositingMode.SourceCopy;
            for (var i = 1; i <= 3; i++)
            {
                var t = i / 4f;
                g.DrawLine(cut,
                    25 * s - 14 * s * t, 5 * s + 18 * s * t,
                    25 * s - 6 * s * t, 5 * s + 10 * s * t);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Clone so the icon survives DestroyIcon; leaking HICONs from a
            // long-running tray app is how you exhaust GDI handles.
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
