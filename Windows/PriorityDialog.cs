using System.Drawing;
using System.Windows.Forms;

namespace RemoteControl;

/// Small prompt for DeviceWindow's right-click "Set Priority..." - pre-fills the current
/// value, returns the new one (or null if cancelled). Higher wins ties on connection: a
/// device with strictly higher priority than whoever's currently connected preempts them -
/// see PairingCoordinator.TryClaimOrPreempt.
class PriorityDialog : Form
{
    private readonly NumericUpDown _priorityBox;
    public int? NewPriority { get; private set; }

    public PriorityDialog(int currentPriority)
    {
        Text = "Set connection priority";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(18, 20, 26);
        Width = 340;
        Height = 180;
        Padding = new Padding(20);
        KeyPreview = true;

        var label = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 9.5f),
            Text = "Higher priority preempts a connected device with a lower one:"
        };

        _priorityBox = new NumericUpDown
        {
            Dock = DockStyle.Top,
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(currentPriority, 0, 100),
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.Gainsboro,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10f),
            Margin = new Padding(0, 4, 0, 0)
        };

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.RightToLeft
        };

        var saveButton = new Button
        {
            Text = "Save",
            Width = 90,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.Gainsboro
        };
        DeviceWindow.StyleButton(saveButton);
        saveButton.Click += (_, _) => { NewPriority = (int)_priorityBox.Value; Close(); };

        var cancelButton = new Button
        {
            Text = "Cancel",
            Width = 90,
            Height = 30,
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
