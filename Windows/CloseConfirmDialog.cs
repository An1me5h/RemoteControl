using System.Drawing;
using System.Windows.Forms;

namespace RemoteControl;

/// Shown when the user clicks the X on DeviceWindow - RemoteControl doesn't actually quit
/// on close (it's a tray app), but silently hiding the window with no explanation left the
/// user unsure whether it had actually closed. This makes that choice explicit instead.
class CloseConfirmDialog : Form
{
    public enum Choice { Minimize, Exit }

    /// Minimize is the safe default - if the dialog is dismissed any way OTHER than clicking
    /// "Disconnect & Exit" (Escape, the X button, clicking outside if it somehow lost focus),
    /// nothing destructive happens.
    public Choice Result { get; private set; } = Choice.Minimize;

    public CloseConfirmDialog()
    {
        Text = "RemoteControl";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 20, 26);
        Width = 360;
        Height = 190;
        Padding = new Padding(20);
        KeyPreview = true;

        var message = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 70,
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 9.5f),
            Text = "RemoteControl is still running in the background - your phone can " +
                   "keep controlling this PC. Minimize it, or disconnect and fully exit?"
        };

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.RightToLeft
        };

        var exitButton = new Button
        {
            Text = "Disconnect && Exit",
            Width = 150,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.FromArgb(230, 150, 150), // a warning tint - this one actually ends the session
            Margin = new Padding(0, 0, 0, 0)
        };
        DeviceWindow.StyleButton(exitButton);
        exitButton.Click += (_, _) => { Result = Choice.Exit; Close(); };

        var minimizeButton = new Button
        {
            Text = "Minimize to Tray",
            Width = 150,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.Gainsboro,
            Margin = new Padding(0, 0, 10, 0)
        };
        DeviceWindow.StyleButton(minimizeButton);
        minimizeButton.Click += (_, _) => { Result = Choice.Minimize; Close(); };

        buttonRow.Controls.Add(exitButton);
        buttonRow.Controls.Add(minimizeButton);

        Controls.Add(message);
        Controls.Add(buttonRow);

        AcceptButton = minimizeButton; // Enter = the safe choice
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); }; // Result stays Minimize
    }
}
