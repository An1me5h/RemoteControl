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

                    if (packet.Value.Type == PacketType.Ping)
                    {
                        await writer.WriteLineAsync("{\"t\":\"PONG\"}");
                        continue;
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
            Interlocked.Decrement(ref _clientCount);
            ClientCountChanged?.Invoke(_clientCount);
        }
    }
}
