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

    public event Action<int>? ClientCountChanged;
    public event Action<Packet>? PacketReceived;
    public event Action<string>? UndecodableLineReceived;
    public event Action<int, bool>? HeldInputReleased;
    public event Action<string>? DeviceConnected;
    public event Action? DeviceDisconnected;
    public event Action<string>? ConnectionRejected;

    public Server(PairingCoordinator pairing)
    {
        _pairing = pairing;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener.Stop();
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
            _ = HandleClientAsync(client, token);
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

                var deviceLabel = await PerformHandshakeAsync(reader, writer, token);
                if (deviceLabel == null) return; // rejected/timed out - already reported via events

                approved = true;
                Interlocked.Increment(ref _clientCount);
                ClientCountChanged?.Invoke(_clientCount);
                DeviceConnected?.Invoke(deviceLabel);

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
    /// device window before it's trusted and saved. Returns a display label for the
    /// approved device, or null if the handshake was rejected/abandoned/timed out.
    private async Task<string?> PerformHandshakeAsync(StreamReader reader, StreamWriter writer, CancellationToken token)
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
            return label;
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
                    return label;
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
