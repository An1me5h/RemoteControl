# ForClaudeUseOnly.md — RemoteControl

> Internal map of this repo so the whole tree does not need re-reading.
> Verified against source on 2026-08-14. The crash fix is device-verified; TEXT, COMBO,
> and custom keys (§0's two most recent entries, §5 items 7-8) are build-verified only -
> user asked to stop automating the phone, see those entries for why.

---

## 0. Branch log

Not a git repo yet (no `git init` run here) — no branches to log against. If this gets
git-initialized later, start logging changes here per branch as work happens.

### 2026-08-14 — real multi-key combos + user-defined custom keys

User asked for two things: (1) multi-key combos ("Ctrl + T... it should work like a real
keyboard also it should work for multiple types of combination") and (2) a way to define
named macro buttons ("create a custom key, in that which key combination to add, and when
pressed the custom key will press the combination of keys... provision to add multiple").

**On (1)**: holding a modifier (`VkDown`) and then tapping another key already produces a
correct real-OS combo with the *existing* protocol, because `SendInput`/Windows keyboard
state is global, not scoped per call - a modifier's key-down from one `SendInput` call is
still "held" as far as the OS is concerned when a later, separate `SendInput` call presses
another key, exactly like a physical keyboard reporting keys in sequence. That part needed
no fix. What it doesn't give you is a **one-tap** combo without the hold/tap/release
dance, which is really what "create a custom key" (2) is asking for - so the actual new
work is a proper atomic primitive plus the custom-key UI built on it, not a bug fix.

Added:
- **New packet type `COMBO`** (`{"t":"COMBO","keys":[17,84]}` for Ctrl+T, values are
  Windows VK codes) - both sides. `InputInjector.Combo(int[])` (Windows) sends all
  keys-down (in order) then all keys-up (reverse order) as **one `SendInput` call**, not a
  sequence of separate dispatches - this is deliberately more atomic than the existing
  hold-a-modifier-then-tap approach, so it's the more "real keyboard" - faithful of the
  two, which is why custom keys are built on it rather than just automating the
  hold/tap/release sequence over the wire.
- **`Android/.../CustomKeys.kt`** (new file) - `CustomKey(label, keys)` data class,
  `CustomKeyStore` (load/save as one JSON array in `SharedPreferences`), `KeyCatalog` (the
  46-entry A-Z/0-9/special-key list offered in the add-dialog's spinner).
- **`Android/.../res/layout/dialog_custom_key.xml`** (new file) - the add-custom-key
  dialog: name field, 4 modifier checkboxes, a Spinner for the final key.
- **`MainActivity.kt`** - `setupCustomKeys()`/`renderCustomKeys()`/
  `showAddCustomKeyDialog()`/`confirmDeleteCustomKey()`. Custom-key buttons are built in
  Kotlin, not inflated from XML, which surfaced a real gotcha worth remembering: a style's
  `layout_*` attributes (width/height/weight/margin) do **not** apply to a
  programmatically-constructed view just because you pass the style as `defStyleRes` in
  the 4-arg `Button(...)` constructor - only View-level attributes (background,
  textColor, ...) come along for free; `LayoutParams` still has to be set explicitly in
  code (see `dp()` + the explicit `LinearLayout.LayoutParams(0, dp(44), 1f)` in
  `renderCustomKeys`). Missing this would have shipped custom-key buttons that render
  unstyled-looking (wrap-content sized, no margin) instead of matching the rest of the
  keyboard.

Both sides rebuilt clean. **Not touched on a real device** - same situation as the
previous entry below (user said to stop automating the phone right after confirming the
crash fix), so this is build-verified only. In particular the `Button(this, null, 0,
R.style.KeyButton)` + explicit `LayoutParams` combination for custom-key buttons has not
been visually confirmed to actually render correctly - reasoned through carefully, but
reasoning isn't the same as looking at it.

### 2026-08-14 — fixed the real crash, added type-and-send text

User reported "the app keep stopping." Captured a real crash via
`adb logcat -d -v time AndroidRuntime:E` while the app was in this state and found two
things, both the same root cause:

1. An ANR: `Input dispatching timed out ... Waited 5001ms for MotionEvent`.
2. `FATAL EXCEPTION: android.os.NetworkOnMainThreadException` at
   `ConnectionManager.send(ConnectionManager.kt:91)`, called from
   `TrackpadView.onTouchEvent` → `MainActivity.setupTrackpad$lambda`.

**Real root cause**: `send()` wrote to the socket (`PrintWriter.println`, which flushes)
synchronously on whatever thread called it. For trackpad drags and key taps that's the UI
thread, and Android hard-blocks network I/O there. This bug **predates today** - it was
already present when `send()`'s `try/catch` silently swallowed it (that's the actual
explanation for the "queues forever, connected=true" symptom investigated earlier the same
day, further down this log - not a dead/zombie `PrintWriter` connection as diagnosed at
the time). Removing that catch (to add `checkError()`-based dead-connection detection,
also earlier the same day) was independently correct, but it also removed the accidental
safety net, so the exact same underlying bug started crashing the app outright instead of
silently queueing.

**Fix**: added a second dedicated single-thread executor, `writeExecutor`, in
`ConnectionManager.kt`. `send(packet)` now only ever does
`writeExecutor.execute { processOne(packet) }` - it hands off and returns immediately, so
it's safe to call from the UI thread. `processOne` is the *only* place that ever touches
`writer`/`offlineQueue`; `drainOfflineQueue` also submits its work to the same executor
(rather than running inline on the connect-thread as before) so a live `send()` arriving
mid-drain simply waits its turn on the executor instead of racing the drain loop for the
socket. `drainOfflineQueue` now stops immediately on the first failed `processOne` instead
of continuing to loop - looping after `writer` goes null would otherwise just pop-and-
immediately-requeue every remaining item forever without making progress (net queue size
never shrinks in that state, since each iteration removes-then-re-adds one item).

Verified via a deterministic, non-interactive test rather than asking the user to trigger
it: force-stopped and relaunched the app, drove it entirely through `adb shell input tap`/
`swipe` (coordinates found via `uiautomator dump`) to connect and drag the trackpad while
capturing `adb logcat`. Result: 195/195 packets logged `sent`, zero failures, zero crashes
- same process PID throughout. Cross-checked against the PC side importantly: an
`Get-NetTCPConnection` during this same window showed an established connection on 5201
with **identical local and remote IP** (`192.168.178.125` both sides) - unexplained, flagged
but not chased since the logcat evidence (matching swipe deltas appearing as `sent` in
real time) was independently conclusive that the phone->PC path works; if a future session
hits something that circles back to this, it's worth a proper look with `Get-Process
-Id <OwningProcess>` on that connection to see what actually owns it.

**New feature added same session, once the crash was fixed and confirmed**: type-and-send
text. User wanted to type with the phone's real keyboard in a text box and send the whole
block at once ("like a copy paste") instead of one `KEY`/`VKTAP` packet per character via
the custom on-screen keyboard. Added:
- New packet type `TEXT` (`{"t":"TEXT","text":"..."}`, JSON-escaped via `JSONObject` on
  the Android side same as `KEY`) - both `Packet.cs`/`PacketCodec.Decode` (Windows) and
  `Packet.kt` (Android).
- `InputInjector.SendText(string)` (Windows) - normalizes `\r\n`/`\r` to `\n`, then walks
  each character: `\n` → `KeyTap(VK_RETURN)`, everything else → the existing `SendChar`
  (same `KEYEVENTF_UNICODE` path as single-character `KEY` packets, deliberately **not** a
  clipboard `Ctrl+V` - keeps working in fields that block paste, e.g. some password boxes).
- Android UI: third state for the CONTROL tab's centre panel (`CenterPanel.PAD/KEYS/TEXT`
  in `MainActivity.kt`, generalized from the old two-state trackpad/keyboard toggle), a new
  `TEXT` button next to `KEYS` in the click-button row, and `textPanel` in the layout - a
  multiline `EditText` + `Send` button. Send clears the box after sending.

Both sides rebuilt clean. Not yet interactively confirmed against a real device by
anyone (the crash-fix verification above covers the crash itself, not this new feature) -
**user asked to stop automating the phone for now ("it is working correctly, do not use
this app anymore i will test, only use it when i tell you to")**, so this is unverified
beyond compiling. Do not touch the device again until asked.

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
            │   ├─ Packet.kt
            │   └─ CustomKeys.kt         CustomKey data class, SharedPreferences persistence, spinner catalog
            └─ res/
                ├─ layout/activity_main.xml, dialog_custom_key.xml
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

**PacketFormat.cs** (56 lines) — `Packet` → human-readable line, printed by `TrayApp` via
`Console.WriteLine`.

| Line | Member | Notes |
|------|--------|-------|
| 4 | `static class PacketFormat` | |
| 6 | `KnownVk` | vk-code → friendly name (Ctrl, Alt, Enter, arrows, ...) for the log |
| 13 | `Describe(Packet p)` | one line per packet type |
| 34 | `DescribeMove(Packet p)` | shows raw `dx/dy` **and** magnitude+angle - the "point/angle/value" vector view, easier to eyeball than two separate deltas |
| 41 | `VkName(int k)` | falls back to `0xNN` for anything not in `KnownVk` |
| 43 | `DescribeText(Packet p)` | truncates to 60 chars for the log line, shows total char count; `\n` shown literally as `\n` so a multi-line paste stays one log line |
| 51 | `DescribeCombo(Packet p)` | joins `VkName` for every key with `+`, e.g. `Ctrl (0x11) + T (0x54)` |

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

**Packet.cs** (82 lines) — wire-format decode.

| Line | Member | Notes |
|------|--------|-------|
| 5 | `enum PacketType` | `Move, Scroll, LClick, RClick, MClick, LDown, LUp, Key, VkDown, VkUp, VkTap, Text, Combo, Ping, Unknown` |
| 11 | `readonly record struct Packet(...)` | includes `string? Text` (`TEXT`) and `int[]? Keys` (`COMBO`) |
| 21 | `static class PacketCodec` | |
| 25 | `Decode(string line)` | real `System.Text.Json` parse, not string matching; `keys` parsed via `JsonValueKind.Array` + `EnumerateArray()` |

**InputInjector.cs** (202 lines) — `SendInput` P/Invoke, dispatches decoded packets to OS input.

| Line | Member | Notes |
|------|--------|-------|
| 5 | `enum MouseButtonKind` | |
| 8 | `static class InputInjector` | |
| 10 | `Dispatch(Packet p)` | the one switch every packet type goes through |
| 34 | `Combo(int[] vks)` | all keys down (array order) then all up (reverse order) as **one `SendInput` call** - what makes a custom key an atomic real combo rather than a hold-then-separately-dispatched-tap sequence |
| 52 | `SendText(string text)` | types a whole block at once (Send button on the phone) - normalizes `\r\n`/`\r` to `\n`, then per char: `\n` → `KeyTap(VK_RETURN)`, else → `SendChar`. Deliberately per-character `KEYEVENTF_UNICODE`, not a clipboard `Ctrl+V` - keeps working in fields that block paste |
| 64 | `MoveRelative(double dx, double dy)` | relative gesture in, **absolute** `SendInput` out (normalized 0–65535 over the virtual desktop) — bypasses the pointer-acceleration curve so Wi-Fi/TCP timing jitter doesn't distort the delta-to-pixel mapping |
| 91 | `Scroll(int notches)` | `notches * 120` = one wheel click |
| 97 | `Click(MouseButtonKind)` | down+up in one `SendInput` call |
| 104 | `ButtonState(MouseButtonKind, bool down)` | drag hold/release |
| 110 | `SendChar(char c)` | `KEYEVENTF_UNICODE` — layout-independent |
| 117 | `KeyState(ushort vk, bool down)` | modifier hold/release |
| 120 | `KeyTap(ushort vk)` | full press+release |
| 126 | `FlagsFor(MouseButtonKind)` | |
| 134 | `MouseInput(...)` / 140 `KeyInput(...)` | `INPUT` struct builders |
| 201 | `SendInput(...)` | `[DllImport("user32.dll")]` |

### Android — `Android/app/src/main/java/com/remotecontrol/`

**Packet.kt** (75 lines) — mirrors the Windows wire format.

| Line | Member | Notes |
|------|--------|-------|
| 7 | `sealed class Packet` | |
| 10–32 | `Move, Scroll, LeftClick, RightClick, MiddleClick, LeftDown, LeftUp, Ping, Key, VkDown, VkUp, VkTap` | `Key` uses `JSONObject` for correct char escaping; the rest are raw string templates |
| 36 | `data class Text(val text: String)` | whole-block send for the TEXT panel's Send button; `JSONObject` escaping like `Key` |
| 45 | `data class Combo(val keys: List<Int>)` | atomic multi-key press for custom keys; `JSONArray` for the `keys` field |
| 51 | `object VK` | Windows virtual-key constants; `forChar(Char)` maps `'A'..'Z'`/`'0'..'9'` to their VK codes (ASCII-aligned) for modifier combos |

**CustomKeys.kt** (59 lines) — user-defined macro buttons: label + ordered VK list, sent
as one `Packet.Combo`.

| Line | Member | Notes |
|------|--------|-------|
| 9 | `data class CustomKey(val label: String, val keys: List<Int>)` | |
| 13 | `object KeyCatalog` | the add-dialog spinner's 46 entries: Enter/Tab/Esc/Space/Backspace/Delete/arrows, then `'A'..'Z'`, then `'0'..'9'` |
| 31 | `object CustomKeyStore` | |
| 34 | `load(SharedPreferences): List<CustomKey>` | one JSON array under a single pref key; any parse failure just returns `emptyList()` |
| 49 | `save(SharedPreferences, List<CustomKey>)` | overwrites the whole array - no per-key storage |

**ConnectionManager.kt** (238 lines) — TCP client, UDP discovery, auto-reconnect,
latency, offline queue. **Every socket write happens on `writeExecutor`, never the
caller's thread** - see the 2026-08-14 crash-fix entry in §0 for why this matters
(`send()` is called from the UI thread for trackpad/keyboard input, and Android throws
`NetworkOnMainThreadException` for network I/O there).

| Line | Member | Notes |
|------|--------|-------|
| 20 | `class ConnectionManager` | no `Context` needed - discovery uses plain UDP broadcast, not multicast, so no `MulticastLock`/`WifiManager` dependency (unlike a typical mDNS-based design) |
| 22 | `enum class State` | `DISCONNECTED, CONNECTING, CONNECTED` |
| 68 | `connect(host: String?, port: Int)` | `host == null` → auto-discover |
| 73 | `disconnect()` | clears `offlineQueue` too - user-initiated, the backlog is no longer relevant |
| 85 | `send(Packet)` | callable from **any** thread - only ever does `writeExecutor.execute { processOne(packet) }`, never touches the socket itself |
| 100 | `processOne(Packet): Boolean` | the *only* place that calls `writer.println()` or touches `offlineQueue` - runs exclusively on `writeExecutor`. Writes directly if connected, else queues. `PrintWriter` never throws on a failed write (swallows the `IOException` internally); `checkError()` is what actually detects a dead connection, and on failure force-closes the socket so `readLoop`'s blocked read notices and `connectLoop` reconnects. Returns false if the packet ended up queued instead of sent |
| 119 | `enqueue(Packet)` | caps at `MAX_QUEUED_PACKETS`, drop-oldest on overflow |
| 131 | `drainOfflineQueue()` | submits its own loop to `writeExecutor` - replays the backlog one packet every `DRAIN_INTERVAL_MS` (10ms), called right after reaching `CONNECTED`. Since `writeExecutor` is single-threaded, any `send()` submitted while this runs simply waits its turn, so live input can't jump ahead of the backlog. **Stops immediately on the first failed `processOne`** rather than continuing to loop - looping after `writer` goes null would otherwise just pop-and-immediately-requeue every remaining item forever with the net queue size never shrinking |
| 141 | `connectLoop(...)` | reconnect loop on a single background executor thread (`executor` - separate from `writeExecutor`, see next line); calls `drainOfflineQueue()` right after `setState(CONNECTED)` |
| *(field)* | `executor` vs `writeExecutor` | two *separate* single-thread executors, deliberately. `executor` runs `connectLoop`/`readLoop`, which block for long stretches (reads, reconnect sleeps) - submitting writes there too would starve them until disconnect |
| 177 | `readLoop(Socket)` | paces `PING` every 5s using a **dynamic** `soTimeout` (= time left until next scheduled ping), not a fixed timeout — a fixed timeout would let the actual ping cadence drift up to 2x late in steady state |
| 200 | `discoverHost(port)` | sends `RC_DISCOVER` to `255.255.255.255:58201`, waits for `RC_HOST ...` |
| 221 | `closeSocket()` |
| 231 | `setState(State)` / 235 `log(String)` | both hop to the main thread via `Handler(Looper.getMainLooper())` |

**TrackpadView.kt** (120 lines) — custom `View`, touch → `Packet`.

| Line | Member | Notes |
|------|--------|-------|
| 14 | `class TrackpadView : View` | |
| 19–20 | `onPacket`, `sensitivity` | default `1.4f` |
| 70 | `onTouchEvent(MotionEvent)` | tracks `activePointers` (live) separately from `maxPointers` (gesture-lifetime max) — needed because `GestureDetector`'s tap callbacks fire on a delayed handler message well after `ACTION_UP`, by which point a live pointer count would already have been reset to 0 |
| 114 | `resetOrigin(MotionEvent)` | called on every pointer-count change to avoid a cursor jump from stale `lastX/lastY` |
| (inner) `GestureDetector.SimpleOnGestureListener` | `onSingleTapConfirmed` (finger count → click type), `onDoubleTap`, `onLongPress` (drag start + haptic), `onScroll` (2+ fingers only) |

**MainActivity.kt** (370 lines) — two tabs (plain toggle buttons, not `TabLayout` — no
Material dependency was worth pulling in for a 2-way toggle), CONTROL / CONFIG.

| Line | Member | Notes |
|------|--------|-------|
| 19 | `class MainActivity : AppCompatActivity` | |
| 56 | `onCreate` | |
| 75 | `bindViews()` | |
| 100 | `setupTabs()` / 106 `selectTab(Boolean)` | |
| 113 | `setupTrackpad()` | `trackpadView.onPacket = { conn.send(it) }` |
| 117 | `setupClickButtons()` | LEFT/RIGHT/MID buttons below the pad |
| *(inner)* | `enum class CenterPanel { PAD, KEYS, TEXT }` | the CONTROL tab's centre area is always exactly one of these three - trackpad, on-screen keyboard, or type-and-send text box |
| 128 | `setupPanelToggles()` | KEYS/TEXT buttons each toggle their panel on, or back to PAD if already showing |
| 139 | `showPanel(CenterPanel)` | sets visibility on all three panels + highlights whichever of `btnKeys`/`btnText` is active (same `key_bg_active` drawable as held modifiers) |
| 148 | `setupTextPanel()` | wires `btnSendText` - reads `etTextInput`, sends a `Packet.Text` if non-empty, clears the box |
| 158 | `setupKeyboard()` | wires special-key taps (Esc/Tab/Backspace/Enter/Space/arrows) and the 4 sticky modifier buttons |
| 188 | `toggleModifier(vk, Button)` | Ctrl/Alt/Shift/Win are sticky - tap to hold (`VkDown`, highlighted bg), tap again to release (`VkUp`) |
| 198 | `assignCharKeys(ViewGroup)` | recursive; reads the single char each key carries in `android:tag` |
| 213 | `sendChar(Char)` | if any modifier is held **and** the char has a VK mapping (letters/digits), sends a real `VkTap` instead of a unicode `Key` so the combo (e.g. Ctrl+C) actually reaches Windows; everything else always falls back to plain `Key` - see §0's most recent entry for why this (hold + separately-dispatched tap) is a real combo despite being two dispatches, not one |
| 223 | `setupCustomKeys()` | loads saved `CustomKey`s via `CustomKeyStore`, renders them, wires `btnAddCustomKey` |
| 237 | `renderCustomKeys()` | rebuilds `customKeysContainer` from scratch, 2 buttons/row. Buttons are built in Kotlin (`Button(this, null, 0, R.style.KeyButton)`), **not** inflated from XML - the style only supplies View-level attributes this way; `layout_*` (width/height/weight/margin) still needs explicit `LayoutParams`, set right below via `dp()` |
| 258 | `dp(Int): Int` | dp → px, needed because the custom-key buttons' `LayoutParams` are built in code |
| 260 | `confirmDeleteCustomKey(CustomKey)` | long-press on a custom key - the only way to remove one |
| 272 | `showAddCustomKeyDialog()` | inflates `dialog_custom_key.xml` into an `AlertDialog`; Save builds the `keys` list as `[held modifiers in Ctrl,Alt,Shift,Win order] + [spinner selection]` |
| 307 | `vkLabel(Int): String` | Ctrl/Alt/Shift/Win → name, else `0xNN` - used to auto-generate a label when the name field is left blank |
| 315 | `setupConfig()` | host/port/sensitivity persistence via `SharedPreferences("remotecontrol")`; sensitivity range 0.2–3.2 in 0.1 steps (`SeekBar` progress 0–30) |
| 336 | `setupConnection()` | status dot colour, latency readout, Connect/Disconnect button |
| 366 | `onDestroy()` | disconnects |

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
| `{"t":"TEXT","text":".."}` | TEXT panel's Send button - whole block at once | yes — per-char `KEYEVENTF_UNICODE`, `\n` → Enter key tap |
| `{"t":"COMBO","keys":[..]}` | a tapped custom key | yes — all keys down (array order) then all up (reverse order), **one `SendInput` call** |
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
3. **No symbol-key combos.** Neither the sticky-modifier char-key path (`Packet.VK.forChar`
   only maps `A-Z`/`0-9`, their VK codes equal ASCII) nor the Custom Keys spinner
   (`KeyCatalog` - same A-Z/0-9 plus named special keys, no punctuation) can express e.g.
   Ctrl+`/`. The sticky-modifier path silently falls back to a plain unicode `KEY` send
   for anything `forChar` doesn't cover (which won't trigger the shortcut); Custom Keys
   just doesn't offer the option at all.
4. **`asInvoker`, not `highestAvailable`, in `app.manifest`.** Deliberate — chosen so the
   app doesn't UAC-prompt on every launch. Means it can't send input into an
   elevated-process window unless the user manually runs it as administrator. See
   `README.md`'s "Elevated windows" note.
5. ~~Real device connects but trackpad/keyboard had no visible effect on the PC.~~
   **RESOLVED 2026-08-14.** Root cause was `NetworkOnMainThreadException` - `send()` wrote
   to the socket synchronously on the calling thread, which for trackpad/keyboard input is
   the UI thread, and Android forbids that outright. Fixed by routing every write through
   a dedicated `writeExecutor` in `ConnectionManager.kt`. Verified via a non-interactive
   `adb shell input swipe`-driven test: 195/195 packets sent, zero crashes. Full writeup
   in §0's "fixed the real crash" entry.
6. **Windows Firewall will prompt on first `Server.Start()`.** Both TCP 5201 and UDP
   58201 need to be allowed on the private-network profile, or discovery/connect will
   silently fail with no error surfaced to the user beyond "Discovery failed" on the
   phone.
7. **TEXT (type-and-send) is unverified on a real device.** Added and build-verified only
   - user asked to stop automating the phone right after the crash fix was confirmed
   ("it is working correctly... do not use this app anymore i will test"), so this landed
   after that point and hasn't been clicked through by anyone yet. The underlying
   `SendChar`/`KeyTap` primitives it's built from are proven (used by every other packet
   type already verified working), but the new code paths specifically - `Packet.Text`
   JSON round-trip, `InputInjector.SendText`'s per-character loop and `\n`-to-Enter
   handling, the `CenterPanel` three-way toggle - are not.
8. **COMBO and Custom Keys are unverified on a real device.** Same situation as item 7,
   same reason (user said to stop automating the phone). Specifically unverified: the
   `Combo` atomic multi-`SendInput` path itself (never exercised, not even via a manual
   packet test like TEXT got); the add-custom-key dialog and its Spinner; whether
   dynamically-built `Button`s with manually-set `LayoutParams` actually render correctly
   in `renderCustomKeys()` (reasoned through, not seen); `CustomKeyStore`'s JSON
   round-trip through real `SharedPreferences` (only the data shapes were checked by eye).

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
