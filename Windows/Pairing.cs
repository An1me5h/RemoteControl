using System.Security.Cryptography;

namespace RemoteControl;

/// Owns trust state and the "only one device at a time" rule. A single mutable slot -
/// claimed the moment a TCP connection starts its handshake (before we even know if it'll
/// turn out to be a trusted device or need pairing) and released when that connection
/// ends - is what enforces "only one device can control it at a time": a second
/// connection attempt while the slot is held gets rejected immediately, whether it's an
/// unrecognized device or the very same one reconnecting.
class PairingCoordinator
{
    public const int MaxCodeAttempts = 5;

    /// Covers the *entire* handshake from HELLO to the last code attempt, not per-message -
    /// has to be generous enough for a real person to notice the pairing window, read a
    /// 6-digit code off it, and type it into the phone, not just round-trip a packet.
    public const int HandshakeTimeoutSeconds = 120;

    private readonly object _lock = new();
    private readonly List<TrustedDevice> _trusted;
    private bool _slotClaimed;

    /// (code, requesting device's model name)
    public event Action<string, string>? PairingCodeGenerated;
    public event Action? PairingEnded;
    public event Action<TrustedDevice>? DeviceApproved;
    public event Action<TrustedDevice>? DeviceForgotten;

    public PairingCoordinator()
    {
        _trusted = DeviceTrustStore.Load();
    }

    public IReadOnlyList<TrustedDevice> TrustedDevices
    {
        get { lock (_lock) return _trusted.ToList(); }
    }

    public bool TryClaimSlot()
    {
        lock (_lock)
        {
            if (_slotClaimed) return false;
            _slotClaimed = true;
            return true;
        }
    }

    public void ReleaseSlot()
    {
        lock (_lock) { _slotClaimed = false; }
    }

    public TrustedDevice? FindTrusted(string deviceId, string model, string build)
    {
        lock (_lock)
        {
            return _trusted.FirstOrDefault(d =>
                d.DeviceId == deviceId && d.Model == model && d.Build == build);
        }
    }

    /// 6-digit code, cryptographically random (not System.Random) - this is the one thing
    /// standing between "any device on the LAN" and "only devices someone deliberately
    /// approved," so it shouldn't be guessable from a predictable seed.
    public string GenerateCode()
    {
        var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();
        return code;
    }

    public void NotifyPairingStarted(string code, string model) => PairingCodeGenerated?.Invoke(code, model);

    public void NotifyPairingEnded() => PairingEnded?.Invoke();

    public void Approve(string deviceId, string model, string build, string name)
    {
        TrustedDevice device;
        lock (_lock)
        {
            _trusted.RemoveAll(d => d.DeviceId == deviceId);
            device = new TrustedDevice(deviceId, model, build, name, DateTime.Now);
            _trusted.Add(device);
            DeviceTrustStore.Save(_trusted);
        }
        DeviceApproved?.Invoke(device);
    }

    public void Forget(string deviceId)
    {
        TrustedDevice? removed;
        lock (_lock)
        {
            removed = _trusted.FirstOrDefault(d => d.DeviceId == deviceId);
            if (removed != null)
            {
                _trusted.Remove(removed);
                DeviceTrustStore.Save(_trusted);
            }
        }
        if (removed != null) DeviceForgotten?.Invoke(removed);
    }
}
