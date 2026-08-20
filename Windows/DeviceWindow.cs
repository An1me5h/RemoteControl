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
    private readonly Action _requestExit;
    private readonly Action<string> _disconnectDevice;

    private readonly Label _statusLabel;
    private readonly Label _tailscaleLabel;
    private readonly Button _addDeviceButton;
    private readonly Button _addRemoteDeviceButton;
    private readonly Panel _pairingPanel;
    private readonly Label _pairingCodeLabel;
    private readonly Label _pairingModelLabel;
    private readonly PictureBox _pairingQrBox;
    private readonly DataGridView _trustedGrid;
    private readonly Button _forgetButton;

    private readonly List<TrustedDevice> _listedDevices = new();

    // Which device (by id) the highlighted row belongs to - kept in sync by
    // ShowConnected/ShowDisconnected, used by RefreshTrustedList to color that one row and
    // by the row context menu to decide whether "Disconnect" applies to it.
    private string? _connectedDeviceId;
    // Whether the CURRENTLY connected device (above) arrived over Tailscale - drives the
    // status label's "via LAN"/"via Tailscale" suffix and which row (primary vs the
    // synthetic remote-access row) gets the "connected" highlight in RefreshTrustedList.
    private bool _connectedViaRemote;

    // Which host the currently-OPEN pairing code/QR targets - set when either "Add"
    // button opens pairing, read by OnPairingOpened/OnAttemptEnded to rebuild the right
    // QR and instruction text. Irrelevant while pairing is closed.
    private bool _pairingIsRemote;

    // Guards the one-time column auto-fit (see Shown handler in the constructor) against
    // running more than once, in case Shown ends up firing again on a later Hide()+Show()
    // cycle - only the very first real layout should decide the columns' widths.
    private bool _columnsAutoFitted;

    // Exact pixel height OnPairingOpened added to the window to fit the newly-revealed
    // pairing panel - OnPairingClosed subtracts precisely this back out (not a recomputed
    // value) so repeated open/close cycles can't drift the window size even if the panel's
    // own content height varies between the two (e.g. LAN vs remote instruction text wraps
    // to a different number of lines). 0 while pairing is closed.
    private int _pairingPanelHeightAdded;

    private const string DateFormat = "yyyy-MM-dd HH:mm";

    public DeviceWindow(PairingCoordinator pairing, string localAddress, int port,
                        Action requestExit, Action<string> disconnectDevice)
    {
        _pairing = pairing;
        _localAddress = localAddress;
        _port = port;
        _requestExit = requestExit;
        _disconnectDevice = disconnectDevice;

        Text = "RemoteControl - Devices";
        Icon = AppIcon;
        // Widened from 480 -> 680 -> 840 -> 1180 across this file's history, each time to
        // fit the trusted-devices table's growing column count without cramming. This last
        // jump is bigger than the others because it's based on a REAL measurement, not
        // another guess: an off-screen render (with the Shown-hooked AutoResizeColumns
        // below actually having run) showed the 6 fit-to-content columns needing 1076px
        // combined against a client area that only had 778px to give them at the old 840
        // window width - a ~300px shortfall, which is exactly what pushed Permission and
        // Priority off the visible right edge into horizontal-scroll territory.
        Width = 1180;
        Height = 660;
        // The pairing panel and its labels below are sized against fixed pixel widths
        // (_pairingPanel.Width = 440, trustedLabel's MaximumSize = 440, _pairingModelLabel's
        // MaximumSize = 400) rather than the window's actual current width - simpler than
        // recomputing wrap widths on every Resize, but it means shrinking the window below
        // what those fixed widths need makes the text wrap wider than the visible window and
        // spill past its right edge instead of narrowing along with it. MinimumSize stops the
        // window from ever getting that narrow in the first place, matching the size the
        // layout was actually designed for (680 now also covers the grid's minimum usable
        // width, not just the pairing panel's).
        MinimumSize = new Size(900, 420);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 20, 26);
        Padding = new Padding(16);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
            // Unlike trustedLabel/_pairingModelLabel (already fixed, see the 2026-08-19
            // text-overflow entry), this one had NO width cap at all - "Connected: " plus a
            // long device model/name (some Android model strings run long) could extend
            // straight past the window's edge instead of wrapping. Matches the same 440
            // budget the other labels already wrap against.
            MaximumSize = new Size(440, 0),
            Margin = new Padding(0, 0, 0, 12)
        };

        _tailscaleLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(140, 148, 165),
            Font = new Font("Segoe UI", 9f),
            Text = "Tailscale: checking...",
            MaximumSize = new Size(440, 0),
            Margin = new Padding(0)
        };
        var tailscaleRefreshButton = new Button
        {
            Text = "Refresh",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6, 2, 6, 2),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.Gainsboro,
            Margin = new Padding(8, 0, 0, 0)
        };
        StyleButton(tailscaleRefreshButton);
        // Manual, not polled - Tailscale connecting/disconnecting while this window is
        // already open has no OS event this app can subscribe to, so re-checking is on the
        // user's own click rather than a background timer.
        tailscaleRefreshButton.Click += (_, _) => RefreshTailscaleLabel();

        var tailscaleCopyButton = new Button
        {
            Text = "Copy",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6, 2, 6, 2),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.Gainsboro,
            Margin = new Padding(4, 0, 0, 0)
        };
        StyleButton(tailscaleCopyButton);
        // Re-resolves the IP at click time rather than reading _tailscaleLabel's text back -
        // avoids parsing the label's own display string just to recover the value.
        tailscaleCopyButton.Click += (_, _) =>
        {
            string? ip = TailscaleHelper.GetIPv4();
            if (ip != null) Clipboard.SetText($"{ip}:{_port}");
        };
        var tailscaleRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 12)
        };
        tailscaleRow.Controls.Add(_tailscaleLabel);
        tailscaleRow.Controls.Add(tailscaleRefreshButton);
        tailscaleRow.Controls.Add(tailscaleCopyButton);

        _addDeviceButton = new Button
        {
            Text = "+ Add New Device (LAN)",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(200, 0),
            Padding = new Padding(8, 6, 8, 6),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.Gainsboro,
            Margin = new Padding(0, 0, 8, 12)
        };
        StyleButton(_addDeviceButton);
        _addDeviceButton.Click += (_, _) => TogglePairing(remote: false);

        // Separate button rather than a toggle on the same one - the code/QR this
        // generates is only reachable from off this LAN (it embeds the Tailscale IP, not
        // the LAN one - see BuildPairUri), so scanning it while standing right next to the
        // PC on the LAN would connect over Tailscale needlessly. Keeping the two visually
        // distinct also fixes the actual bug report: the old single button's QR always
        // used the LAN address regardless of intent, so "pairing for remote access" was
        // silently establishing a LAN connection instead.
        _addRemoteDeviceButton = new Button
        {
            Text = "+ Add Remote Device (Tailscale)",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(200, 0),
            Padding = new Padding(8, 6, 8, 6),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.Gainsboro,
            Margin = new Padding(0, 0, 0, 12)
        };
        StyleButton(_addRemoteDeviceButton);
        _addRemoteDeviceButton.Click += (_, _) => TogglePairing(remote: true);

        var addDeviceRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0)
        };
        addDeviceRow.Controls.Add(_addDeviceButton);
        addDeviceRow.Controls.Add(_addRemoteDeviceButton);

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
            Text = PairingInstructionText(remote: false),
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
            Text = "Trusted devices - right-click a row for more options, or select one and click Forget to revoke it",
            MaximumSize = new Size(620, 0),
            Margin = new Padding(0, 0, 0, 6)
        };

        _trustedGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.FromArgb(24, 27, 35),
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9.5f),
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            // EnableHeadersVisualStyles must be off, or Windows' own visual-styles renderer
            // draws the header background/text and ignores ColumnHeadersDefaultCellStyle
            // entirely - a common WinForms dark-theme gotcha, same species of bug as the
            // FlatStyle.Flat button colors fixed earlier in this file.
            EnableHeadersVisualStyles = false,
            GridColor = Color.FromArgb(40, 45, 58),
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            RowTemplate = { Height = 26 }
        };
        _trustedGrid.DefaultCellStyle.BackColor = Color.FromArgb(24, 27, 35);
        _trustedGrid.DefaultCellStyle.ForeColor = Color.Gainsboro;
        _trustedGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(45, 52, 66);
        _trustedGrid.DefaultCellStyle.SelectionForeColor = Color.Gainsboro;
        _trustedGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(30, 34, 44);
        _trustedGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Gainsboro;
        _trustedGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _trustedGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _trustedGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;

        // Every column is independently user-resizable (AutoSizeMode.None on all of them,
        // including Name) - Name used to be AutoSizeMode.Fill, which is what made dragging
        // any OTHER column's border feel like it was resizing backwards: a Fill column
        // silently absorbs whatever width a neighboring drag frees up, so the border under
        // the cursor doesn't move the way a plain drag normally would. Widths below are
        // just reasonable starting points - AutoResizeColumns (after the constructor's
        // first RefreshTrustedList, further down) immediately fits every column to its
        // actual header/content on first show, same as a spreadsheet's "fit to content".
        _trustedGrid.AllowUserToResizeColumns = true;
        _trustedGrid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Name", HeaderText = "Name", Width = 160, MinimumWidth = 80, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _trustedGrid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Paired", HeaderText = "Paired", Width = 120, MinimumWidth = 60, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _trustedGrid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "LastConnected", HeaderText = "Last Connected", Width = 130, MinimumWidth = 60, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _trustedGrid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "LastDisconnected", HeaderText = "Last Disconnected", Width = 135, MinimumWidth = 60, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _trustedGrid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Permission", HeaderText = "Permission", Width = 95, MinimumWidth = 60, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _trustedGrid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Priority", HeaderText = "Priority", Width = 60, MinimumWidth = 50, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });

        // Right-click doesn't select a row by default the way a left-click does - without
        // this, the context menu would act on whatever row was PREVIOUSLY selected (or
        // none), not the one actually under the cursor.
        _trustedGrid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            _trustedGrid.ClearSelection();
            _trustedGrid.Rows[e.RowIndex].Selected = true;
            _trustedGrid.CurrentCell = _trustedGrid.Rows[e.RowIndex].Cells[0];
        };
        _trustedGrid.ContextMenuStrip = BuildRowContextMenu();

        _forgetButton = new Button
        {
            Text = "Forget selected device",
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 32),
            Padding = new Padding(8, 6, 8, 6),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.Gainsboro
        };
        StyleButton(_forgetButton);
        _forgetButton.Click += (_, _) => ForgetSelected();

        var listContainer = new Panel { Dock = DockStyle.Fill };
        listContainer.Controls.Add(_trustedGrid);
        listContainer.Controls.Add(_forgetButton);

        root.Controls.Add(_statusLabel, 0, 0);
        root.Controls.Add(tailscaleRow, 0, 1);
        root.Controls.Add(addDeviceRow, 0, 2);
        root.Controls.Add(_pairingPanel, 0, 3);
        root.Controls.Add(trustedLabel, 0, 4);
        root.Controls.Add(listContainer, 0, 5);
        Controls.Add(root);

        RefreshTailscaleLabel();
        // Tailscale can connect/disconnect while this window sits hidden in the background
        // (it's never destroyed, just Hide()/Show()n - see OnFormClosing) - re-check every
        // time it's shown again. If Tailscale connects while the window is ALREADY open and
        // visible, the Refresh button next to the label covers that (manual, not polled).
        VisibleChanged += (_, _) => { if (Visible) RefreshTailscaleLabel(); };

        RefreshTrustedList();
        // One-time fit-to-content - exactly the "show it all fit to text at the start" the
        // columns' hardcoded widths above were only ever meant as a fallback for.
        // Deliberately NOT called here directly: AutoResizeColumns needs the grid to
        // already have a real window handle and a completed Dock layout pass to measure
        // text correctly - calling it inline in the constructor (before the Form has ever
        // been shown) silently produced wrong, too-narrow widths, confirmed by an
        // off-screen render showing "Permissic"/"Priori" still truncated despite this call
        // being right here. Hooking Shown, guarded by a flag so it only actually runs once
        // even if Shown ends up firing again on a later Hide()+Show() cycle, is what
        // guarantees a real handle exists first. Also deliberately NOT repeated on every
        // later RefreshTrustedList (device connects/disconnects, etc.) even after that one
        // run - once the user has dragged a column to their own preferred width, refitting
        // it out from under them on a background refresh would be exactly the
        // unpredictable-resize complaint this whole thing is fixing, just triggered a
        // different way.
        Shown += (_, _) =>
        {
            if (_columnsAutoFitted) return;
            _columnsAutoFitted = true;
            _trustedGrid.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.AllCells);
        };
        _pairing.DeviceApproved += _ => RefreshTrustedListThreadSafe();
        _pairing.DeviceForgotten += _ => RefreshTrustedListThreadSafe();
        // Fired by RecordConnected/RecordDisconnected/Rename (Pairing.cs) - any change to a
        // device's OWN record (not just being added/removed from the trusted list) needs
        // the same refresh so the Paired/Last Connected/Last Disconnected columns and the
        // Name column stay current.
        _pairing.DeviceUpdated += _ => RefreshTrustedListThreadSafe();
        _pairing.PairingOpened += code => RunOnUiThread(() => OnPairingOpened(code));
        _pairing.PairingClosed += () => RunOnUiThread(OnPairingClosed);
        _pairing.PairingAttemptStarted += model => RunOnUiThread(() => OnAttemptStarted(model));
        _pairing.PairingAttemptEnded += () => RunOnUiThread(OnAttemptEnded);
    }

    public void ShowConnected(string deviceId, string label, bool isRemote)
    {
        RunOnUiThread(() =>
        {
            _statusLabel.Text = $"Connected: {label} — via {(isRemote ? "Tailscale (remote)" : "LAN")}";
            _connectedDeviceId = deviceId;
            _connectedViaRemote = isRemote;
            RefreshTrustedList(); // so the newly-connected device's row gets highlighted
        });
    }

    public void ShowDisconnected()
    {
        RunOnUiThread(() =>
        {
            _statusLabel.Text = "No device connected";
            _connectedDeviceId = null;
            _connectedViaRemote = false;
            RefreshTrustedList(); // clears whichever row was highlighted
        });
    }

    /// Shared by both "Add" buttons. Closing (the button already open acts as Cancel) always
    /// just closes, regardless of which mode is currently showing. Opening while the OTHER
    /// button is showing isn't reachable - see the buttons' Enabled wiring in OnPairingOpened.
    private void TogglePairing(bool remote)
    {
        if (_pairing.OpenCode != null) { _pairing.ClosePairing(); return; }

        if (remote && TailscaleHelper.GetIPv4() == null)
        {
            MessageBox.Show(this,
                "Tailscale isn't connected on this PC right now - connect it first (see the Tailscale row above), then try again.",
                "Tailscale not detected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _pairingIsRemote = remote;
        _pairing.OpenPairing();
    }

    private void OnPairingOpened(string code)
    {
        _pairingModelLabel.Text = PairingInstructionText(_pairingIsRemote);
        _pairingCodeLabel.Text = code;
        _pairingQrBox.Image?.Dispose();
        // Resolved fresh at QR-build time rather than reusing the Tailscale-row label's
        // cached text - this is the exact bug being fixed: the QR/link this generates has
        // to embed whichever address actually matches the button that was clicked, or
        // scanning it silently connects over the wrong path regardless of intent.
        string host = _pairingIsRemote ? (TailscaleHelper.GetIPv4() ?? _localAddress) : _localAddress;
        _pairingQrBox.Image = GenerateQrImage(BuildPairUri(code, host));
        _pairingPanel.Visible = true;
        // Whichever button opened this becomes Cancel; the other is disabled outright
        // (not just left alone) so it can't start a second, conflicting pairing session -
        // only one code/QR can ever be open at a time (PairingCoordinator._openCode).
        _addDeviceButton.Text = _pairingIsRemote ? "+ Add New Device (LAN)" : "Cancel Pairing";
        _addDeviceButton.Enabled = !_pairingIsRemote;
        _addRemoteDeviceButton.Text = _pairingIsRemote ? "Cancel Pairing" : "+ Add Remote Device (Tailscale)";
        _addRemoteDeviceButton.Enabled = _pairingIsRemote;

        // Grow the window to fit the newly-revealed panel instead of letting it squeeze
        // the trusted-devices grid down with no way back except a manual resize (reported
        // bug) - the grid's TableLayoutPanel row is Percent(100), so it silently absorbs
        // whatever height the AutoSize rows above it need; this makes the WINDOW grow
        // instead. PerformLayout() first forces _pairingPanel's AutoSize height to reflect
        // the content just set, now that it's actually Visible - reading .Height before
        // that would see stale (often zero) layout from while it was hidden.
        PerformLayout();
        _pairingPanelHeightAdded = _pairingPanel.Height + _pairingPanel.Margin.Vertical;
        Height += _pairingPanelHeightAdded;

        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private void OnPairingClosed()
    {
        _pairingPanel.Visible = false;
        _addDeviceButton.Text = "+ Add New Device (LAN)";
        _addDeviceButton.Enabled = true;
        _addRemoteDeviceButton.Text = "+ Add Remote Device (Tailscale)";
        _addRemoteDeviceButton.Enabled = true;

        // Shrink back by the EXACT amount OnPairingOpened added, not a recomputed value -
        // see _pairingPanelHeightAdded's own doc comment for why.
        Height -= _pairingPanelHeightAdded;
        _pairingPanelHeightAdded = 0;
    }

    /// The pairing panel's instruction text, aware of which button opened it - this is
    /// what actually tells the user (before they scan anything) whether the code/QR they're
    /// about to use only works on this LAN or specifically requires Tailscale.
    private static string PairingInstructionText(bool remote) => remote
        ? "Scan this QR with the new/remote device's RemoteControl app (CONFIG tab → Scan QR to Connect), or type the code shown below. This pairing is for REMOTE (Tailscale) access - the device must already be on your tailnet to use it."
        : "Scan this QR with the new device's RemoteControl app (CONFIG tab → Scan QR to Connect), or type the code shown below. This pairing is for devices on this LAN - it won't work from outside your home network.";

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
        if (_pairing.OpenCode != null) _pairingModelLabel.Text = PairingInstructionText(_pairingIsRemote);
    }

    private void RefreshTailscaleLabel()
    {
        string? ip = TailscaleHelper.GetIPv4();
        _tailscaleLabel.Text = ip != null
            ? $"Tailscale: {ip}:{_port} - reachable from anywhere your tailnet reaches, not just this network"
            : "Tailscale: not detected (install/connect Tailscale to control this PC remotely)";
    }

    /// Matches the `remotecontrol://pair` intent-filter MainActivity registers on the
    /// Android side - scanning this (with the app installed) opens straight into it with
    /// host/port pre-filled and the code auto-submitted, no typing needed. `host` is
    /// whichever address actually matches the pairing mode - see OnPairingOpened - NOT
    /// always _localAddress; that was the bug: a QR meant for remote approval used to
    /// embed the LAN address regardless, so scanning it just connected over the LAN.
    private string BuildPairUri(string code, string host) =>
        $"remotecontrol://pair?host={Uri.EscapeDataString(host)}&port={_port}&code={code}";

    private static Image GenerateQrImage(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        byte[] png = new PngByteQRCode(data).GetGraphic(8);
        using var ms = new MemoryStream(png);
        return Image.FromStream(ms);
    }

    private void RefreshTrustedListThreadSafe() => RunOnUiThread(RefreshTrustedList);

    private const string RemoteRowNamePrefix = "    ↳ ";

    private void RefreshTrustedList()
    {
        // Remember the selected device (by id, not row index - rows get rebuilt below) so a
        // context-menu action or Forget click doesn't lose the user's selection just because
        // a DeviceUpdated/DeviceApproved event happened to refresh the grid around the same
        // time. Also remember WHICH of a device's (possibly two) rows was selected - both
        // map to the same DeviceId, so previouslySelectedId alone can't tell them apart, and
        // silently reselecting the wrong one would be a real annoyance if the remote sub-row
        // was selected (e.g. about to click Revoke) when an unrelated event refreshed the grid.
        string? previouslySelectedId = SelectedDevice()?.DeviceId;
        bool previouslySelectedWasRemoteRow =
            _trustedGrid.CurrentRow?.Cells[0].Value is string s && s.StartsWith(RemoteRowNamePrefix);

        var devices = _pairing.TrustedDevices;
        _listedDevices.Clear();
        _trustedGrid.Rows.Clear();

        foreach (var d in devices)
        {
            _listedDevices.Add(d);
            int rowIndex = _trustedGrid.Rows.Add(
                d.Name,
                d.PairedAt.ToString(DateFormat),
                d.LastConnectedAt?.ToString(DateFormat) ?? "-",
                d.LastDisconnectedAt?.ToString(DateFormat) ?? "-",
                d.ViewOnly ? "View Only" : "Full Control",
                d.Priority.ToString());

            if (d.DeviceId == _connectedDeviceId) HighlightRow(_trustedGrid.Rows[rowIndex]);
            if (d.DeviceId == previouslySelectedId && !previouslySelectedWasRemoteRow)
                _trustedGrid.Rows[rowIndex].Selected = true;

            // A user-visible ROW (not a column value) for remote access, per explicit
            // request - "so we know exactly which device[s] are connect[ed] [via] remote
            // pair[ing]" at a glance, rather than reading a column on the main row. Shows
            // when this device last connected specifically over Tailscale (distinct from
            // LastConnectedAt above, which updates for LAN reconnects too), and gets its
            // own "currently connected" highlight when the live session is actually remote.
            if (d.RemoteApproved)
            {
                _listedDevices.Add(d); // same underlying device - either row maps back to it
                int remoteRowIndex = _trustedGrid.Rows.Add(
                    RemoteRowNamePrefix + "Remote access (Tailscale)",
                    "-",
                    d.LastRemoteConnectedAt?.ToString(DateFormat) ?? "Never",
                    "-", "-", "-");
                var remoteRow = _trustedGrid.Rows[remoteRowIndex];
                remoteRow.DefaultCellStyle.ForeColor = Color.FromArgb(140, 148, 165);
                if (d.DeviceId == _connectedDeviceId && _connectedViaRemote) HighlightRow(remoteRow);
                if (d.DeviceId == previouslySelectedId && previouslySelectedWasRemoteRow)
                    _trustedGrid.Rows[remoteRowIndex].Selected = true;
            }
        }
    }

    private static void HighlightRow(DataGridViewRow row)
    {
        row.DefaultCellStyle.BackColor = Color.FromArgb(28, 58, 48);
        row.DefaultCellStyle.ForeColor = Color.FromArgb(150, 230, 190);
        row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(38, 74, 62);
        row.DefaultCellStyle.SelectionForeColor = Color.FromArgb(170, 245, 210);
    }

    /// The device the currently selected grid row corresponds to, or null if nothing's
    /// selected (or the grid is empty) - shared by Forget, the row context menu's three
    /// actions, and RefreshTrustedList's own selection-preserving logic.
    private TrustedDevice? SelectedDevice()
    {
        if (_trustedGrid.CurrentRow == null) return null;
        int index = _trustedGrid.CurrentRow.Index;
        return index >= 0 && index < _listedDevices.Count ? _listedDevices[index] : null;
    }

    private ContextMenuStrip BuildRowContextMenu()
    {
        var menu = new ContextMenuStrip();
        var disconnectItem = new ToolStripMenuItem("Disconnect");
        var permissionItem = new ToolStripMenuItem(); // text set fresh in Opening below - depends on the selected device's current ViewOnly state
        var revokeRemoteItem = new ToolStripMenuItem("Revoke Remote Access"); // visibility set fresh in Opening below - only relevant once a device HAS remote approval
        var priorityItem = new ToolStripMenuItem("Set Priority...");
        var renameItem = new ToolStripMenuItem("Rename...");
        var historyItem = new ToolStripMenuItem("View History...");
        menu.Items.Add(disconnectItem);
        menu.Items.Add(permissionItem);
        menu.Items.Add(revokeRemoteItem);
        menu.Items.Add(priorityItem);
        menu.Items.Add(renameItem);
        menu.Items.Add(historyItem);

        // Cancel the whole menu (rather than just disabling items) when right-clicking empty
        // space below the last row - CellMouseDown's e.RowIndex < 0 guard means nothing gets
        // selected in that case, so there's nothing for these actions to act on anyway.
        // Otherwise, relabel permissionItem for whichever row is actually selected - it's one
        // shared menu instance across every row, not a fresh one per row.
        menu.Opening += (_, e) =>
        {
            var device = SelectedDevice();
            if (device == null) { e.Cancel = true; return; }
            permissionItem.Text = device.ViewOnly ? "Set to Full Control" : "Set to View Only";
            // Nothing to revoke for a device that was only ever approved on the LAN -
            // hidden rather than disabled, so the menu doesn't show a dead item every time.
            revokeRemoteItem.Visible = device.RemoteApproved;
        };

        // Only makes sense for whichever row IS the live connection right now - Forget
        // already covers "remove a device that isn't currently connected".
        disconnectItem.Click += (_, _) =>
        {
            var device = SelectedDevice();
            if (device != null) _disconnectDevice(device.DeviceId);
        };

        permissionItem.Click += (_, _) =>
        {
            var device = SelectedDevice();
            if (device != null) _pairing.SetViewOnly(device.DeviceId, !device.ViewOnly);
        };

        revokeRemoteItem.Click += (_, _) =>
        {
            var device = SelectedDevice();
            if (device != null) _pairing.RevokeRemoteAccess(device.DeviceId);
        };

        priorityItem.Click += (_, _) =>
        {
            var device = SelectedDevice();
            if (device == null) return;

            // Loops instead of a single show/apply - if the chosen number is already taken
            // by another device, SetPriority refuses it (returns false) and this reopens
            // the dialog pre-filled with that same rejected number, rather than silently
            // discarding the attempt or applying a duplicate.
            int startingValue = device.Priority;
            while (true)
            {
                using var dialog = new PriorityDialog(startingValue);
                dialog.ShowDialog(this);
                if (!dialog.NewPriority.HasValue) return; // cancelled
                int chosen = dialog.NewPriority.Value;
                if (chosen == device.Priority) return; // unchanged

                if (_pairing.SetPriority(device.DeviceId, chosen)) return;

                var holder = _pairing.TrustedDevices.FirstOrDefault(d => d.Priority == chosen);
                MessageBox.Show(this,
                    $"Priority {chosen} is already used by \"{holder?.Name ?? "another device"}\" - pick a different number.",
                    "Priority already in use", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                startingValue = chosen;
            }
        };

        renameItem.Click += (_, _) =>
        {
            var device = SelectedDevice();
            if (device == null) return;
            using var dialog = new RenameDeviceDialog(device.Name);
            dialog.ShowDialog(this);
            if (dialog.NewName != null && dialog.NewName != device.Name)
            {
                _pairing.Rename(device.DeviceId, dialog.NewName);
            }
        };

        historyItem.Click += (_, _) =>
        {
            var device = SelectedDevice();
            if (device == null) return;
            using var dialog = new DeviceHistoryDialog(device);
            dialog.ShowDialog(this);
        };

        return menu;
    }

    private void ForgetSelected()
    {
        var device = SelectedDevice();
        if (device != null) _pairing.Forget(device.DeviceId);
    }

    /// FlatStyle.Flat buttons need every color set explicitly, or they fall back to
    /// WinForms' light-theme defaults - the near-invisible-text bug this fixes. Also adds
    /// a visible border and hover/press feedback so buttons don't look flat-out inert
    /// against the dark background. Internal (not private) so CloseConfirmDialog's buttons
    /// get the exact same treatment instead of duplicating it.
    internal static void StyleButton(Button button)
    {
        button.FlatAppearance.BorderColor = Color.FromArgb(70, 76, 92);
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 45, 58);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(50, 56, 72);
    }

    /// <ApplicationIcon> in the .csproj only embeds AppIcon.ico as the .exe FILE's own
    /// Win32 resource (what Explorer shows) - it does NOT make any WinForms Form use it as
    /// its own Icon property. Every Form defaults to the built-in .NET WinForms icon (the
    /// generic two-overlapping-squares glyph) unless something explicitly assigns Icon -
    /// which nothing here ever did, despite the real icon being embedded and previously
    /// verified present in the compiled binary. That verification only proved the resource
    /// EXISTS, not that any window actually uses it - this is the gap that left every title
    /// bar and the taskbar button showing the generic default instead (reported 2026-08-20).
    /// ExtractAssociatedIcon reads the icon back out of the running exe itself - same
    /// mechanism (and same icon) the earlier icon-embedding verification already confirmed
    /// works, so this can't drift from whatever AppIcon.ico actually built into. Shared by
    /// every Form in this app (set in each one's own constructor) rather than loaded once
    /// per-window, since Icon.ExtractAssociatedIcon is cheap and safe to call repeatedly on
    /// the same Application.ExecutablePath.
    internal static readonly Icon AppIcon =
        Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

    private void RunOnUiThread(Action action)
    {
        if (InvokeRequired)
        {
            BeginInvoke(action);
            return;
        }
        action();
    }

    /// Clicking X here used to just silently Hide() - technically correct (RemoteControl is
    /// a tray app, it was never going to actually quit) but gave the user no indication it
    /// was still running and controllable rather than genuinely closed. Now asks explicitly.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // A close triggered by ExitThread() itself (Application shutting down for real, e.g.
        // via the tray menu's Exit item or CloseConfirmDialog's own Exit choice below) must
        // not loop back into asking again or cancelling - only an interactive click on this
        // window's own X button should prompt.
        if (e.CloseReason != CloseReason.UserClosing)
        {
            base.OnFormClosing(e);
            return;
        }

        e.Cancel = true;
        using var confirm = new CloseConfirmDialog();
        confirm.ShowDialog(this);

        if (confirm.Result == CloseConfirmDialog.Choice.Exit)
        {
            _requestExit();
        }
        else
        {
            Hide(); // keep it alive so the tray menu can reopen it instantly
        }
        base.OnFormClosing(e);
    }
}
