using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RemoteControl;

/// Newline-delimited JSON over TCP, phone -> PC. One thread per connected client.
class Server
{
    public const int Port = 5201;

    private readonly TcpListener _listener = new(IPAddress.Any, Port);
    private CancellationTokenSource? _cts;
    private int _clientCount;

    public event Action<int>? ClientCountChanged;
    public event Action<Packet>? PacketReceived;
    public event Action<string>? UndecodableLineReceived;
    public event Action<int, bool>? HeldInputReleased;

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
        Interlocked.Increment(ref _clientCount);
        ClientCountChanged?.Invoke(_clientCount);

        // Tracks input this client left "down" (a held key, a mouse button mid-drag) so it
        // can be released the moment the connection ends, no matter why. See ReleaseHeldInput.
        var heldVks = new HashSet<int>();
        bool leftButtonDown = false;

        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true, NewLine = "\n" })
            {
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
            Interlocked.Decrement(ref _clientCount);
            ClientCountChanged?.Invoke(_clientCount);
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
