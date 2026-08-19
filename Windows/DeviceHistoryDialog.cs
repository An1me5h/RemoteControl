using System.Drawing;
using System.Windows.Forms;

namespace RemoteControl;

/// Read-only popup showing one trusted device's full event log (paired, connected,
/// disconnected, renamed) - opened via DeviceWindow's right-click "View History".
class DeviceHistoryDialog : Form
{
    public DeviceHistoryDialog(TrustedDevice device)
    {
        Text = $"History - {device.Name}";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(18, 20, 26);
        Width = 420;
        Height = 420;
        MinimumSize = new Size(320, 260);
        Padding = new Padding(16);

        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(24, 27, 35),
            ForeColor = Color.Gainsboro,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9.5f),
            IntegralHeight = false
        };

        // Newest first - what just happened is almost always the reason someone opened this.
        foreach (var entry in device.History.OrderByDescending(h => h.At))
        {
            list.Items.Add($"{entry.At:yyyy-MM-dd HH:mm}   {entry.EventText}");
        }
        if (list.Items.Count == 0) list.Items.Add("(no recorded history)");

        var closeButton = new Button
        {
            Text = "Close",
            Dock = DockStyle.Bottom,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.Gainsboro,
            Margin = new Padding(0, 12, 0, 0)
        };
        DeviceWindow.StyleButton(closeButton);
        closeButton.Click += (_, _) => Close();

        var container = new Panel { Dock = DockStyle.Fill };
        container.Controls.Add(list);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(container, 0, 0);
        root.Controls.Add(closeButton, 0, 1);
        Controls.Add(root);

        AcceptButton = closeButton;
    }
}
