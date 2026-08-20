using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RemoteControl;

/// Full-virtual-desktop, click-drag rectangle picker for ScreenStreamer's capture region -
/// same interaction as a screenshot/snipping tool. Caller does `using var picker = new
/// RegionPickerForm(); picker.ShowDialog();` then reads SelectedRegion (null if cancelled
/// via Escape, or if the drag was too small to be a real selection).
class RegionPickerForm : Form
{
    private const int MinSelectionSize = 20;

    public Rectangle? SelectedRegion { get; private set; }

    private Point _dragStart;
    private Rectangle _dragRect;
    private bool _dragging;

    public RegionPickerForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        // Covers the WHOLE virtual desktop (every monitor), not just the primary one -
        // ScreenStreamer only ever captures the primary screen today
        // (GetSystemMetrics SM_CXSCREEN/SM_CYSCREEN), so the final selection is clamped to
        // that below. Showing the full virtual desktop here just means a drag that strays
        // onto a second monitor doesn't feel like it hit an invisible wall mid-gesture.
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        // A real per-pixel-layered window (WS_EX_LAYERED under the hood) - the actual live
        // desktop shows through, dimmed by this opacity, not a static pre-captured image.
        Opacity = 0.35;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _dragging = true;
        _dragStart = e.Location;
        _dragRect = new Rectangle(e.Location, Size.Empty);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragRect = MakeRect(_dragStart, e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _dragRect = MakeRect(_dragStart, e.Location);

        // A genuine drag-select needs real size - an accidental single click (or a
        // barely-moved one) shouldn't be treated as "select a near-zero-pixel region",
        // it should just cancel like Escape does.
        if (_dragRect.Width >= MinSelectionSize && _dragRect.Height >= MinSelectionSize)
        {
            // This form's own client coordinates start at Bounds.Location
            // (SystemInformation.VirtualScreen.Location, which can be negative for a
            // monitor positioned left of/above the primary) - add that back to turn the
            // drag rectangle (drawn in this form's LOCAL coordinates) into real screen
            // coordinates, which is what ScreenStreamer's CopyFromScreen expects.
            var origin = SystemInformation.VirtualScreen.Location;
            SelectedRegion = new Rectangle(
                _dragRect.X + origin.X, _dragRect.Y + origin.Y, _dragRect.Width, _dragRect.Height);
        }
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            SelectedRegion = null;
            Close();
        }
    }

    private static Rectangle MakeRect(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_dragRect.IsEmpty) return;
        using var fill = new SolidBrush(Color.FromArgb(40, 94, 230, 201));
        using var pen = new Pen(Color.FromArgb(94, 230, 201), 2);
        e.Graphics.FillRectangle(fill, _dragRect);
        e.Graphics.DrawRectangle(pen, _dragRect);
    }
}
