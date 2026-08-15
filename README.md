# Hi, DISCLAIMER

I am Currently a student and build this app to control my desktop with my mobile, maybe becuase I wanted to operate my screen when I am in my bed, I don't know, I just want to learn and know if it is possible or not !! I do want warn you it is not the safest way to use your device using this method. but if you trust your closed loop network that it is an option.

For future update:
->Planning to make TV application, so you could control that screen as well.

# RemoteControl

Turn an Android phone into a wireless trackpad + keyboard for a Windows PC. Phone → PC
only, over your local Wi-Fi network. Built from scratch

```
[Android phone]                                  [Windows PC]
 TrackpadView / keyboard  ── JSON packets ──┐
 ConnectionManager ──────────────────────────┼─ TCP :5201  ──► InputInjector (SendInput)
                                              └─ UDP :58201 ──► Discovery responder
```

## What's in this folder

- `Windows/` — C# .NET 8 tray app (`RemoteControl.exe`). Sits in the system tray and
  listens on TCP 5201 for input packets and UDP 58201 for auto-discovery. Launched from a
  terminal, it also prints a live packet log there and pops the Devices window; launched
  by double-click (or a Startup-folder shortcut), it runs silently in the background with
  just the tray icon — see "Running the Windows side" below.
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

For a single file that runs on a PC with no .NET installed at all (e.g. to hand to a
friend), publish self-contained instead:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

**Launched from a terminal** (`dotnet run`, or running the exe directly from an open
cmd/PowerShell window), it reattaches to that terminal and prints a live log of
everything the server does, and also pops the Devices window automatically:

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
when things aren't working.

**Launched by double-clicking the exe** (or from a Startup-folder shortcut, for it to
start automatically at login) — no console, no log, no auto-popped window, just the tray
icon. This is the background/silent way to run it; open the Devices window yourself
anytime via the tray icon.

Either way, a tray icon appears (gray dot = idle, green = a phone is connected) —
right-click it for the PC's address, "Devices..." to open the Devices window (see
below), "Copy address" to paste into the phone's CONFIG tab manually, or to Exit.

## Pairing and the Devices window

Right-click the tray icon → **Devices...** opens a real window showing which device is
currently connected, an **Add New Device** button, and every device that's ever been
trusted, with a **Forget selected device** button to revoke one. Forgetting a device that's
*currently connected* disconnects it immediately, not just on its next reconnect attempt —
its session is severed the moment you click Forget.

**Pairing is opt-in, not automatic.** An unrecognized phone trying to connect gets
rejected outright unless you've explicitly clicked **Add New Device** in the Devices
window first — that's what generates the 6-digit code and QR code, and shows them right
there so a new device can be added *before* anyone attempts to connect, not only
reactively after. The code stays valid until either a device successfully uses it or you
click **Cancel Pairing**; a wrong code just gets 5 attempts before that one connection is
rejected, without closing pairing itself. Once a device pairs successfully, the PC
remembers it (by Android ID + model + build number, not just an IP) for every future
connection — no code needed again unless you Forget it.

**Scan the QR code** from the phone's CONFIG tab → **Scan QR to Connect**. It opens an
in-app camera scanner (asks for camera permission the first time) — point it at the QR
and it fills in the host, connects, and submits the pairing code automatically, no
typing. If the code shown happens to be stale by the time you scan it (someone clicked
Add New Device again in between), the app falls back to the normal type-it-in dialog with
an error shown, so nothing silently hangs. Typing the code manually works exactly the
same either way.

**Only one device controls the PC at a time.** The moment any phone starts connecting —
recognized or not — the PC claims an internal slot and refuses every other connection
attempt with a "busy" rejection until that one ends. A second phone (or a laptop's
browser probing the port, or anything else on the LAN) can't interleave input with
whoever's already connected, and can't even attempt to pair while someone else is.

This means someone else on the same Wi-Fi network can no longer connect to and operate
this PC just by knowing (or scanning for) its IP: they'd need to either already be a
trusted device, or type a code that only appears on your own screen at the moment you
approve them.

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

**Saved Devices**: if you control more than one machine from the same phone (a PC, a
media-center laptop, a Raspberry Pi, whatever else runs `RemoteControl.exe`), type an
IP once, tap **+ Save Current Device**, give it a name and a type (TV, Laptop, Desktop,
Monitor, Raspberry Pi, Smart Fridge, or Other) — it shows up as a button in the Saved
Devices list from then on. Tapping a saved device fills in its IP/port and switches the
connection to it immediately, even if you're already connected to something else.
Long-press a saved device to delete it.

CONTROL tab: the trackpad panel supports 1-finger drag to move the cursor, tap with
1/2/3 fingers for left/right/middle click, double-tap for double-click, and long-press
to start a drag (release to drop it). The **KEYS** button swaps the trackpad for an
on-screen keyboard. The **TEXT** button swaps in a text box instead — type with the
phone's own keyboard (autocorrect, swipe-typing, whatever you normally use) and tap
**Send** to type the whole thing on the PC at once, rather than one packet per keystroke.
Only one of trackpad/keyboard/text box is ever showing at a time; tapping KEYS or TEXT
again while it's already active goes back to the trackpad.

**Hold mode**: every key on the on-screen keyboard — letters, digits, Ctrl/Alt/Shift/Win,
arrows, Enter, Tab, Backspace, Space, Esc — behaves like a real keyboard key: a quick tap
presses and releases it once, same as always. **Holding a key down** instead sends it down
and leaves it held — even after you lift your finger — until you tap that same key again
to release it. That's how you build combos: hold Ctrl (it lights up), tap C, tap Ctrl
again to let go. Because holding is now available on any key, not just the four
modifiers, you can also do things like hold an arrow key or Backspace for continuous
repeat in whatever app you're controlling. How long you need to hold before it engages is
adjustable in CONFIG (**Hold threshold**, 0.3s–3.0s, defaults to 2.0s) — turn it down for
a snappier feel, or up if fast typing keeps accidentally triggering it.

Two independent safety nets make sure a held key can never get stuck on the PC: if you
background the app while something is held, the phone releases it right away. And if the
**connection itself drops** (Wi-Fi hiccup, phone walks out of range, PC sleeps) while a
key is held, the PC notices the instant that connection ends and releases whatever it was
holding on its own — it doesn't wait for or need a message from the phone, which is what
actually matters, since a dead connection can't carry one anyway.

**Custom Keys**, at the bottom of the on-screen keyboard, is a faster one-tap alternative
for combos you use often, instead of the hold-then-tap-then-release dance above: tap
**+ Custom Key**, give it a name, tick whichever of Ctrl/Alt/Shift/Win it needs, pick the
key it ends with, and Save. The PC receives the whole combo as one atomic keypress (all
keys down, then all up, in a single batch) rather than a sequence of separate actions.
Long-press a custom key to delete it.

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

Pairing handshake (also newline-delimited JSON, same TCP connection, happens once before
any of the packets above are accepted):

| Packet | Direction | Meaning |
|---|---|---|
| `{"t":"HELLO","deviceId":..,"model":..,"build":..,"name":..}` | phone → PC | first thing sent on every connection, identifies the phone |
| `{"t":"WELCOME"}` | PC → phone | recognized (or just-paired) device, connection is live |
| `{"t":"PAIRREQUIRED"}` | PC → phone | unrecognized device — phone should show a code-entry prompt |
| `{"t":"PAIRCODE","code":".."}` | phone → PC | the code the user typed in, in response to `PAIRREQUIRED`/`WRONGCODE` |
| `{"t":"WRONGCODE","attemptsLeft":..}` | PC → phone | code didn't match, try again |
| `{"t":"REJECTED","reason":".."}` | PC → phone | handshake failed for good (`busy`, `pairing_closed`, `wrong_code`, `bad_hello`) — connection is about to close |

The QR code shown alongside the pairing code isn't part of this TCP protocol at all — it
encodes a `remotecontrol://pair?host=<ip>&port=<port>&code=<code>` link. The app's own
**Scan QR to Connect** button reads it directly via an in-app camera scanner; the app also
registers itself as a handler for that link in case a phone's regular camera app offers to
open it, though that's just a bonus path, not the primary one. Either way it fills in the
host and code automatically, skipping the manual PAIRCODE typing step above (the packet
itself is identical either way — the QR is just a shortcut for supplying it).

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

- One device controls the PC at a time by design (see "Pairing and the Devices window"
  above) — a second connection attempt is rejected outright, not interleaved.
- Pairing trusts a *device*, not a *person*: anyone with physical access to an already-
  trusted phone can control the PC, and the 6-digit code is only as secret as whoever can
  see the PC's screen at the moment it's shown. Fine for a home network among people you
  live with; it's not multi-user access control.
- QR scan-to-pair uses the app's own in-app scanner (CONFIG tab → **Scan QR to
  Connect**), not your phone's regular camera app, so it works the same regardless of
  what camera app is installed. Typing the 6-digit code shown next to the QR works
  exactly the same either way.
- `Ctrl`/`Alt`/`Shift`/`Win` combos only work for letters and digits (their Windows
  virtual-key codes are layout-independent); symbol combos aren't supported.
