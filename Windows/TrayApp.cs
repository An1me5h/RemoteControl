using System.Windows.Forms;

namespace RemoteControl;

/// No main window - just a tray icon. A hidden Form exists solely to give the
/// server's background-thread callbacks a UI-thread target to Invoke onto (for the
/// NotifyIcon updates - Console.WriteLine needs no such marshaling, it's thread-safe).
class TrayApp : ApplicationContext
{
    private readonly Form _hiddenWindow;
    private readonly NotifyIcon _icon;
    private readonly Server _server;
    private readonly ToolStripMenuItem _statusItem;
    private readonly string _localAddress;
    private readonly CancellationTokenSource _cts = new();

    public TrayApp()
    {
        _hiddenWindow = new Form { ShowInTaskbar = false, Opacity = 0, FormBorderStyle = FormBorderStyle.FixedToolWindow };
        _hiddenWindow.Load += (_, _) => _hiddenWindow.Hide();
        _hiddenWindow.Show();

        _localAddress = Discovery.GetLocalAddress() ?? "unknown address";
        Console.WriteLine($"RemoteControl listening on {_localAddress}:{Server.Port} (TCP) and UDP {Discovery.Port} (discovery)");
        Console.WriteLine("Waiting for a phone to connect...");

        _statusItem = new ToolStripMenuItem(StatusText(0)) { Enabled = false };
        var copyItem = new ToolStripMenuItem("Copy address", null, (_, _) =>
            Clipboard.SetText($"{_localAddress}:{Server.Port}"));
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitThread());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(copyItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = TrayIcons.Idle,
            Visible = true,
            Text = Truncate($"RemoteControl - {_localAddress}:{Server.Port} - no client"),
            ContextMenuStrip = menu
        };

        _server = new Server();
        _server.ClientCountChanged += OnClientCountChanged;
        _server.ClientCountChanged += count => Console.WriteLine(count > 0
            ? $"[{Now()}] Client connected. {count} connected."
            : $"[{Now()}] Client disconnected. {count} connected.");
        _server.PacketReceived += p => Console.WriteLine($"[{Now()}] {PacketFormat.Describe(p)}");
        _server.UndecodableLineReceived += line => Console.WriteLine($"[{Now()}] ?? unrecognized: {line}");
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
        _hiddenWindow.Close();
        base.ExitThreadCore();
    }
}
