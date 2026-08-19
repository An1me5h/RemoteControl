using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RemoteControl;

/// Small tray icons, generated at runtime (no .ico asset needed) - a rounded-rectangle
/// "screen" shape (gray while idle, green while a device is connected) with an optional
/// red dot in the top-right corner. TrayApp blinks between ConnectedDotOn/ConnectedDotOff
/// while connected, so the red dot reads as a live/active indicator rather than a static
/// decoration - the green base shape itself stays constant, only the dot blinks.
static class TrayIcons
{
    public static readonly Icon Idle = MakeMonitorIcon(Color.Gray, showRedDot: false);
    public static readonly Icon ConnectedDotOn = MakeMonitorIcon(Color.MediumSeaGreen, showRedDot: true);
    public static readonly Icon ConnectedDotOff = MakeMonitorIcon(Color.MediumSeaGreen, showRedDot: false);

    private static Icon MakeMonitorIcon(Color baseColor, bool showRedDot)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var path = RoundedRect(new RectangleF(1, 2, 14, 11), 3f))
            using (var brush = new SolidBrush(baseColor))
            {
                g.FillPath(brush, path);
            }

            if (showRedDot)
            {
                using var redBrush = new SolidBrush(Color.FromArgb(235, 60, 60));
                g.FillEllipse(redBrush, 10, 0, 6, 6);
            }
        }

        nint hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone(); // clone owns its own copy, safe to destroy the HICON below
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);
}
