using System.Security.Cryptography;

namespace RemoteControl;

/// Owns trust state, the "only one device at a time" rule, and pairing-mode state. A
/// single mutable slot - claimed the moment a TCP connection starts its handshake (before
/// we even know if it'll turn out to be a trusted device or need pairing) and released
/// when that connection ends - is what enforces "only one device can control it at a
/// time": a second connection attempt while the slot is held gets rejected immediately,
/// whether it's an unrecognized device or the very same one reconnecting.
///
/// Pairing itself is opt-in, not always-on: an unrecognized device is rejected outright
/// unless the user has explicitly opened pairing mode (DeviceWindow's "Add New Device"
/// button), which is what generates the code/QR shown on screen. This is deliberately a
/// standing code - generated once when pairing opens, valid until someone successfully
/// uses it or the user cancels - rather than a fresh one minted mid-handshake, so the QR
/// is actually there to be scanned *before* anyone tries to connect, not only reactively
/// after.
class PairingCoordinator
{
    public const int MaxCodeAttempts = 5;

    /// Bounds a single handshake attempt (HELLO through its last PAIRCODE try), not how
    /// long pairing mode itself stays open - that's controlled by OpenPairing/ClosePairing
    /// instead, with no timeout of its own (closes on success or explicit cancel).
    public const int HandshakeTimeoutSeconds = 120;

    private readonly object _lock = new();
    private readonly List<TrustedDevice> _trusted;
    private bool _slotClaimed;
    private string? _openCode;

    /// Fired with the new code when pairing mode opens (Add New Device clicked).
    public event Action<string>? PairingOpened;
    /// Fired when pairing mode closes - explicit cancel, or automatically after a
    /// successful Approve (single-use: one open, one device, then closed again).
    public event Action? PairingClosed;
    /// Fired when an unrecognized device's HELLO actually starts a handshake attempt
    /// against an already-open code - lets DeviceWindow show which device is trying and
    /// steal focus, without changing what code is displayed (it's already showing).
    public event Action<string>? PairingAttemptStarted;
    public event Action? PairingAttemptEnded;
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

    /// Null means pairing is closed - any unrecognized device gets rejected outright.
    public string? OpenCode
    {
        get { lock (_lock) return _openCode; }
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

    /// Generates a fresh code and opens pairing mode - called from the "Add New Device"
    /// button, not automatically. Calling this again while already open regenerates the
    /// code (invalidates whatever was showing before), same as a fresh open.
    public string OpenPairing()
    {
        string code;
        lock (_lock)
        {
            code = GenerateCode();
            _openCode = code;
        }
        PairingOpened?.Invoke(code);
        return code;
    }

    public void ClosePairing()
    {
        lock (_lock) { _openCode = null; }
        PairingClosed?.Invoke();
    }

    /// 6-digit code, cryptographically random (not System.Random) - this is the one thing
    /// standing between "any device on the LAN" and "only devices someone deliberately
    /// approved," so it shouldn't be guessable from a predictable seed.
    private static string GenerateCode() => RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();

    public void NotifyAttemptStarted(string model) => PairingAttemptStarted?.Invoke(model);

    public void NotifyAttemptEnded() => PairingAttemptEnded?.Invoke();

    public void Approve(string deviceId, string model, string build, string name)
    {
        TrustedDevice device;
        lock (_lock)
        {
            _trusted.RemoveAll(d => d.DeviceId == deviceId);
            device = new TrustedDevice(deviceId, model, build, name, DateTime.Now);
            _trusted.Add(device);
            DeviceTrustStore.Save(_trusted);
            _openCode = null; // single-use: this open/scan cycle is done
        }
        PairingClosed?.Invoke();
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
