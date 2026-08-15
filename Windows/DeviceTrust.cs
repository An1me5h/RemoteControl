using System.Text.Json;

namespace RemoteControl;

/// A phone that's completed pairing at least once. Identity is the triple (DeviceId,
/// Model, Build) - DeviceId alone (Android's ANDROID_ID) would already be enough in
/// practice, but checking all three is what the user asked for ("check if everything
/// matches") and costs nothing extra.
record TrustedDevice(string DeviceId, string Model, string Build, string Name, DateTime PairedAt);

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
