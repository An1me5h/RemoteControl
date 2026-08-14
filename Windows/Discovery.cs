using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RemoteControl;

/// UDP broadcast discovery so the phone doesn't need a manually-typed IP.
/// Uses its own port and message format - not real mDNS/Bonjour, so it never
/// touches the 224.0.0.251:5353 multicast group real Bonjour responders use.
static class Discovery
{
    public const int Port = 58201;
    private const string DiscoverMessage = "RC_DISCOVER";

    public static void StartResponder(int tcpPort, CancellationToken token)
        => _ = Task.Run(() => ResponderLoopAsync(tcpPort, token), token);

    private static async Task ResponderLoopAsync(int tcpPort, CancellationToken token)
    {
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));

        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(token);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            if (Encoding.UTF8.GetString(result.Buffer) != DiscoverMessage) continue;

            string? localIp = GetLocalAddress();
            if (localIp is null) continue;

            byte[] reply = Encoding.UTF8.GetBytes($"RC_HOST {localIp} {tcpPort}");
            await udp.SendAsync(reply, result.RemoteEndPoint, token);
        }
    }

    /// Picks the NIC actually used to reach the internet (real Wi-Fi adapter, not a
    /// Hyper-V/WSL/VPN virtual one) by opening a UDP "connection" and reading back the
    /// local endpoint it bound to. No packet is actually sent for a UDP connect.
    public static string? GetLocalAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 53);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
