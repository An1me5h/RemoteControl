using System.Drawing;
using System.Windows.Forms;

namespace RemoteControl;

/// Small prompt for DeviceWindow's right-click "Set Priority..." - pre-fills the current
/// value, returns the new one (or null if cancelled). LOWER wins: 1 is the best/highest
/// priority a device can have, and a device with a strictly lower number than whoever's
/// currently connected preempts them - see PairingCoordinator.TryClaimOrPreempt. Range is
/// 1-99 (not 0) - a device that's never had a priority set is treated as having none at
/// all (weaker than every explicitly-numbered device, never preempts and never gets
/// preempted by another unset device), not as secretly ranking above 1. This dialog itself
/// doesn't know about other devices - DeviceWindow's click handler is what actually
/// enforces "no two devices share a number", via PairingCoordinator.SetPriority's bool
/// return, looping this dialog back open with an error if the chosen number's taken.
class PriorityDialog : Form
{
    private readonly NumericUpDown _priorityBox;
    public int? NewPriority { get; private set; }

    public PriorityDialog(int currentPriority)
    {
        Text = "Set connection priority";
        Icon = DeviceWindow.AppIcon;
        // Sizable (was FixedDialog) plus AutoSize below - together these are what fixed the
        // dialog getting cut off: FixedDialog blocked the user from dragging it bigger to
        // see the rest, AND the Height was a hardcoded guess (180) sized for the OLD, much
        // shorter label text - once that text grew, nothing grew the window to match, so
        // the NumericUpDown and both buttons rendered completely off the bottom, invisible.
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(18, 20, 26);
        Width = 360;
        // AutoSize (default AutoSizeMode.GrowOnly) sizes the form's height to whatever its
        // stacked Dock=Top/Bottom children actually need - the label's own AutoSize height
        // included - instead of a hardcoded number that has to be re-guessed every time the
        // text changes. GrowOnly (not GrowAndShrink) is the deliberate choice: it sets a
        // content-driven FLOOR the user can still drag bigger via the Sizable border above,
        // rather than fighting every manual resize back down to the "preferred" size.
        AutoSize = true;
        MinimumSize = new Size(360, 220);
        Padding = new Padding(20);
        KeyPreview = true;

        var label = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            // A Dock=Top AutoSize label needs an explicit MaximumSize.Width to wrap at all -
            // without one, its preferred-size pass measures the text at its natural
            // (unwrapped) width, gets a single-line height, and THEN Dock squashes it to the
            // form's real width - meaning the text renders on one line and gets clipped
            // instead of wrapping to as many lines as AutoSize actually reserved room for.
            // Confirmed missing here via an off-screen DrawToBitmap render before this fix.
            MaximumSize = new Size(310, 0),
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 9.5f),
            Text = "Lower number = higher priority (1-99, no two devices can share one). " +
                   "A device with no priority set never preempts anyone.",
            Margin = new Padding(0, 0, 0, 6)
        };

        _priorityBox = new NumericUpDown
        {
            Dock = DockStyle.Top,
            Minimum = 1,
            Maximum = 99,
            Value = Math.Clamp(currentPriority <= 0 ? 1 : currentPriority, 1, 99),
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.Gainsboro,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10f),
            Margin = new Padding(0, 4, 0, 0)
        };

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 40),
            FlowDirection = FlowDirection.RightToLeft,
            // These two buttons are meant to sit side by side, always - WrapContents=false
            // (default is true) means if they ever genuinely don't fit, the panel grows
            // WIDER instead of silently wrapping to a second row, which is what was eating
            // the second button entirely while this row's height stayed a fixed 40px.
            WrapContents = false
        };

        var saveButton = new Button
        {
            Text = "Save",
            // AutoSize + a MinimumSize floor instead of a hardcoded Width/Height - see
            // RenameDeviceDialog's identical fix for why (fixed 90x30 clipped the text).
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(90, 32),
            Padding = new Padding(8, 4, 8, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.Gainsboro
        };
        DeviceWindow.StyleButton(saveButton);
        saveButton.Click += (_, _) => { NewPriority = (int)_priorityBox.Value; Close(); };

        var cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(90, 32),
            Padding = new Padding(8, 4, 8, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.Gainsboro,
            Margin = new Padding(0, 0, 10, 0)
        };
        DeviceWindow.StyleButton(cancelButton);
        cancelButton.Click += (_, _) => { NewPriority = null; Close(); };

        buttonRow.Controls.Add(saveButton);
        buttonRow.Controls.Add(cancelButton);

        Controls.Add(_priorityBox);
        Controls.Add(label);
        Controls.Add(buttonRow);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Shown += (_, _) => { _priorityBox.Focus(); _priorityBox.Select(0, _priorityBox.Text.Length); };
    }
}
