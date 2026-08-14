# RemoteControl

Turn an Android phone into a wireless trackpad + keyboard for a Windows PC. Phone → PC
only, over your local Wi-Fi network. Built from scratch (a ground-up rewrite, not related
to the old `PhoneTrack` project).

```
[Android phone]                                  [Windows PC]
 TrackpadView / keyboard  ── JSON packets ──┐
 ConnectionManager ──────────────────────────┼─ TCP :5201  ──► InputInjector (SendInput)
                                              └─ UDP :58201 ──► Discovery responder
```

## What's in this folder

- `Windows/` — C# .NET 8 console + tray app (`RemoteControl.exe`). Prints every packet
  it receives to the console it was launched from, and also sits in the system tray.
  Listens on TCP 5201 for input packets and UDP 58201 for auto-discovery.
- `Android/` — Kotlin app (Gradle project, package `com.remotecontrol`). Trackpad +
  on-screen keyboard, CONTROL/CONFIG tabs, auto-reconnect.
- `RemoteControl-debug.apk` — prebuilt debug APK, sideloadable as-is.

## Running the Windows side

```powershell
cd Windows
dotnet run
```

Or build a standalone exe:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

The terminal you ran `dotnet run` in shows a live log of everything the server does —
the address it's listening on, every client connect/disconnect, and every packet as it
arrives:

```
RemoteControl listening on 192.168.178.125:5201 (TCP) and UDP 58201 (discovery)
Waiting for a phone to connect...
[16:47:11.362] Client connected. 1 connected.
[16:47:23.478] MOVE     dx=  15.0 dy=  -7.0   |    16.6px @   25.0deg
[16:47:23.496] KEY      'q'
```

`MOVE` lines show both the raw `dx/dy` and a magnitude+angle vector. Anything that
couldn't be parsed shows up as `?? unrecognized: <raw line>` instead of silently
vanishing — see "If input doesn't seem to reach the PC" below for how to read this log
when things aren't working. A tray icon also appears alongside the console (gray dot =
idle, green = a phone is connected) — right-click it for the PC's address, or "Copy
address" to paste into the phone's CONFIG tab manually, or to Exit.

**Elevated windows**: `RemoteControl.exe` runs at normal (`asInvoker`) privilege, so by
Windows' own UIPI rules it cannot send input into a window owned by an elevated process
(e.g. Task Manager running as admin). If you need that, right-click the exe → *Run as
administrator*.

## Running the Android side

Build and install directly to a phone connected over USB (Developer options → USB
debugging must be on):

```powershell
cd Android
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'
.\gradlew installDebug
```

Or install the prebuilt `RemoteControl-debug.apk` at the repo root:

```powershell
adb install -r RemoteControl-debug.apk
```

On first launch, go to the CONFIG tab. Leave the IP field blank and tap **Connect** to
auto-discover the PC on the LAN, or type its IP manually (shown in the Windows tray
tooltip / right-click menu) if discovery doesn't find it (e.g. the phone and PC are on
different Wi-Fi subnets, or a firewall blocks UDP broadcast).

CONTROL tab: the trackpad panel supports 1-finger drag to move the cursor, tap with
1/2/3 fingers for left/right/middle click, double-tap for double-click, and long-press
to start a drag (release to drop it). The **KEYS** button swaps the trackpad for an
on-screen keyboard; **Ctrl/Alt/Shift/Win** are sticky toggles — tap to hold, tap again to
release — so combos like Ctrl+C work by holding Ctrl then tapping C. The **TEXT** button
swaps in a text box instead — type with the phone's own keyboard (autocorrect, swipe-
typing, whatever you normally use) and tap **Send** to type the whole thing on the PC at
once, rather than one packet per keystroke. Only one of trackpad/keyboard/text box is
ever showing at a time; tapping KEYS or TEXT again while it's already active goes back to
the trackpad.

**Modifier combos**: holding Ctrl/Alt/Shift/Win (tap to toggle) and then tapping a letter,
digit, or a special key like Tab/Esc/Enter/arrows already sends a real combo — e.g. hold
Ctrl, tap Tab, gives Ctrl+Tab. **Custom Keys**, at the bottom of the on-screen keyboard,
is a faster one-tap alternative for combos you use often: tap **+ Custom Key**, give it a
name, tick whichever of Ctrl/Alt/Shift/Win it needs, pick the key it ends with, and Save.
The PC receives the whole combo as one atomic keypress (all keys down, then all up, in a
single batch) rather than a sequence of separate hold/tap/release actions, so it behaves
exactly like pressing them together on a real keyboard. Long-press a custom key to delete
it.

## Firewall

Windows will prompt to allow `RemoteControl.exe` through the firewall on first run
(needs both TCP 5201 and UDP 58201 on private networks). Allow it, or auto-discovery and
the phone's connection will silently fail.

## Wire protocol

Newline-delimited JSON, phone → PC, TCP port 5201:

| Packet | Meaning |
|---|---|
| `{"t":"MOVE","dx":..,"dy":..}` | move cursor by relative delta |
| `{"t":"SCROLL","d":..}` | scroll wheel notches |
| `{"t":"LCLICK"}` / `{"t":"RCLICK"}` / `{"t":"MCLICK"}` | click |
| `{"t":"LDOWN"}` / `{"t":"LUP"}` | left button hold / release (drag) |
| `{"t":"KEY","ch":".."}` | type a unicode character |
| `{"t":"VKDOWN","k":..}` / `{"t":"VKUP","k":..}` | hold/release a Windows virtual-key (modifiers) |
| `{"t":"VKTAP","k":..}` | press+release a virtual-key (Enter, arrows, etc.) |
| `{"t":"TEXT","text":".."}` | type a whole block of text at once (the TEXT panel's Send button) |
| `{"t":"COMBO","keys":[..]}` | press multiple virtual-keys together as one atomic combo (custom keys) |
| `{"t":"PING"}` | PC replies `{"t":"PONG"}` — used for the latency readout |

UDP port 58201, discovery only: phone broadcasts `RC_DISCOVER`, PC replies
`RC_HOST <ip> <tcp-port>` directly to the sender. This is a custom protocol, not real
mDNS/Bonjour — it doesn't touch the 224.0.0.251:5353 multicast group, so it can't
collide with (or be confused by) other mDNS traffic on the network.

## If input doesn't seem to reach the PC

Watch the console (see above) before doing anything else — it tells you exactly which
side the problem is on:

- **Nothing shows up when you touch the trackpad or press a key** → the phone isn't
  actually sending. Check the CONFIG tab's log line for send errors, and confirm the
  status dot is genuinely green (not stuck) when you try.
- **Lines show up but as `?? unrecognized: ...`** → something is reaching the PC but
  failing to parse — that's a real bug, worth reporting with the raw line shown.
- **Lines decode correctly (`MOVE dx=... dy=...`, `KEY '...'`) but nothing happens on
  screen** → the packets are fine; the problem is in `InputInjector`/`SendInput`, e.g. a
  permissions issue (try "Run as administrator" — see the elevated-windows note above).

## Offline queue

If the phone loses its connection mid-use, `ConnectionManager` doesn't drop your input —
it queues it (capped at the last ~300 packets) and replays it once reconnected, one
packet every 10ms rather than all at once, so a several-second network blip doesn't
dump a burst of stale trackpad movement on the PC in one instant. An explicit
Disconnect from the CONFIG tab clears the queue instead of replaying it later.

## Known limitations

- One phone at a time is the intended use case; the server will technically accept
  multiple TCP connections but their input just interleaves.
- No pairing/auth — anyone on the same LAN who knows (or discovers) the IP can send
  input. Fine for a home network; don't run this on an untrusted network.
- `Ctrl`/`Alt`/`Shift`/`Win` combos only work for letters and digits (their Windows
  virtual-key codes are layout-independent); symbol combos aren't supported.
