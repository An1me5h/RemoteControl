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
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(18, 20, 26);
        Width = 340;
        Height = 160;
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
        saveButton.Click += (_, _) => Accept();

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
