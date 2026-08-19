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
        // Sizable + AutoSize (not a hardcoded Height) - same fix as PriorityDialog's and
        // RenameDeviceDialog's: FixedDialog blocked the user from dragging this bigger, and
        // the message label's own hardcoded Height (70, AutoSize=false) was a guess that
        // didn't leave enough room, so the two buttons below it rendered off the bottom of
        // the visible window entirely - the exact same class of bug, just triggered by the
        // close/quit path instead of a right-click menu.
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 20, 26);
        Width = 460;
        AutoSize = true;
        MinimumSize = new Size(440, 210);
        Padding = new Padding(20);
        KeyPreview = true;

        var message = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            // See PriorityDialog's label for why MaximumSize.Width is required, not
            // optional, for a Dock=Top AutoSize label to actually wrap instead of rendering
            // one clipped line.
            MaximumSize = new Size(410, 0),
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 9.5f),
            Text = "RemoteControl is still running in the background - your phone can " +
                   "keep controlling this PC. Minimize it, or disconnect and fully exit?",
            Margin = new Padding(0, 0, 0, 6)
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

        var exitButton = new Button
        {
            Text = "Disconnect && Exit",
            // AutoSize + a MinimumSize floor instead of a hardcoded Width - the hardcoded
            // 150 turned out to be too narrow for this text (confirmed by an off-screen
            // render: "Disconnect &" was visibly clipped), the same class of bug fixed for
            // DeviceWindow's own buttons earlier this session.
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(150, 32),
            Padding = new Padding(8, 4, 8, 4),
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
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(150, 32),
            Padding = new Padding(8, 4, 8, 4),
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
