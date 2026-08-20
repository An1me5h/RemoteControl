using System.Drawing;
using System.Windows.Forms;

namespace RemoteControl;

/// Small prompt for DeviceWindow's right-click "Rename" - pre-fills the current name,
/// returns the new one (or null if cancelled/left blank/unchanged).
class RenameDeviceDialog : Form
{
    private readonly TextBox _nameBox;
    public string? NewName { get; private set; }

    public RenameDeviceDialog(string currentName)
    {
        Text = "Rename device";
        Icon = DeviceWindow.AppIcon;
        // Sizable + AutoSize (not a hardcoded Height) - same fix as PriorityDialog's, so
        // this one can't silently clip again the next time its content changes.
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(18, 20, 26);
        Width = 360;
        AutoSize = true;
        MinimumSize = new Size(340, 170);
        Padding = new Padding(20);
        KeyPreview = true;

        var label = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 9.5f),
            Text = "Device name:"
        };

        _nameBox = new TextBox
        {
            Dock = DockStyle.Top,
            Text = currentName,
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
            WrapContents = false
        };

        var saveButton = new Button
        {
            Text = "Save",
            // AutoSize + a MinimumSize floor instead of a hardcoded Width/Height - the
            // fixed 90x30 didn't leave enough room for this font's real glyph metrics,
            // clipping the text top/bottom (reported, confirmed on screen). Same fix
            // CloseConfirmDialog's buttons already got; this dialog and PriorityDialog's
            // had been missed.
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(90, 32),
            Padding = new Padding(8, 4, 8, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.Gainsboro
        };
        DeviceWindow.StyleButton(saveButton);
        saveButton.Click += (_, _) => Accept();

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
        cancelButton.Click += (_, _) => { NewName = null; Close(); };

        buttonRow.Controls.Add(saveButton);
        buttonRow.Controls.Add(cancelButton);

        Controls.Add(_nameBox);
        Controls.Add(label);
        Controls.Add(buttonRow);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Shown += (_, _) => { _nameBox.Focus(); _nameBox.SelectAll(); };
    }

    private void Accept()
    {
        string trimmed = _nameBox.Text.Trim();
        NewName = trimmed.Length > 0 ? trimmed : null;
        Close();
    }
}
