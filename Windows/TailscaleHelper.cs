using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteControl;

/// Finds this machine's Tailscale IPv4 address (100.x.y.z) by looking for the virtual
/// adapter the Tailscale client installs, rather than shelling out to the `tailscale`
/// CLI - works even if the CLI isn't on PATH, and returns null cleanly if Tailscale
/// isn't installed or isn't currently connected.
static class TailscaleHelper
{
    public static string? GetIPv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            bool isTailscale =
                nic.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
                nic.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase);
            if (!isTailscale) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                // The adapter shows OperationalStatus.Up as soon as its driver loads, even
                // while the Tailscale service itself is stopped/logged out - at that point
                // Windows has only handed it a leftover link-local (169.254.x.x) address,
                // which isn't reachable from anywhere else and isn't a real tailnet IP.
                // Tailscale's actual IPv4 range is the CGNAT block 100.64.0.0/10
                // (100.64.0.0-100.127.255.255) - checking against that instead of just
                // "any IPv4 address on this adapter" is what actually proves Tailscale is
                // connected, not just installed.
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    IsTailscaleRange(addr.Address.GetAddressBytes()))
                    return addr.Address.ToString();
            }
        }
        return null;
    }

    private static bool IsTailscaleRange(byte[] v4) => v4[0] == 100 && v4[1] >= 64 && v4[1] <= 127;
}
