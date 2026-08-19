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
/// Priority: HIGHER number wins. A connecting device with a higher priority than whoever's
/// currently connected preempts them (kicks the lower-priority session, takes the slot) -
/// see PairingCoordinator.TryClaimOrPreempt. Default 0 for every device, so priority is
/// opt-in: nothing preempts anything until the user actually raises one device above others.
record TrustedDevice(
    string DeviceId, string Model, string Build, string Name, DateTime PairedAt,
    DateTime? LastConnectedAt = null, DateTime? LastDisconnectedAt = null,
    List<DeviceHistoryEntry>? History = null, bool ViewOnly = false, int Priority = 0)
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
