using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RemoteControl;

/// Small solid-dot tray icons, generated at runtime (no .ico asset needed) - mirrors
/// the connection-status dot on the Android side.
static class TrayIcons
{
    public static readonly Icon Idle = MakeDotIcon(Color.Gray);
    public static readonly Icon Connected = MakeDotIcon(Color.MediumSeaGreen);

    private static Icon MakeDotIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 13, 13);
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

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);
}
