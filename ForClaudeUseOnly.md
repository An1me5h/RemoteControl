# ForClaudeUseOnly.md — RemoteControl

> Internal map of this repo so the whole tree does not need re-reading.
> Verified against source on 2026-08-14.

---

## 0. Branch log

Not a git repo yet (no `git init` run here) — no branches to log against. If this gets
git-initialized later, start logging changes here per branch as work happens.

### 2026-08-14 — dropped the log window for real console output

User explicitly rejected the separate `LogWindow` from the previous entry below ("i dont
wna tseperate log, i told you multiple time i want to know what is going on in the
terminal") - they run the server via `dotnet run` in a terminal and want the log *there*,
not in another window. Root cause: `RemoteControl.csproj` had `<OutputType>WinExe</OutputType>`
(GUI subsystem) specifically so double-clicking the exe wouldn't pop a console - but that
same setting means `Console.WriteLine` output never reaches a terminal at all, even one
that launched it via `dotnet run`, which is why the user saw a blank prompt with no error
and no output.

Fix: switched `OutputType` to `Exe` (console subsystem) and deleted `LogWindow.cs`
entirely. `TrayApp.cs` now writes connect/disconnect and every `PacketReceived`/
`UndecodableLineReceived` line straight to `Console.WriteLine` - no marshaling needed for
that part since `Console.WriteLine` is thread-safe on its own (unlike `NotifyIcon`
updates, which still go through the hidden-window `Invoke` pattern as before). Tray icon
is unchanged and still appears alongside the console. `PacketFormat.cs` (the
human-readable formatter) was kept as-is, just consumed differently.

**Tradeoff now baked in, not accidental**: double-clicking `RemoteControl.exe` will pop a
console window (previously it wouldn't have). Given the user's actual workflow is
exclusively `dotnet run` from a terminal, this was judged the right default over the
previous WinExe-plus-hidden-log-window design - revisit only if the user later wants a
true double-click-silent launch path back (e.g. via `AttachConsole(ATTACH_PARENT_PROCESS)`
instead of a hard `OutputType` switch).

Verified end to end: rebuilt, ran the exe directly with stdout redirected to a file,
confirmed the startup banner printed, confirmed the **already-reconnecting phone**
(its `ConnectionManager` retries every 3s in the background, so it picked the new server
instance back up within ~11s of it starting) showed up as `Client connected` followed by
real `PING` lines, then sent a raw `MOVE`+`KEY` packet over a manual TCP socket and
watched both format correctly in the log. The phone's own connection dropped again ~11s
after reconnecting for a reason not yet investigated (worth watching for - could be
Android background/Doze throttling since the app isn't foregrounded, or could be the same
underlying issue behind the original "connects but nothing happens" report).

### 2026-08-14 — diagnostics + offline queue

User installed the app, connected (tray dot went green), but trackpad/keyboard input had
no visible effect on the PC and there was no way to see what was actually arriving.
Re-verified the injection pipeline itself was fine — sent a raw hand-crafted `MOVE`
packet straight to the *live, currently-running* `dotnet run` process over a TCP socket
and the cursor moved correctly — so the gap was specifically in visibility, not (as far
as this pass could prove) `InputInjector`/`SendInput` itself. Added:

- **`Windows/LogWindow.cs`** (new) + **`Windows/PacketFormat.cs`** (new) — a hidden-by-
  default debug window (tray menu → "Show log") listing every decoded packet in
  human-readable form (`MOVE` shows both raw dx/dy and a magnitude+angle vector, `KEY`
  shows the actual character, clicks/drags/modifiers are named). Lines that fail to
  decode now also surface as `?? unrecognized: <raw line>` instead of being silently
  dropped (`Server.cs`'s `if (packet is null) continue;` previously ate them with no
  trace at all). `LogWindow.Append` checks `Visible` before doing any formatting/marshal
  work and always uses `BeginInvoke` (never blocking `Invoke`), so the window costs
  nothing when hidden and can never stall the input-dispatch hot path even when open.
- **`Server.cs`** — added `PacketReceived`/`UndecodableLineReceived` events, fired for
  every line read (decoded or not) before dispatch.
- **`Android/.../ConnectionManager.kt`** — `send()` no longer silently drops packets
  while offline; they're queued (`offlineQueue`, capped at `MAX_QUEUED_PACKETS = 300`,
  drop-oldest on overflow) and replayed one at a time, `DRAIN_INTERVAL_MS = 10` apart,
  right after reconnecting and before `readLoop` resumes normal operation - so a
  reconnect never dumps a burst of stale backlog on the PC in one instant. An explicit
  `disconnect()` clears the queue instead of preserving it.

Both sides rebuilt clean and the APK was reinstalled+relaunched on the Pixel 7 (no
crash). Root cause of the original "nothing happens" report is still open — the log
window is what should pin it down on the next real test with the phone in hand.

### 2026-08-14 — initial build

Built from scratch as a full rewrite of the earlier `PhoneTrack` project (see
`../PhoneTrack/ForClaudeUseOnly.md`), per explicit instruction to not reuse any of its
code and to start a fresh project named `RemoteControl` with both Android and Windows
sides. Nothing here is copied from PhoneTrack — only the general concept (phone as
trackpad+keyboard, phone→PC only, TCP JSON) and a few toolchain version numbers
(AGP 8.13.0 / Kotlin 2.1.20 / Gradle 9.0.0 / compileSdk 36 / minSdk 26, already known-good
on this machine) carried over.

Deliberately fixed several things that were open bugs/gaps in PhoneTrack rather than
reproducing them:
- **Real JSON parsing** (`System.Text.Json` on the PC side) instead of PhoneTrack's
  hand-rolled substring `Dispatch`.
- **Working auto-discovery** — a small custom UDP broadcast protocol on port 58201
  (`RC_DISCOVER` / `RC_HOST <ip> <port>`), actually wired up and started. PhoneTrack's
  `MdnsAdvertiser` was never constructed by `Program.cs`, so discovery could never work
  there; it also polluted the real Bonjour multicast group (224.0.0.251:5353), which this
  version avoids entirely by not using multicast.
- **System tray icon** — PhoneTrack's README described one but it never existed; this is
  a real `NotifyIcon` (gray = idle, green = phone connected) with a status/copy-address/
  exit menu, no console window.
- **Latency actually measured** — PING/PONG round-trip is wired end to end and shown in
  the Android status bar; PhoneTrack had the UI element but the PC never replied to PING.
- **`assembleRelease` works out of the box** — `minifyEnabled false` by default (PhoneTrack
  had it `true` pointing at a `proguard-rules.pro` that didn't exist).
- **Sticky modifier keys** (Ctrl/Alt/Shift/Win) for real key combos (Ctrl+C etc.) via
  VKDOWN/VKUP — PhoneTrack's keyboard only ever sent isolated taps, no combos possible.

Both sides build clean: `dotnet build` (Windows, 0 warnings/errors) and
`gradlew clean assembleDebug` (Android, BUILD SUCCESSFUL, 36 tasks). Runtime-smoke-tested
the Windows exe directly (not just compiled): launched it, confirmed it listens on TCP
5201 and UDP 58201, sent a real `MOVE` packet over a raw TCP socket and confirmed the OS
cursor moved, and sent a real `RC_DISCOVER` UDP broadcast and got back a correct `RC_HOST`
reply. The Android APK was installed to the same Pixel 7 used for PhoneTrack
(`34271FDH2000QZ`) via `adb install -r` and launched (`am start`); confirmed via
`dumpsys activity activities` that `MainActivity` reached `Resumed` state with a live
`ProcessRecord` and no crash in logcat. Not yet click-tested end-to-end against the
Windows server on the same network (i.e. actually connecting and dragging the trackpad) —
that still needs a human with the phone in hand.

---

## 1. What this project is

Turn an Android phone into a wireless trackpad + keyboard for a Windows PC.

```
[Android phone]                                  [Windows PC]
 TrackpadView / keyboard  ── JSON packets ──┐
 ConnectionManager ──────────────────────────┼─ TCP :5201  ──► InputInjector (SendInput)
                                              └─ UDP :58201 ──► Discovery responder
```

One direction only: phone → PC. The only thing that goes PC → phone is `PONG` (latency
reply) and the UDP discovery reply — there is no screen mirroring, no clipboard sync,
nothing else.

---

## 2. Directory layout

```
RemoteControl/
├─ README.md                     user-facing build/run doc
├─ ForClaudeUseOnly.md            this file
├─ RemoteControl-debug.apk        prebuilt debug APK (rebuild after any Android change)
├─ Windows/                       C# .NET 8 tray app
│   ├─ RemoteControl.csproj       Exe (console subsystem, deliberately - see §0), net8.0-windows, UseWindowsForms=true (for NotifyIcon)
│   ├─ app.manifest                asInvoker + PerMonitorV2 DPI, wired via <ApplicationManifest>
│   ├─ Program.cs                 entry point
│   ├─ TrayApp.cs                 ApplicationContext - the whole "UI"
│   ├─ TrayIcons.cs               runtime-generated dot icons (no .ico asset)
│   ├─ PacketFormat.cs            Packet -> human-readable line, printed via Console.WriteLine
│   ├─ Server.cs                  TCP listener + per-client handler
│   ├─ Discovery.cs                UDP broadcast responder + GetLocalAddress()
│   ├─ Packet.cs                  wire-format decode (System.Text.Json)
│   ├─ InputInjector.cs           SendInput P/Invoke, dispatches Packet -> OS input
│   └─ .gitignore                 bin/, obj/
└─ Android/                       Kotlin client, Gradle project, package com.remotecontrol
    ├─ settings.gradle / build.gradle / gradle.properties / local.properties
    ├─ gradlew / gradlew.bat / gradle/wrapper/          Gradle 9.0.0, generated 2026-08-14
    ├─ .gitignore
    └─ app/
        ├─ build.gradle           compileSdk/targetSdk 36, minSdk 26, AGP 8.13.0, Kotlin 2.1.20
        ├─ proguard-rules.pro     empty - exists so assembleRelease doesn't fail
        └─ src/main/
            ├─ AndroidManifest.xml
            ├─ java/com/remotecontrol/
            │   ├─ MainActivity.kt
            │   ├─ TrackpadView.kt
            │   ├─ ConnectionManager.kt
            │   └─ Packet.kt
            └─ res/
                ├─ layout/activity_main.xml
                ├─ values/colors.xml, strings.xml, styles.xml, themes.xml
                ├─ drawable/dot_red.xml, dot_yellow.xml, dot_green.xml, key_bg.xml,
                │   key_bg_active.xml, ic_launcher_background.xml, ic_launcher_foreground.xml
                └─ mipmap-anydpi-v26/ic_launcher.xml, ic_launcher_round.xml
```

No `res/mipmap-*dpi/` raster PNGs — minSdk is 26, so the `anydpi-v26` adaptive-icon XMLs
(pure vector, no bitmap asset needed) are sufficient on their own; there's no lower-API
fallback to provide.

---

## 3. Function index

### Windows — `Windows/`

**Program.cs** (15 lines) — entry point.

| Line | Member |
|------|--------|
| 5 | `static class Program` |
| 8 | `Main()` — sets DPI mode, `Application.Run(new TrayApp())` |

**TrayApp.cs** (92 lines) — owns the console output *and* the tray icon; no other window.

| Line | Member | Notes |
|------|--------|-------|
| 8 | `class TrayApp : ApplicationContext` | |
| 17 | ctor | creates a hidden `Form` (for `Invoke` marshaling of `NotifyIcon` updates only), prints the startup banner, builds the tray menu, wires `Server`'s events straight to `Console.WriteLine` (no marshaling needed - `Console.WriteLine` is thread-safe on its own), starts `Server` + `Discovery` |
| 58 | `Now()` | timestamp prefix for log lines |
| 60 | `OnClientCountChanged(int)` | marshals to UI thread via the hidden form, updates icon/tooltip/menu text (separate from the `Console.WriteLine` subscriber on the same event, which needs no marshaling) |
| 75 | `StatusText(int)` | |
| 80 | `Truncate(string)` | `NotifyIcon.Text` is capped at 63 chars by the Win32 shell API |
| 82 | `ExitThreadCore()` | stops server/discovery, disposes icon |

**TrayIcons.cs** (39 lines) — generates the tray dot icons at runtime.

| Line | Member | Notes |
|------|--------|-------|
| 9 | `static class TrayIcons` | |
| 11–12 | `Idle`, `Connected` | gray / mint-green dot |
| 14 | `MakeDotIcon(Color)` | `Bitmap` → `GetHicon()` → clone → `DestroyIcon` on the original handle (leak-safe pattern) |
| 38 | `DestroyIcon` | `[DllImport("user32.dll")]` |

**PacketFormat.cs** (41 lines) — `Packet` → human-readable line, printed by `TrayApp` via
`Console.WriteLine`.

| Line | Member | Notes |
|------|--------|-------|
| 4 | `static class PacketFormat` | |
| 6 | `KnownVk` | vk-code → friendly name (Ctrl, Alt, Enter, arrows, ...) for the log |
| 13 | `Describe(Packet p)` | one line per packet type |
| 32 | `DescribeMove(Packet p)` | shows raw `dx/dy` **and** magnitude+angle - the "point/angle/value" vector view, easier to eyeball than two separate deltas |
| 39 | `VkName(int k)` | falls back to `0xNN` for anything not in `KnownVk` |

**Server.cs** (94 lines) — TCP listener, one thread per client.

| Line | Member | Notes |
|------|--------|-------|
| 8 | `class Server` | |
| 12 | `Port = 5201` (via the listener) | |
| 16–18 | `ClientCountChanged`, `PacketReceived`, `UndecodableLineReceived` events | the latter two feed `Console.WriteLine` via `TrayApp` |
| 20 | `Start()` | |
| 27 | `Stop()` | |
| 33 | `AcceptLoopAsync(CancellationToken)` | |
| 50 | `HandleClientAsync(TcpClient, CancellationToken)` | `NoDelay=true`; every decoded line fires `PacketReceived` (or `UndecodableLineReceived` if `Decode` returned null) before dispatch; PING gets a real `PONG` reply; everything else goes to `InputInjector.Dispatch` |

**Discovery.cs** (66 lines) — UDP broadcast responder, own protocol (not mDNS).

| Line | Member | Notes |
|------|--------|-------|
| 10 | `static class Discovery` | |
| 15 | `StartResponder(int tcpPort, CancellationToken)` | |
| 18 | `ResponderLoopAsync(...)` | binds UDP `:58201`, replies to `RC_DISCOVER` with `RC_HOST <ip> <port>` |
| 53 | `GetLocalAddress()` | UDP-connect-to-8.8.8.8 trick to find the real outbound NIC (skips VPN/Hyper-V/WSL virtual adapters) |

**Packet.cs** (68 lines) — wire-format decode.

| Line | Member | Notes |
|------|--------|-------|
| 5 | `enum PacketType` | `Move, Scroll, LClick, RClick, MClick, LDown, LUp, Key, VkDown, VkUp, VkTap, Ping, Unknown` |
| 11 | `readonly record struct Packet(...)` | |
| 19 | `static class PacketCodec` | |
| 21 | `Decode(string line)` | real `System.Text.Json` parse, not string matching |

**InputInjector.cs** (168 lines) — `SendInput` P/Invoke, dispatches decoded packets to OS input.

| Line | Member | Notes |
|------|--------|-------|
| 5 | `enum MouseButtonKind` | |
| 8 | `static class InputInjector` | |
| 10 | `Dispatch(Packet p)` | the one switch every packet type goes through |
| 30 | `MoveRelative(double dx, double dy)` | relative gesture in, **absolute** `SendInput` out (normalized 0–65535 over the virtual desktop) — bypasses the pointer-acceleration curve so Wi-Fi/TCP timing jitter doesn't distort the delta-to-pixel mapping |
| 57 | `Scroll(int notches)` | `notches * 120` = one wheel click |
| 63 | `Click(MouseButtonKind)` | down+up in one `SendInput` call |
| 70 | `ButtonState(MouseButtonKind, bool down)` | drag hold/release |
| 76 | `SendChar(char c)` | `KEYEVENTF_UNICODE` — layout-independent |
| 83 | `KeyState(ushort vk, bool down)` | modifier hold/release |
| 86 | `KeyTap(ushort vk)` | full press+release |
| 92 | `FlagsFor(MouseButtonKind)` | |
| 100 | `MouseInput(...)` / 106 `KeyInput(...)` | `INPUT` struct builders |
| 167 | `SendInput(...)` | `[DllImport("user32.dll")]` |

### Android — `Android/app/src/main/java/com/remotecontrol/`

**Packet.kt** (58 lines) — mirrors the Windows wire format.

| Line | Member | Notes |
|------|--------|-------|
| 6 | `sealed class Packet` | |
| 9–31 | `Move, Scroll, LeftClick, RightClick, MiddleClick, LeftDown, LeftUp, Ping, Key, VkDown, VkUp, VkTap` | `Key` uses `JSONObject` for correct char escaping; the rest are raw string templates |
| 34 | `object VK` | Windows virtual-key constants; `forChar(Char)` maps `'A'..'Z'`/`'0'..'9'` to their VK codes (ASCII-aligned) for modifier combos |

**ConnectionManager.kt** (206 lines) — TCP client, UDP discovery, auto-reconnect,
latency, offline queue.

| Line | Member | Notes |
|------|--------|-------|
| 19 | `class ConnectionManager` | no `Context` needed - discovery uses plain UDP broadcast, not multicast, so no `MulticastLock`/`WifiManager` dependency (unlike a typical mDNS-based design) |
| 20 | `enum class State` | `DISCONNECTED, CONNECTING, CONNECTED` |
| 61 | `connect(host: String?, port: Int)` | `host == null` → auto-discover |
| 66 | `disconnect()` | clears `offlineQueue` too - user-initiated, the backlog is no longer relevant |
| 76 | `send(Packet)` | fast path: connected + `offlineQueue` empty → write immediately. Otherwise (offline, or mid-drain) → enqueue, capped at `MAX_QUEUED_PACKETS` (drop-oldest on overflow). Never silently drops anymore |
| 95 | `drainOfflineQueue()` | replays the backlog one packet every `DRAIN_INTERVAL_MS` (10ms) - called right after reaching `CONNECTED`, before `readLoop` resumes. Runs on the same background thread as `connectLoop`; a send failure re-queues the packet and rethrows, so `connectLoop`'s normal catch handles reconnection uniformly |
| 109 | `connectLoop(...)` | reconnect loop on a single background executor thread; calls `drainOfflineQueue()` right after `setState(CONNECTED)` |
| 145 | `readLoop(Socket)` | paces `PING` every 5s using a **dynamic** `soTimeout` (= time left until next scheduled ping), not a fixed timeout — a fixed timeout would let the actual ping cadence drift up to 2x late in steady state |
| 168 | `discoverHost(port)` | sends `RC_DISCOVER` to `255.255.255.255:58201`, waits for `RC_HOST ...` |
| 189 | `closeSocket()` |
| 199 | `setState(State)` / 203 `log(String)` | both hop to the main thread via `Handler(Looper.getMainLooper())` |

**TrackpadView.kt** (120 lines) — custom `View`, touch → `Packet`.

| Line | Member | Notes |
|------|--------|-------|
| 14 | `class TrackpadView : View` | |
| 19–20 | `onPacket`, `sensitivity` | default `1.4f` |
| 70 | `onTouchEvent(MotionEvent)` | tracks `activePointers` (live) separately from `maxPointers` (gesture-lifetime max) — needed because `GestureDetector`'s tap callbacks fire on a delayed handler message well after `ACTION_UP`, by which point a live pointer count would already have been reset to 0 |
| 114 | `resetOrigin(MotionEvent)` | called on every pointer-count change to avoid a cursor jump from stale `lastX/lastY` |
| (inner) `GestureDetector.SimpleOnGestureListener` | `onSingleTapConfirmed` (finger count → click type), `onDoubleTap`, `onLongPress` (drag start + haptic), `onScroll` (2+ fingers only) |

**MainActivity.kt** (231 lines) — two tabs (plain toggle buttons, not `TabLayout` — no
Material dependency was worth pulling in for a 2-way toggle), CONTROL / CONFIG.

| Line | Member | Notes |
|------|--------|-------|
| 14 | `class MainActivity : AppCompatActivity` | |
| 43 | `onCreate` | |
| 60 | `bindViews()` | |
| 79 | `setupTabs()` / 85 `selectTab(Boolean)` | |
| 92 | `setupTrackpad()` | `trackpadView.onPacket = { conn.send(it) }` |
| 96 | `setupClickButtons()` | LEFT/RIGHT/MID buttons below the pad |
| 102 | `setupKeyboardToggle()` | KEYS ⇄ PAD button swaps `trackpadView`/`keyboardPanel` visibility - they never coexist |
| 111 | `setupKeyboard()` | wires special-key taps (Esc/Tab/Backspace/Enter/Space/arrows) and the 4 sticky modifier buttons |
| 141 | `toggleModifier(vk, Button)` | Ctrl/Alt/Shift/Win are sticky - tap to hold (`VkDown`, highlighted bg), tap again to release (`VkUp`) |
| 151 | `assignCharKeys(ViewGroup)` | recursive; reads the single char each key carries in `android:tag` |
| 166 | `sendChar(Char)` | if any modifier is held **and** the char has a VK mapping (letters/digits), sends a real `VkTap` instead of a unicode `Key` so the combo (e.g. Ctrl+C) actually reaches Windows; everything else always falls back to plain `Key` |
| 176 | `setupConfig()` | host/port/sensitivity persistence via `SharedPreferences("remotecontrol")`; sensitivity range 0.2–3.2 in 0.1 steps (`SeekBar` progress 0–30) |
| 197 | `setupConnection()` | status dot colour, latency readout, Connect/Disconnect button |
| 227 | `onDestroy()` | disconnects |

---

## 4. Wire protocol

Newline-delimited JSON, TCP port **5201**, phone → PC (except `PONG`, PC → phone).

| Packet | Sent by | Handled by PC |
|--------|---------|---------------|
| `{"t":"MOVE","dx":..,"dy":..}` | `TrackpadView.onTouchEvent` | yes |
| `{"t":"SCROLL","d":..}` | `onScroll` (2+ fingers) | yes |
| `{"t":"LCLICK"}` / `{"t":"RCLICK"}` / `{"t":"MCLICK"}` | tap (1/2/3 fingers) / click buttons | yes |
| `{"t":"LDOWN"}` / `{"t":"LUP"}` | long-press drag start/end | yes |
| `{"t":"KEY","ch":".."}` | char keys (no modifier held, or non-alnum char) | yes — `KEYEVENTF_UNICODE` |
| `{"t":"VKDOWN","k":..}` / `{"t":"VKUP","k":..}` | Ctrl/Alt/Shift/Win toggle | yes — modifier hold/release |
| `{"t":"VKTAP","k":..}` | Esc/Tab/Backspace/Enter/Space/arrows, or a letter/digit typed while a modifier is held | yes |
| `{"t":"PING"}` | every 5s | yes — PC replies `{"t":"PONG"}` |

UDP port **58201**, discovery only, not real mDNS (own protocol, doesn't touch
224.0.0.251:5353):

| Message | Direction |
|---|---|
| `RC_DISCOVER` | phone → broadcast `255.255.255.255:58201` |
| `RC_HOST <ip> <port>` | PC → unicast reply to sender |

---

## 5. Known limitations (by design, not bugs to fix reflexively)

1. **No pairing/auth.** Anyone on the LAN who has or discovers the IP can send input.
   Acceptable for a home network; don't expose this beyond one.
2. **One phone at a time is the intended use.** `Server` will accept multiple concurrent
   TCP clients, but nothing arbitrates between them - two phones' input just interleaves.
3. **Modifier combos only cover letters and digits.** `Packet.VK.forChar` only maps
   `A-Z`/`0-9` (their VK codes equal ASCII); symbol combos (e.g. Ctrl+`/`) aren't
   supported and silently fall back to a plain unicode `KEY` send (which won't trigger
   the shortcut).
4. **`asInvoker`, not `highestAvailable`, in `app.manifest`.** Deliberate — chosen so the
   app doesn't UAC-prompt on every launch. Means it can't send input into an
   elevated-process window unless the user manually runs it as administrator. See
   `README.md`'s "Elevated windows" note.
5. **OPEN BUG, not yet root-caused: real device connects (green dot) but trackpad/keyboard
   had no visible effect on the PC.** The `InputInjector`/`SendInput` path itself is
   proven working (a raw hand-crafted `MOVE` packet sent straight to the live process
   moved the cursor - see §0's 2026-08-14 diagnostics entry), so the gap is somewhere
   between real touch input and what actually leaves the phone, or possibly Firewall
   (§ item 6) silently eating the TCP payload despite the handshake having completed.
   The console output (`TrayApp.cs`, printed live to whatever terminal ran `dotnet run`)
   was added specifically to pin this down next: if nothing shows there at all while
   touching the trackpad, the phone isn't sending; if `?? unrecognized: ...` lines show
   up, it's sending but malformed; if clean `MOVE`/`KEY` lines show up and *still*
   nothing happens on screen, the bug is genuinely in `InputInjector`. Don't assume this
   is fixed just because the code changed - it hasn't been re-tested against a real touch
   gesture on the device since logging was wired up (only a raw hand-crafted TCP packet
   has been confirmed to log + dispatch correctly, and the phone's background
   auto-reconnect + PING was confirmed to reach the log - see §0's two 2026-08-14
   entries above for exactly what's been verified vs. not).
6. **Windows Firewall will prompt on first `Server.Start()`.** Both TCP 5201 and UDP
   58201 need to be allowed on the private-network profile, or discovery/connect will
   silently fail with no error surfaced to the user beyond "Discovery failed" on the
   phone. Worth checking directly (`Get-NetFirewallRule` / Windows Security → Firewall →
   Allowed apps) if item 5 above is still unresolved - a rule that allows the TCP accept
   but not the ongoing data, or one scoped to the wrong profile, could plausibly explain
   "shows connected, does nothing."

---

## 6. Building

### Windows

```powershell
cd Windows
dotnet build
```

Standalone exe: `dotnet publish -c Release -r win-x64 --self-contained false`.

### Android

```powershell
cd Android
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'
.\gradlew assembleDebug
```

`JAVA_HOME` must be set — no system JDK on this machine, only Android Studio's bundled
JBR (JDK 21). Output: `app/build/outputs/apk/debug/app-debug.apk`. Copy it to the repo
root as `RemoteControl-debug.apk` after any Android change, same convention as
PhoneTrack used.

Install straight to a USB-connected phone (preferred over handing over the APK):

```powershell
"C:\Users\anime\AppData\Local\Android\Sdk\platform-tools\adb.exe" install -r "Android\app\build\outputs\apk\debug\app-debug.apk"
```
