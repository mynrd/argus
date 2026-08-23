# Argus

Watch your Windows desktop apps from a browser. Pick which apps to monitor, see them as live
preview tiles, open one full-screen, and type into it remotely.

Built to be reached from a phone or tablet over Tailscale.

---

## Quick start

```powershell
.\run.ps1
```

Then open the URL it prints. That is it - one command, one process.

```powershell
.\run.ps1 -Dev          # Angular dev server + hot reload on http://localhost:4200
.\run.ps1 -Port 8080    # different port
.\run.ps1 -SkipBuild    # run what is already compiled
```

Requires the .NET 10 SDK and Node.js. First run installs npm packages automatically.

---

## How it is put together

One process, not two.

```
   Browser                          Argus.Server (your desktop session)
  ---------                        ------------------------------------
   Angular  ──── SignalR ───────►   ArgusHub        control: window list, attach,
            ◄─── /hubs/argus ────                   subscribe, status, keystrokes

            ◄─── WebSocket ──────   CaptureSession  one capture thread per window
                 /ws/frames         (binary JPEG)   GDI PrintWindow ─► JPEG ─► fan-out

                                    InputRouter     PostMessage / SendInput / WriteConsoleInput
                                    WatchdogService health loop, re-attach, heartbeat
```

The original plan had a separate agent process talking to a separate web server. They are merged.
Both halves have to run on the machine being watched anyway, so splitting them would only add a
hop, a second reconnect path, and an offline-detection problem - all for nothing.

**It must run as a normal user process, not a Windows service.** Services run in session 0, which
has no access to the interactive desktop; window enumeration, capture and input injection all fail
there. Use the startup folder or a scheduled task set to "run only when the user is logged on".

### Why two connections

SignalR carries control messages. Frames go over a separate raw WebSocket.

SignalR's JSON protocol base64-encodes `byte[]`, which adds ~33% to every frame of every tile, and
its MessagePack protocol pulls in a package with open security advisories. A dedicated binary
socket avoids both, and keeps a burst of frames from delaying a keystroke.

Frame wire format - a 16-byte header, then raw JPEG:

| offset | type | meaning |
|--------|------|---------|
| 0 | int64 | window handle |
| 8 | int32 | sequence number |
| 12 | uint16 | width |
| 14 | uint16 | height |
| 16 | … | JPEG bytes |

---

## Capture

`PrintWindow` with `PW_RENDERFULLCONTENT`, encoded to JPEG with `System.Drawing`.

Measured on Windows 11 26200, capturing **fully occluded background windows** with no bleed-through
from windows on top:

| App | Result |
|-----|--------|
| Notepad++, Paint, File Explorer | works |
| Chrome, Electron, VS Code | works |
| Windows Terminal | works |

This is worth stating because the usual advice is that GPU-composited windows capture black and you
need `Windows.Graphics.Capture` and D3D11 interop. That was not true for anything tested here, so
Argus ships the far simpler GDI path with no D3D dependency. The `IWindowCaptureSource` interface
is the seam to add a WGC backend if some app ever does come back blank - `GdiWindowCaptureSource`
already probes for all-black output and reports it in the UI.

Minimised windows cannot be captured at all; tiles show "Minimised" until the window is restored.

### Quality presets

| Preset | Max size | JPEG | fps | Used for |
|--------|----------|------|-----|----------|
| Preview | 400×300 | 50 | 2 | dashboard tiles |
| Low | 854×480 | 45 | 6 | phones, slow links |
| Medium | 1280×720 | 65 | 15 | default on desktop |
| High | 1920×1080 | 80 | 25 | full-screen detail |

A window is captured once per tick at the fastest rate any viewer asked for, then encoded once per
distinct quality in use. A tile and a full-screen view of the same window cost one capture and two
encodes, not two of each. Frames are never upscaled.

---

## Keyboard input

This is the part with real limits. **All of the following was verified by injecting keys and
screenshotting the result**, not inferred.

| Target | Background mode | Focus mode |
|--------|-----------------|------------|
| Notepad++ (Win32/Scintilla) | works | works |
| Windows Terminal | **silently fails** | works - `ipconfig` ran and returned output |
| Chrome / Electron / VS Code | **silently fails** | works |
| Classic conhost (`ConsoleWindowClass`) | should work via `WriteConsoleInput` - **untested**, no such window existed on the test machine | works |

**Background mode** uses `PostMessage`. It does not steal desktop focus, so the machine stays
usable while you type from your phone. Two things make it work at all:

- The message must go to the *focused child* window, not the top-level frame. Notepad++ types into
  a `Scintilla` child. `GetGUIThreadInfo` returns NULL for a thread that does not own the
  foreground, so Argus attaches to the target's input queue briefly (`AttachThreadInput` +
  `GetFocus`) to find it.
- Printable keys are sent as `WM_CHAR` **only**. Sending `WM_KEYDOWN` as well double-types, because
  controls that run their own key handling synthesise the character themselves.

Background mode also cannot do modifier combos reliably. A posted `WM_KEYDOWN` does not update the
target thread's async key state, so an app calling `GetKeyState(VK_CONTROL)` sees Ctrl as up and
reads Ctrl+C as a plain `c`. Use Focus mode for combos.

**Focus mode** brings the window to the foreground and uses `SendInput`. Fully faithful - real key
state, so Ctrl+C and friends behave exactly as if typed at the machine. The cost is that it owns
the physical desktop while in use.

Note that `PostMessage` returning success means only that the message was *queued*, never that the
app acted on it. Argus therefore refuses background input outright for window classes measured to
discard it, rather than reporting keys as delivered while nothing happens. The Settings screen
badges those apps "Keyboard needs Focus mode" before you ever try.

**Windows Terminal tabs are ConPTY-hosted and have no HWND of their own**, so neither `PostMessage`
nor `WriteConsoleInput` can reach them. Focus mode is the only option there.

`Argus.ConsoleInject.exe` exists because `AttachConsole` is process-wide: doing it in-process would
detach the server from its own console and kill its logging. One helper is kept alive per target
console and fed over stdin.

### Allow Type

The floating key pad has an **Allow Type** switch, on by default.

On, the pad works as it always has: opening it foregrounds the app on the host and arms this
device's keyboard, so whatever you type here is forwarded. That is what you want on a phone, where
the pad and the soft keyboard turn up together.

Off, the pad is only its buttons. The keyboard in front of you stays the browser's, and text
reaches the app through Send Text instead. This is the setting for a desktop next to a live viewer,
where a real keyboard otherwise types into whatever is open on the watched machine by accident.
Either way the pad's own keys still reach the host, so Esc, the arrows and Ctrl combos are a tap
away. The choice is remembered per device.

### Send Text

The viewer's toolbar - the one that stays on screen in full screen - has a **Send Text** button. It
opens a box, and Send types the whole block into the window at once, with a **Hit Enter** checkbox
for finishing on Enter.

This exists because typing live from a phone is one hub round trip and one `SetForegroundWindow`
per character, with a soft keyboard over the picture while you do it. A URL or a command line is
easier to compose in a box, check, and then hand over in one go - and one `SendInput` batch cannot
be split between two windows by something stealing the foreground half way through.

Line breaks and tabs in the text are sent as the real Enter and Tab keys rather than as characters,
because a literal `\n` does nothing in a shell and shows as a box in a text field. CRLF counts as
one Enter, and text that already ends in a newline is not given a second one by the checkbox. Other
control characters are dropped. Text goes through the Focus backend whatever the window is, and is
capped at 10,000 characters per send.

---

## Health loop

`WatchdogService` runs every 2 seconds and:

- marks a closed app dead, and keeps watching for it to reopen
- re-attaches to the new HWND when it does, keeping the same tile and subscribers
- attaches selected apps that were not running at startup
- rebuilds a capture source that has gone silent for 6s while someone is watching
- pushes live status and frame rate, and a heartbeat so the browser can show "agent offline"

Selections persist by `processName|windowTitle`, not by HWND, so they survive both an Argus restart
and the target app being closed and reopened. Stored in `%LOCALAPPDATA%\Argus\selection.json`.

---

## Access and security

Argus injects keystrokes into your desktop and **has no authentication**.

It binds to loopback plus your Tailscale address only - detected automatically from the
`100.64.0.0/10` CGNAT range. It never binds `0.0.0.0` by default, because that would expose desktop
control to every network the machine ever joins.

Override only if you mean it:

```powershell
.\run.ps1 -Urls "http://0.0.0.0:5227"     # don't do this on untrusted wifi
```

`tailscale serve` in front of Argus gets you HTTPS on the tailnet if you want it.

---

## Tests

```powershell
dotnet test                        # 133 tests
cd src\Argus.Web && npx ng test    # 4 tests
```

The .NET suite covers key mapping, text injection, quality presets, window filtering and re-attach
matching, the frame wire format, Tailscale address detection, JPEG encoding, and client-registry identity. It
also includes end-to-end tests that launch the real server and drive it exactly as the browser does
- SignalR plus a raw WebSocket - asserting that frames actually arrive, that changing quality
changes the frame size, and that detaching stops the stream.

To exclude the end-to-end tests:

```powershell
dotnet test --filter "Category!=EndToEnd"
```

---

## Layout

```
src/Argus.Server/          ASP.NET Core host - capture, input, hub, frame socket, serves the UI
  Capture/                 capture sources, sessions, JPEG encoding, quality presets
  Input/                   key mapping and the three injection backends
  Streaming/               frame wire format, client sockets, registry
  Windows/                 window enumeration and filtering
  Services/                watchdog, selection persistence, network binding
src/Argus.ConsoleInject/   out-of-process WriteConsoleInput helper
src/Argus.Web/             Angular 22 front end (builds into Argus.Server/wwwroot)
src/Argus.Android.App/     Kotlin + Compose phone client - see its README to build the APK
tests/Argus.Server.Tests/  unit + end-to-end tests
```
