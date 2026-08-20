using System.Text.Json;

namespace RemoteControl;

/// One entry in a trusted device's event log - shown via DeviceWindow's right-click
/// "View History". EventText is plain, human-readable ("Connected", "Disconnected",
/// "Renamed from 'Old' to 'New'") rather than a structured enum + args, since it's only
/// ever displayed, never parsed back.
record DeviceHistoryEntry(DateTime At, string EventText);

/// A phone that's completed pairing at least once. Identity is the triple (DeviceId,
/// Model, Build) - DeviceId alone (Android's ANDROID_ID) would already be enough in
/// practice, but checking all three is what the user asked for ("check if everything
/// matches") and costs nothing extra.
///
/// LastConnectedAt/LastDisconnectedAt/History are all nullable/optional so an OLD
/// trusted_devices.json (saved before this feature existed) still deserializes cleanly -
/// System.Text.Json falls back to these defaults for properties missing from older JSON.
/// Use the History property (never the raw field) when reading, so callers never have to
/// null-check.
/// ViewOnly: can watch the screen stream but every input packet is dropped server-side
/// (Server.cs) instead of reaching InputInjector - checked fresh per packet, so toggling it
/// mid-session takes effect immediately, no reconnect needed.
/// Priority: LOWER number wins - 1 is the best/highest priority a device can have
/// (PriorityDialog's picker won't go below 1). A connecting device with a lower priority
/// number than whoever's currently connected preempts them (kicks the weaker session,
/// takes the slot) - see PairingCoordinator.TryClaimOrPreempt. Default 0 for every device
/// means "no priority set", not "priority zero" - it's treated as weaker than every
/// explicitly-numbered device, so priority stays opt-in: nothing preempts anything until
/// the user actually assigns a real number to at least one device.
/// RemoteApproved: being trusted on the LAN does NOT automatically trust this device from
/// off-LAN (e.g. over Tailscale) - Server.cs's handshake still requires a fresh
/// pairing-code approval the first time a trusted device connects from a remote address,
/// same opt-in-with-a-code flow a brand-new device goes through. Once approved this way it
/// stays approved (until explicitly revoked from DeviceWindow), so this is a one-time step
/// per device, not per remote session. Default false so an OLD trusted_devices.json (saved
/// before this existed) doesn't retroactively grant remote access to devices that were
/// only ever approved over the LAN.
/// LastRemoteConnectedAt: separate from LastConnectedAt (which updates on EVERY connection,
/// LAN or remote) - this one only updates when a connection specifically arrived over
/// Tailscale, so DeviceWindow can show "last connected remotely" on its own synthetic row
/// for the device instead of just a capability flag.
record TrustedDevice(
    string DeviceId, string Model, string Build, string Name, DateTime PairedAt,
    DateTime? LastConnectedAt = null, DateTime? LastDisconnectedAt = null,
    List<DeviceHistoryEntry>? History = null, bool ViewOnly = false, int Priority = 0,
    bool RemoteApproved = false, DateTime? LastRemoteConnectedAt = null)
{
    public List<DeviceHistoryEntry> History { get; init; } = History ?? new List<DeviceHistoryEntry>();
}

/// Persists trusted devices as a JSON array in %AppData%\RemoteControl\trusted_devices.json
/// - not next to the exe, since that can be read-only (e.g. under Program Files).
static class DeviceTrustStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RemoteControl", "trusted_devices.json");

    public static List<TrustedDevice> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<TrustedDevice>();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<TrustedDevice>>(json) ?? new List<TrustedDevice>();
        }
        catch (Exception)
        {
            return new List<TrustedDevice>();
        }
    }

    public static void Save(List<TrustedDevice> devices)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(devices));
    }
}
