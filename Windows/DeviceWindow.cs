using System.Drawing;
using System.Windows.Forms;
using QRCoder;

namespace RemoteControl;

/// Real, visible window (not hidden like TrayApp's marshaling helper) showing which
/// device is connected, the "Add New Device" pairing flow, and the list of trusted
/// devices. Opened via the tray menu, and auto-shown whenever a new device is actively
/// trying to pair against an already-open code.
class DeviceWindow : Form
{
    private readonly PairingCoordinator _pairing;
    private readonly string _localAddress;
    private readonly int _port;

    private readonly Label _statusLabel;
    private readonly Button _addDeviceButton;
    private readonly Panel _pairingPanel;
    private readonly Label _pairingCodeLabel;
    private readonly Label _pairingModelLabel;
    private readonly PictureBox _pairingQrBox;
    private readonly ListBox _trustedList;
    private readonly Button _forgetButton;

    private readonly List<TrustedDevice> _listedDevices = new();

    private const string DefaultPairingText =
        "Scan this QR with the new device's RemoteControl app (CONFIG tab → Scan QR to Connect), or type the code shown below:";

    public DeviceWindow(PairingCoordinator pairing, string localAddress, int port)
    {
        _pairing = pairing;
        _localAddress = localAddress;
        _port = port;

        Text = "RemoteControl - Devices";
        Width = 480;
        Height = 660;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 20, 26);
        Padding = new Padding(16);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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

        _addDeviceButton = new Button
        {
            Text = "+ Add New Device",
            Width = 200,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.Gainsboro,
            Margin = new Padding(0, 0, 0, 12)
        };
        StyleButton(_addDeviceButton);
        _addDeviceButton.Click += (_, _) => ToggleAddDevice();

        _pairingPanel = new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.FromArgb(30, 34, 44),
            Padding = new Padding(12),
            Visible = false,
            Margin = new Padding(0, 0, 0, 12),
            Width = 440
        };
        // FlowLayoutPanel instead of manual Location math - the model label's text (and
        // therefore its wrapped height) changes with every device name, so anything
        // stacked below it via a fixed Y offset would overlap once a name wraps to 2 lines.
        var pairingStack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        _pairingModelLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 9.5f),
            MaximumSize = new Size(400, 0),
            Text = DefaultPairingText,
            Margin = new Padding(0, 0, 0, 8)
        };
        _pairingCodeLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(94, 230, 201),
            Font = new Font("Consolas", 28f, FontStyle.Bold),
            Text = "------",
            Margin = new Padding(0, 0, 0, 10)
        };
        _pairingQrBox = new PictureBox
        {
            Size = new Size(200, 200),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White,
            Margin = new Padding(0)
        };
        pairingStack.Controls.Add(_pairingModelLabel);
        pairingStack.Controls.Add(_pairingCodeLabel);
        pairingStack.Controls.Add(_pairingQrBox);
        _pairingPanel.Controls.Add(pairingStack);

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
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.Gainsboro
        };
        StyleButton(_forgetButton);
        _forgetButton.Click += (_, _) => ForgetSelected();

        var listContainer = new Panel { Dock = DockStyle.Fill };
        listContainer.Controls.Add(_trustedList);
        listContainer.Controls.Add(_forgetButton);

        root.Controls.Add(_statusLabel, 0, 0);
        root.Controls.Add(_addDeviceButton, 0, 1);
        root.Controls.Add(_pairingPanel, 0, 2);
        root.Controls.Add(trustedLabel, 0, 3);
        root.Controls.Add(listContainer, 0, 4);
        Controls.Add(root);

        RefreshTrustedList();
        _pairing.DeviceApproved += _ => RefreshTrustedListThreadSafe();
        _pairing.DeviceForgotten += _ => RefreshTrustedListThreadSafe();
        _pairing.PairingOpened += code => RunOnUiThread(() => OnPairingOpened(code));
        _pairing.PairingClosed += () => RunOnUiThread(OnPairingClosed);
        _pairing.PairingAttemptStarted += model => RunOnUiThread(() => OnAttemptStarted(model));
        _pairing.PairingAttemptEnded += () => RunOnUiThread(OnAttemptEnded);
    }

    public void ShowConnected(string label)
    {
        RunOnUiThread(() => _statusLabel.Text = $"Connected: {label}");
    }

    public void ShowDisconnected()
    {
        RunOnUiThread(() => _statusLabel.Text = "No device connected");
    }

    private void ToggleAddDevice()
    {
        if (_pairing.OpenCode == null) _pairing.OpenPairing();
        else _pairing.ClosePairing();
    }

    private void OnPairingOpened(string code)
    {
        _pairingModelLabel.Text = DefaultPairingText;
        _pairingCodeLabel.Text = code;
        _pairingQrBox.Image?.Dispose();
        _pairingQrBox.Image = GenerateQrImage(BuildPairUri(code));
        _pairingPanel.Visible = true;
        _addDeviceButton.Text = "Cancel Pairing";
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private void OnPairingClosed()
    {
        _pairingPanel.Visible = false;
        _addDeviceButton.Text = "+ Add New Device";
    }

    /// A device is actively mid-handshake against the already-open code - the code/QR are
    /// already showing (pairing has to be open for this to fire at all), this just names
    /// who's trying and makes sure the window is actually in front of the user right now.
    private void OnAttemptStarted(string model)
    {
        _pairingModelLabel.Text = $"{model} is connecting - enter or scan the code below:";
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private void OnAttemptEnded()
    {
        // Only relevant if pairing is still open (a failed/timed-out attempt doesn't close
        // it - see PairingCoordinator) - revert the label back to the generic instruction
        // now that nobody's actively mid-handshake against it.
        if (_pairing.OpenCode != null) _pairingModelLabel.Text = DefaultPairingText;
    }

    /// Matches the `remotecontrol://pair` intent-filter MainActivity registers on the
    /// Android side - scanning this (with the app installed) opens straight into it with
    /// host/port pre-filled and the code auto-submitted, no typing needed.
    private string BuildPairUri(string code) =>
        $"remotecontrol://pair?host={Uri.EscapeDataString(_localAddress)}&port={_port}&code={code}";

    private static Image GenerateQrImage(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        byte[] png = new PngByteQRCode(data).GetGraphic(8);
        using var ms = new MemoryStream(png);
        return Image.FromStream(ms);
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

    /// FlatStyle.Flat buttons need every color set explicitly, or they fall back to
    /// WinForms' light-theme defaults - the near-invisible-text bug this fixes. Also adds
    /// a visible border and hover/press feedback so buttons don't look flat-out inert
    /// against the dark background.
    private static void StyleButton(Button button)
    {
        button.FlatAppearance.BorderColor = Color.FromArgb(70, 76, 92);
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 45, 58);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(50, 56, 72);
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
