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
    private readonly Button _addDeviceButton;
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

    // Guards the one-time column auto-fit (see Shown handler in the constructor) against
    // running more than once, in case Shown ends up firing again on a later Hide()+Show()
    // cycle - only the very first real layout should decide the columns' widths.
    private bool _columnsAutoFitted;

    private const string DateFormat = "yyyy-MM-dd HH:mm";

    private const string DefaultPairingText =
        "Scan this QR with the new device's RemoteControl app (CONFIG tab → Scan QR to Connect), or type the code shown below:";

    public DeviceWindow(PairingCoordinator pairing, string localAddress, int port,
                        Action requestExit, Action<string> disconnectDevice)
    {
        _pairing = pairing;
        _localAddress = localAddress;
        _port = port;
        _requestExit = requestExit;
        _disconnectDevice = disconnectDevice;

        Text = "RemoteControl - Devices";
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
            // Unlike trustedLabel/_pairingModelLabel (already fixed, see the 2026-08-19
            // text-overflow entry), this one had NO width cap at all - "Connected: " plus a
            // long device model/name (some Android model strings run long) could extend
            // straight past the window's edge instead of wrapping. Matches the same 440
            // budget the other labels already wrap against.
            MaximumSize = new Size(440, 0),
            Margin = new Padding(0, 0, 0, 12)
        };

        _addDeviceButton = new Button
        {
            Text = "+ Add New Device",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(200, 0),
            Padding = new Padding(8, 6, 8, 6),
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
        root.Controls.Add(_addDeviceButton, 0, 1);
        root.Controls.Add(_pairingPanel, 0, 2);
        root.Controls.Add(trustedLabel, 0, 3);
        root.Controls.Add(listContainer, 0, 4);
        Controls.Add(root);

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

    public void ShowConnected(string deviceId, string label)
    {
        RunOnUiThread(() =>
        {
            _statusLabel.Text = $"Connected: {label}";
            _connectedDeviceId = deviceId;
            RefreshTrustedList(); // so the newly-connected device's row gets highlighted
        });
    }

    public void ShowDisconnected()
    {
        RunOnUiThread(() =>
        {
            _statusLabel.Text = "No device connected";
            _connectedDeviceId = null;
            RefreshTrustedList(); // clears whichever row was highlighted
        });
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
        // Remember the selected device (by id, not row index - rows get rebuilt below) so a
        // context-menu action or Forget click doesn't lose the user's selection just because
        // a DeviceUpdated/DeviceApproved event happened to refresh the grid around the same time.
        string? previouslySelectedId = SelectedDevice()?.DeviceId;

        _listedDevices.Clear();
        _listedDevices.AddRange(_pairing.TrustedDevices);
        _trustedGrid.Rows.Clear();

        foreach (var d in _listedDevices)
        {
            int rowIndex = _trustedGrid.Rows.Add(
                d.Name,
                d.PairedAt.ToString(DateFormat),
                d.LastConnectedAt?.ToString(DateFormat) ?? "-",
                d.LastDisconnectedAt?.ToString(DateFormat) ?? "-",
                d.ViewOnly ? "View Only" : "Full Control",
                d.Priority.ToString());

            if (d.DeviceId == _connectedDeviceId)
            {
                var row = _trustedGrid.Rows[rowIndex];
                row.DefaultCellStyle.BackColor = Color.FromArgb(28, 58, 48);
                row.DefaultCellStyle.ForeColor = Color.FromArgb(150, 230, 190);
                row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(38, 74, 62);
                row.DefaultCellStyle.SelectionForeColor = Color.FromArgb(170, 245, 210);
            }

            if (d.DeviceId == previouslySelectedId) _trustedGrid.Rows[rowIndex].Selected = true;
        }
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
        var priorityItem = new ToolStripMenuItem("Set Priority...");
        var renameItem = new ToolStripMenuItem("Rename...");
        var historyItem = new ToolStripMenuItem("View History...");
        menu.Items.Add(disconnectItem);
        menu.Items.Add(permissionItem);
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
