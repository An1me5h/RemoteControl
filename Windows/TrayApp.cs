using System.Windows.Forms;

namespace RemoteControl;

/// No main window on the tray icon itself - a hidden Form exists solely to give the
/// server's background-thread callbacks a UI-thread target to Invoke onto (for the
/// NotifyIcon updates - Console.WriteLine needs no such marshaling, it's thread-safe).
/// DeviceWindow is a real, visible window, opened from the tray menu or auto-shown when
/// a new device needs a pairing code.
class TrayApp : ApplicationContext
{
    private readonly Form _hiddenWindow;
    private readonly NotifyIcon _icon;
    private readonly PairingCoordinator _pairing;
    private readonly Server _server;
    private readonly DeviceWindow _deviceWindow;
    private readonly ToolStripMenuItem _statusItem;
    private readonly string _localAddress;
    private readonly CancellationTokenSource _cts = new();

    /// [foreground] is true when this process reattached to a launching terminal's console
    /// (see Program.cs) - i.e. it was started from `dotnet run` or a shell, not by
    /// double-clicking the exe or a Startup-folder/Task Scheduler entry. Only then do we
    /// also pop the Devices window automatically; a true background start stays tray-only
    /// until the user opens it themselves.
    public TrayApp(bool foreground)
    {
        _hiddenWindow = new Form { ShowInTaskbar = false, Opacity = 0, FormBorderStyle = FormBorderStyle.FixedToolWindow };
        _hiddenWindow.Load += (_, _) => _hiddenWindow.Hide();
        _hiddenWindow.Show();

        _localAddress = Discovery.GetLocalAddress() ?? "unknown address";
        Console.WriteLine($"RemoteControl listening on {_localAddress}:{Server.Port} (TCP) and UDP {Discovery.Port} (discovery)");
        Console.WriteLine("Waiting for a phone to connect...");

        _pairing = new PairingCoordinator();
        _deviceWindow = new DeviceWindow(_pairing, _localAddress, Server.Port);
        if (foreground) _deviceWindow.Show();

        _statusItem = new ToolStripMenuItem(StatusText(0)) { Enabled = false };
        var copyItem = new ToolStripMenuItem("Copy address", null, (_, _) =>
            Clipboard.SetText($"{_localAddress}:{Server.Port}"));
        var devicesItem = new ToolStripMenuItem("Devices...", null, (_, _) =>
        {
            _deviceWindow.Show();
            _deviceWindow.WindowState = FormWindowState.Normal;
            _deviceWindow.BringToFront();
        });
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitThread());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(copyItem);
        menu.Items.Add(devicesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = TrayIcons.Idle,
            Visible = true,
            Text = Truncate($"RemoteControl - {_localAddress}:{Server.Port} - no client"),
            ContextMenuStrip = menu
        };

        _server = new Server(_pairing);
        _server.ClientCountChanged += OnClientCountChanged;
        _server.DeviceConnected += label =>
        {
            _deviceWindow.ShowConnected(label);
            Console.WriteLine($"[{Now()}] Connected: {label}");
        };
        _server.DeviceDisconnected += () =>
        {
            _deviceWindow.ShowDisconnected();
            Console.WriteLine($"[{Now()}] Device disconnected.");
        };
        _server.ConnectionRejected += reason => Console.WriteLine($"[{Now()}] Connection rejected: {reason}");
        _server.PacketReceived += p => Console.WriteLine($"[{Now()}] {PacketFormat.Describe(p)}");
        _server.UndecodableLineReceived += line => Console.WriteLine($"[{Now()}] ?? unrecognized: {line}");
        _server.HeldInputReleased += (vkCount, mouseReleased) => Console.WriteLine(
            $"[{Now()}] Client disconnected while holding input - released {vkCount} key(s)" +
            (mouseReleased ? " and the left mouse button." : "."));

        _pairing.PairingOpened += code => Console.WriteLine($"[{Now()}] Pairing opened - code: {code}");
        _pairing.PairingClosed += () => Console.WriteLine($"[{Now()}] Pairing closed.");
        _pairing.PairingAttemptStarted += model => Console.WriteLine($"[{Now()}] {model} is attempting to pair...");
        _pairing.PairingAttemptEnded += () => Console.WriteLine($"[{Now()}] Pairing attempt ended.");
        _pairing.DeviceApproved += device => Console.WriteLine($"[{Now()}] Paired and trusted: {device.Name}");
        _pairing.DeviceForgotten += device => Console.WriteLine($"[{Now()}] Forgot device: {device.Name}");

        _server.Start();

        Discovery.StartResponder(Server.Port, _cts.Token);
    }

    private static string Now() => DateTime.Now.ToString("HH:mm:ss.fff");

    private void OnClientCountChanged(int count)
    {
        if (_hiddenWindow.InvokeRequired)
        {
            _hiddenWindow.Invoke(() => OnClientCountChanged(count));
            return;
        }

        _icon.Icon = count > 0 ? TrayIcons.Connected : TrayIcons.Idle;
        _statusItem.Text = StatusText(count);
        _icon.Text = Truncate(count > 0
            ? $"RemoteControl - {_localAddress}:{Server.Port} - {count} connected"
            : $"RemoteControl - {_localAddress}:{Server.Port} - no client");
    }

    private string StatusText(int count) => count > 0
        ? $"{_localAddress}:{Server.Port} - {count} connected"
        : $"{_localAddress}:{Server.Port} - waiting for connection";

    // NotifyIcon.Text is capped at 63 characters by the Win32 shell notify API.
    private static string Truncate(string s) => s.Length <= 63 ? s : s[..63];

    protected override void ExitThreadCore()
    {
        _cts.Cancel();
        _server.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _deviceWindow.Dispose();
        _hiddenWindow.Close();
        base.ExitThreadCore();
    }
}
