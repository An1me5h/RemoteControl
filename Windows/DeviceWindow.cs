using System.Drawing;
using System.Windows.Forms;

namespace RemoteControl;

/// Real, visible window (not hidden like TrayApp's marshaling helper) showing which
/// device is connected, any in-progress pairing code, and the list of trusted devices.
/// Opened via the tray menu, and auto-shown whenever a new device needs a pairing code -
/// that's the one moment the user genuinely has to look at it to keep going.
class DeviceWindow : Form
{
    private readonly PairingCoordinator _pairing;

    private readonly Label _statusLabel;
    private readonly Panel _pairingPanel;
    private readonly Label _pairingCodeLabel;
    private readonly Label _pairingModelLabel;
    private readonly ListBox _trustedList;
    private readonly Button _forgetButton;

    private readonly List<TrustedDevice> _listedDevices = new();

    public DeviceWindow(PairingCoordinator pairing)
    {
        _pairing = pairing;

        Text = "RemoteControl - Devices";
        Width = 480;
        Height = 480;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 20, 26);
        Padding = new Padding(16);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _statusLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            Text = "No device connected",
            Margin = new Padding(0, 0, 0, 12)
        };

        _pairingPanel = new Panel
        {
            AutoSize = true,
            BackColor = Color.FromArgb(30, 34, 44),
            Padding = new Padding(12),
            Visible = false,
            Margin = new Padding(0, 0, 0, 12),
            Width = 440
        };
        _pairingModelLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 9.5f),
            Text = ""
        };
        _pairingCodeLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(94, 230, 201),
            Font = new Font("Consolas", 28f, FontStyle.Bold),
            Location = new Point(0, 22),
            Text = "------"
        };
        _pairingPanel.Controls.Add(_pairingModelLabel);
        _pairingPanel.Controls.Add(_pairingCodeLabel);
        _pairingPanel.Height = 90;

        var trustedLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            Text = "Trusted devices - select one and click Forget to revoke it",
            MaximumSize = new Size(440, 0),
            Margin = new Padding(0, 0, 0, 6)
        };

        _trustedList = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(24, 27, 35),
            ForeColor = Color.Gainsboro,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9.5f)
        };

        _forgetButton = new Button
        {
            Text = "Forget selected device",
            Dock = DockStyle.Bottom,
            Height = 32,
            FlatStyle = FlatStyle.Flat
        };
        _forgetButton.Click += (_, _) => ForgetSelected();

        var listContainer = new Panel { Dock = DockStyle.Fill };
        listContainer.Controls.Add(_trustedList);
        listContainer.Controls.Add(_forgetButton);

        root.Controls.Add(_statusLabel, 0, 0);
        root.Controls.Add(_pairingPanel, 0, 1);
        root.Controls.Add(trustedLabel, 0, 2);
        root.Controls.Add(listContainer, 0, 3);
        Controls.Add(root);

        RefreshTrustedList();
        _pairing.DeviceApproved += _ => RefreshTrustedListThreadSafe();
        _pairing.DeviceForgotten += _ => RefreshTrustedListThreadSafe();
    }

    public void ShowConnected(string label)
    {
        RunOnUiThread(() => _statusLabel.Text = $"Connected: {label}");
    }

    public void ShowDisconnected()
    {
        RunOnUiThread(() => _statusLabel.Text = "No device connected");
    }

    public void ShowPairingCode(string code, string model)
    {
        RunOnUiThread(() =>
        {
            _pairingModelLabel.Text = $"A new device wants to connect: {model}\nEnter this code on the phone:";
            _pairingCodeLabel.Text = code;
            _pairingPanel.Visible = true;
            Show();
            WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        });
    }

    public void HidePairingCode()
    {
        RunOnUiThread(() => _pairingPanel.Visible = false);
    }

    private void RefreshTrustedListThreadSafe() => RunOnUiThread(RefreshTrustedList);

    private void RefreshTrustedList()
    {
        _listedDevices.Clear();
        _listedDevices.AddRange(_pairing.TrustedDevices);
        _trustedList.Items.Clear();
        foreach (var d in _listedDevices)
        {
            _trustedList.Items.Add($"{d.Name}  -  paired {d.PairedAt:yyyy-MM-dd HH:mm}");
        }
    }

    private void ForgetSelected()
    {
        int index = _trustedList.SelectedIndex;
        if (index < 0 || index >= _listedDevices.Count) return;
        _pairing.Forget(_listedDevices[index].DeviceId);
    }

    private void RunOnUiThread(Action action)
    {
        if (InvokeRequired)
        {
            BeginInvoke(action);
            return;
        }
        action();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        e.Cancel = true;
        Hide(); // keep it alive so the tray menu can reopen it instantly
        base.OnFormClosing(e);
    }
}
