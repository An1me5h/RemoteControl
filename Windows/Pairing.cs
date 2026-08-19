using System.Security.Cryptography;

namespace RemoteControl;

/// Owns trust state, the "only one device at a time" rule, and pairing-mode state. A
/// single mutable slot - claimed once a connecting device's HELLO has been read (so its
/// identity/priority is known) and released when that connection ends - is what enforces
/// "only one device can control it at a time": a second connection attempt while the slot
/// is held gets rejected immediately, UNLESS the connecting device is a trusted device
/// with strictly higher Priority than whoever currently holds it, in which case it
/// preempts them instead - see TryClaimOrPreempt.
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

    /// A monotonically increasing "who owns the slot right now" ticket. Needed because
    /// preemption (TryClaimOrPreempt below) can hand the slot straight from one connection
    /// to another WITHOUT ever releasing it in between - the preempted connection's own
    /// cleanup still reaches its ReleaseSlot call eventually (it doesn't know it's been
    /// preempted, it just sees its socket die), and that call must be a no-op rather than
    /// releasing a slot that's since been reclaimed by the device that preempted it. Every
    /// caller of ReleaseSlot passes back the generation it received when it claimed the
    /// slot; the release only actually takes effect if that generation is still current.
    private long _slotGeneration;

    public long? TryClaimSlot()
    {
        lock (_lock)
        {
            if (_slotClaimed) return null;
            _slotClaimed = true;
            return ++_slotGeneration;
        }
    }

    public void ReleaseSlot(long generation)
    {
        lock (_lock)
        {
            if (_slotClaimed && generation == _slotGeneration) _slotClaimed = false;
        }
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
            device = new TrustedDevice(deviceId, model, build, name, DateTime.Now,
                History: new List<DeviceHistoryEntry> { new(DateTime.Now, "Paired") });
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

    /// Fired whenever a trusted device's OWN record changes in place (connected,
    /// disconnected, renamed) - as opposed to DeviceApproved/DeviceForgotten, which are
    /// about a device being added to or removed from the trusted list entirely.
    public event Action<TrustedDevice>? DeviceUpdated;

    private TrustedDevice? UpdateDevice(string deviceId, Func<TrustedDevice, TrustedDevice> update)
    {
        TrustedDevice? updated;
        lock (_lock)
        {
            int index = _trusted.FindIndex(d => d.DeviceId == deviceId);
            if (index < 0) return null;
            updated = update(_trusted[index]);
            _trusted[index] = updated;
            DeviceTrustStore.Save(_trusted);
        }
        DeviceUpdated?.Invoke(updated);
        return updated;
    }

    private static TrustedDevice WithHistory(TrustedDevice device, string eventText) =>
        device with { History = new List<DeviceHistoryEntry>(device.History) { new(DateTime.Now, eventText) } };

    /// Called on EVERY successful handshake (Server.HandleClientAsync), whether this
    /// connection just got approved for the first time (Approve() above already logged
    /// "Paired" for that case) or it's an ordinary reconnect of an already-trusted device.
    public void RecordConnected(string deviceId) =>
        UpdateDevice(deviceId, device => WithHistory(device, "Connected") with { LastConnectedAt = DateTime.Now });

    public void RecordDisconnected(string deviceId) =>
        UpdateDevice(deviceId, device => WithHistory(device, "Disconnected") with { LastDisconnectedAt = DateTime.Now });

    public void Rename(string deviceId, string newName) =>
        UpdateDevice(deviceId, device => WithHistory(device, $"Renamed from '{device.Name}' to '{newName}'") with { Name = newName });

    public void SetViewOnly(string deviceId, bool viewOnly) =>
        UpdateDevice(deviceId, device => WithHistory(device,
            viewOnly ? "Set to View Only" : "Set to Full Control") with { ViewOnly = viewOnly });

    public void SetPriority(string deviceId, int priority) =>
        UpdateDevice(deviceId, device => WithHistory(device,
            $"Priority changed from {device.Priority} to {priority}") with { Priority = priority });

    /// Server.HandleClientAsync's per-packet check - true if the currently connected
    /// device (if any) is marked ViewOnly. Queried fresh on every packet rather than cached
    /// once at connection start, so toggling permission mid-session (DeviceWindow's
    /// right-click menu) takes effect on the very next packet, no reconnect needed.
    public bool IsViewOnly(string deviceId)
    {
        lock (_lock) return _trusted.FirstOrDefault(d => d.DeviceId == deviceId)?.ViewOnly ?? false;
    }

    /// Priority's whole point: a connecting device that outranks whoever's currently
    /// connected should be able to preempt them, not just get rejected as "busy" like any
    /// other second connection would. Unlike the old always-claim-before-HELLO design, this
    /// needs the candidate's id up front - Server.cs now reads HELLO before deciding
    /// whether to claim the slot, specifically so this comparison is possible. Returns the
    /// new slot generation if claimed (either the slot was free, or preemption won) - null
    /// if rejected as busy - plus the id of whoever got kicked (null if the slot was simply
    /// free). The actual socket-closing for a preempted device happens in Server.cs; this
    /// method only decides slot ownership, it never touches a socket.
    public (long? generation, string? preemptedDeviceId) TryClaimOrPreempt(string candidateDeviceId, string? currentlyConnectedDeviceId)
    {
        lock (_lock)
        {
            if (!_slotClaimed)
            {
                _slotClaimed = true;
                return (++_slotGeneration, null);
            }

            if (currentlyConnectedDeviceId == null) return (null, null); // slot claimed but nobody fully connected yet (mid-handshake) - don't preempt a handshake in progress

            // LOWER wins - 1 is the best/highest priority a device can have (PriorityDialog
            // won't even let the user pick anything below 1). A device that's never had a
            // priority set (Priority == 0, TrustedDevice's default) has no claim at all -
            // Rank() maps that to int.MaxValue so it always compares as the weakest possible
            // value: it can't preempt anyone, and no priority-less device can preempt IT
            // either (MaxValue is never strictly less than MaxValue), which is what keeps
            // this opt-in - nothing preempts anything until the user explicitly assigns a
            // real number to at least the device trying to take over.
            static int Rank(int priority) => priority <= 0 ? int.MaxValue : priority;
            int candidateRank = Rank(_trusted.FirstOrDefault(d => d.DeviceId == candidateDeviceId)?.Priority ?? 0);
            int currentRank = Rank(_trusted.FirstOrDefault(d => d.DeviceId == currentlyConnectedDeviceId)?.Priority ?? 0);
            if (candidateRank >= currentRank) return (null, null); // strictly lower required - a TIE does not preempt (avoids two equal-priority devices fighting each other on every reconnect)

            // The slot stays claimed throughout - this is a HANDOFF, not a release-then-
            // reclaim (which would leave a window for some THIRD connection to sneak in
            // between). Bumping the generation here is what makes the preempted
            // connection's own eventual ReleaseSlot(itsOldGeneration) call a safe no-op
            // instead of wrongly releasing the winner's slot - see _slotGeneration's doc.
            return (++_slotGeneration, currentlyConnectedDeviceId);
        }
    }
}
