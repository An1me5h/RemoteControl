using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RemoteControl;

/// Newline-delimited JSON over TCP, phone -> PC. One thread per connected client, but only
/// one client is ever actually let through the handshake at a time - see PairingCoordinator.
class Server
{
    public const int Port = 5201;

    private readonly TcpListener _listener = new(IPAddress.Any, Port);
    private readonly PairingCoordinator _pairing;
    private CancellationTokenSource? _cts;
    private int _clientCount;

    // The most recently started connection handler - tracked so Stop() can actually wait
    // for its cleanup (see below) instead of just firing cancellation and returning.
    private volatile Task? _activeHandlerTask;

    // The single currently-approved connection, if any - only one can ever exist thanks to
    // PairingCoordinator's slot. Tracked so Forget can immediately sever a live session for
    // a device that was just revoked, not just block its *next* reconnect attempt.
    private volatile TcpClient? _connectedClient;
    private volatile string? _connectedDeviceId;

    // Exposed so ScreenStreamer can refuse to stream to anyone until a device has actually
    // paired and connected through the real input protocol - screen visibility should follow
    // the same "only the one approved device" rule the rest of this app already enforces,
    // not be reachable by anything on the LAN that happens to know the PC's IP and port.
    public bool HasActiveConnection => _connectedClient != null;

    public event Action<int>? ClientCountChanged;
    public event Action<Packet>? PacketReceived;
    public event Action<string>? UndecodableLineReceived;
    public event Action<int, bool>? HeldInputReleased;
    public event Action<string, string>? DeviceConnected; // (deviceId, label)
    public event Action? DeviceDisconnected;
    public event Action<string>? ConnectionRejected;

    public Server(PairingCoordinator pairing)
    {
        _pairing = pairing;
        _pairing.DeviceForgotten += device =>
        {
            if (device.DeviceId == _connectedDeviceId) KickConnectedClient();
        };
    }

    /// DeviceWindow's right-click "Disconnect" - kicks the live session WITHOUT revoking
    /// trust (unlike Forget), so the device can just reconnect normally afterward. A no-op
    /// if deviceId isn't the one actually connected right now (e.g. the row was stale by
    /// the time the click landed) - safe to call without re-checking first.
    public void DisconnectDevice(string deviceId)
    {
        if (deviceId == _connectedDeviceId) KickConnectedClient();
    }

    /// Forcibly closes the current live connection (if any) - called when the device
    /// that's currently connected gets Forgotten. Closing the socket from here makes the
    /// blocked `ReadLineAsync` in HandleClientAsync's dispatch loop throw/return
    /// immediately, which drives it through the exact same cleanup path (release held
    /// input, decrement count, fire DeviceDisconnected, release the pairing slot) as any
    /// other disconnect - no separate cleanup logic needed.
    private void KickConnectedClient()
    {
        try { _connectedClient?.Close(); } catch (Exception) { /* already closing/closed */ }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    /// Cancelling the token below makes a blocked `ReadLineAsync` in HandleClientAsync's
    /// dispatch loop throw, which unwinds through its `finally` block and releases any held
    /// key/mouse-button state via real Win32 SendInput calls (ReleaseHeldInput) - but that
    /// cleanup runs on the handler's OWN async continuation, not synchronously here.
    /// Without waiting for it, a caller that tears the whole process down right after Stop()
    /// returns (TrayApp.ExitThreadCore does exactly this) can race ahead of that
    /// continuation and exit the process before the key-up ever actually gets sent - nothing
    /// is left running afterward to send it, so it stays held in Windows' real, global
    /// keyboard state indefinitely. Bounded wait so a stuck/slow connection can never hang
    /// app exit; 2s is generous for a SendInput call that normally completes in microseconds.
    public void Stop()
    {
        _cts?.Cancel();
        _listener.Stop();
        try { _activeHandlerTask?.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { /* already faulted/cancelled - fine, we only cared that it ran */ }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token);
            }
            catch (Exception)
            {
                break;
            }
            _activeHandlerTask = HandleClientAsync(client, token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        client.NoDelay = true;

        bool slotClaimed = false;
        bool approved = false;
        var heldVks = new HashSet<int>();
        bool leftButtonDown = false;

        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            // Encoding.UTF8 (not new UTF8Encoding(false)) writes a BOM before the first
            // byte of each new connection - harmless to `line.contains("PONG")` but breaks
            // real JSON parsing (JSONObject(line)) on the handshake replies the Android
            // side needs to actually parse, so it has to be the BOM-less encoding here.
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
            {
                if (!_pairing.TryClaimSlot())
                {
                    await writer.WriteLineAsync("{\"t\":\"REJECTED\",\"reason\":\"busy\"}");
                    ConnectionRejected?.Invoke("busy (another device is already connected)");
                    return;
                }
                slotClaimed = true;

                var handshakeResult = await PerformHandshakeAsync(reader, writer, token);
                if (handshakeResult == null) return; // rejected/timed out - already reported via events
                var (deviceLabel, deviceId) = handshakeResult.Value;

                approved = true;
                _connectedClient = client;
                _connectedDeviceId = deviceId;
                Interlocked.Increment(ref _clientCount);
                ClientCountChanged?.Invoke(_clientCount);
                DeviceConnected?.Invoke(deviceId, deviceLabel);
                // Covers every successful handshake, not just a first-time pairing (Approve
                // already logs its own "Paired" entry for that case) - an ordinary reconnect
                // of an already-trusted device needs its LastConnectedAt/history updated too.
                _pairing.RecordConnected(deviceId);

                while (!token.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(token);
                    if (line == null) break;

                    var packet = PacketCodec.Decode(line);
                    if (packet is null)
                    {
                        UndecodableLineReceived?.Invoke(line);
                        continue;
                    }

                    PacketReceived?.Invoke(packet.Value);

                    switch (packet.Value.Type)
                    {
                        case PacketType.Ping:
                            await writer.WriteLineAsync("{\"t\":\"PONG\"}");
                            continue;
                        case PacketType.VkDown:
                            heldVks.Add(packet.Value.K);
                            break;
                        case PacketType.VkUp:
                            heldVks.Remove(packet.Value.K);
                            break;
                        case PacketType.LDown:
                            leftButtonDown = true;
                            break;
                        case PacketType.LUp:
                            leftButtonDown = false;
                            break;
                    }

                    InputInjector.Dispatch(packet.Value);
                }
            }
        }
        catch (Exception)
        {
            // Client dropped or network error - nothing to do but stop handling it.
        }
        finally
        {
            ReleaseHeldInput(heldVks, leftButtonDown);
            if (approved)
            {
                // Read before clearing - RecordDisconnected needs the id, and this is the
                // last point it's still available.
                if (_connectedDeviceId != null) _pairing.RecordDisconnected(_connectedDeviceId);
                _connectedClient = null;
                _connectedDeviceId = null;
                Interlocked.Decrement(ref _clientCount);
                ClientCountChanged?.Invoke(_clientCount);
                DeviceDisconnected?.Invoke();
            }
            if (slotClaimed) _pairing.ReleaseSlot();
        }
    }

    /// Every connection goes through this before a single input packet is ever dispatched.
    /// Expects a HELLO first; a recognized (DeviceId+Model+Build all matching a saved
    /// entry) device is welcomed immediately, silently, so normal reconnects stay seamless.
    /// An unrecognized device has to prove it knows a one-time code shown in the PC's
    /// device window before it's trusted and saved. Returns a display label and the
    /// device's id for the approved device, or null if the handshake was
    /// rejected/abandoned/timed out.
    private async Task<(string Label, string DeviceId)?> PerformHandshakeAsync(StreamReader reader, StreamWriter writer, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(PairingCoordinator.HandshakeTimeoutSeconds));

        string? line;
        try { line = await reader.ReadLineAsync(cts.Token); }
        catch (OperationCanceledException) { ConnectionRejected?.Invoke("handshake timed out waiting for HELLO"); return null; }
        if (line == null) return null;

        var packet = PacketCodec.Decode(line);
        if (packet is not { Type: PacketType.Hello, DeviceId: not null, Model: not null, Build: not null })
        {
            await writer.WriteLineAsync("{\"t\":\"REJECTED\",\"reason\":\"bad_hello\"}");
            ConnectionRejected?.Invoke("sent something other than a valid HELLO first");
            return null;
        }

        var hello = packet.Value;
        string label = $"{hello.Name ?? hello.Model} ({hello.Model})";

        if (_pairing.FindTrusted(hello.DeviceId!, hello.Model!, hello.Build!) != null)
        {
            await writer.WriteLineAsync("{\"t\":\"WELCOME\"}");
            return (label, hello.DeviceId!);
        }

        // Not a recognized device - only allowed in if the user has explicitly opened
        // pairing mode (DeviceWindow's "Add New Device" button). No open code means no
        // unrecognized device gets past HELLO, full stop - this is what makes pairing
        // opt-in rather than any stranger on the LAN being able to try guessing a code
        // whenever they feel like it.
        string? code = _pairing.OpenCode;
        if (code == null)
        {
            await writer.WriteLineAsync("{\"t\":\"REJECTED\",\"reason\":\"pairing_closed\"}");
            ConnectionRejected?.Invoke($"unrecognized device rejected - pairing isn't open ({label})");
            return null;
        }

        _pairing.NotifyAttemptStarted(hello.Model!);
        await writer.WriteLineAsync("{\"t\":\"PAIRREQUIRED\"}");

        try
        {
            for (int attempt = 1; attempt <= PairingCoordinator.MaxCodeAttempts; attempt++)
            {
                string? codeLine;
                try { codeLine = await reader.ReadLineAsync(cts.Token); }
                catch (OperationCanceledException) { ConnectionRejected?.Invoke("pairing timed out"); return null; }
                if (codeLine == null) return null;

                var codePacket = PacketCodec.Decode(codeLine);
                if (codePacket is { Type: PacketType.PairCode } && codePacket.Value.Code == _pairing.OpenCode)
                {
                    _pairing.Approve(hello.DeviceId!, hello.Model!, hello.Build!, hello.Name ?? hello.Model!);
                    await writer.WriteLineAsync("{\"t\":\"WELCOME\"}");
                    return (label, hello.DeviceId!);
                }

                int attemptsLeft = PairingCoordinator.MaxCodeAttempts - attempt;
                if (attemptsLeft > 0)
                {
                    await writer.WriteLineAsync($"{{\"t\":\"WRONGCODE\",\"attemptsLeft\":{attemptsLeft}}}");
                }
            }

            await writer.WriteLineAsync("{\"t\":\"REJECTED\",\"reason\":\"wrong_code\"}");
            ConnectionRejected?.Invoke($"wrong pairing code entered {PairingCoordinator.MaxCodeAttempts} times ({label})");
            return null;
        }
        finally
        {
            _pairing.NotifyAttemptEnded();
        }
    }

    /// Releases whatever this client left held when it disconnected. This is the only
    /// reliable place to do it: once the connection is gone, there is no way for the phone
    /// to send VKUP/LUP anymore, so anything still down (e.g. a held Win key from the
    /// on-screen keyboard's hold mode) would otherwise stay stuck in Windows' real
    /// keyboard/mouse state indefinitely - corrupting every subsequent keystroke typed on
    /// the PC's own keyboard, not just input from this app.
    private void ReleaseHeldInput(HashSet<int> heldVks, bool leftButtonDown)
    {
        if (heldVks.Count == 0 && !leftButtonDown) return;

        foreach (var vk in heldVks) InputInjector.KeyState((ushort)vk, false);
        if (leftButtonDown) InputInjector.ButtonState(MouseButtonKind.Left, false);

        HeldInputReleased?.Invoke(heldVks.Count, leftButtonDown);
    }
}
