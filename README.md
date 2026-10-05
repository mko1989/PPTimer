# PPTimer

Countdown timer drawn on top of PowerPoint's **presenter view** (Windows), or Keynote's / PowerPoint's
presenter view on a **Mac** (see [Mac version](#mac-version)), controlled over the network by Bitfocus
Companion. The audience screen never shows it.

```
Companion ──WebSocket──►  PPTimer add-in (inside POWERPNT.EXE)
                            ├─ HTTP/WebSocket API on :9595
                            └─ overlay window owned by the presenter view window
```

## Layout

| Path | What |
| --- | --- |
| `addin/PPTimer/Core/` | Timer, settings, JSON, HTTP + WebSocket server. Platform-neutral. |
| `addin/PPTimer/Windows/` | COM add-in entry point, presenter view detection, overlay window. |
| `addin/DevServer/` | Runs `Core` on macOS so you can work on Companion without Windows. |
| `addin/scripts/` | `install` / `uninstall` (.cmd wrappers + .ps1). |
| `mac/` | Mac menu bar app (Swift). Same API and web pages; see [Mac version](#mac-version). |
| `companion-modules/companion-module-pptimer/` | Companion module (base 2.x, needs Companion 4.3+). |
| `build.sh` | Builds the add-in and produces `dist/PPTimer-win/` + `.zip`. |

### Why a COM add-in rather than a VSTO project

VSTO is a wrapper around the same COM add-in mechanism (`IDTExtensibility2` plus registry
keys). Its project system only builds in Visual Studio on Windows. This project uses the plain
.NET SDK, so it builds on the Mac with `dotnet build` and has no VSTO runtime to install. It
has the same capabilities.

## How it works

- **Finding the presenter view.** Every 400 ms (on PowerPoint's UI thread) it looks through the
  PowerPoint process's windows and takes the first match of:
  1. a visible top-level window whose class is in `presenterWindowClasses` (default `PodiumParent`),
  2. a visible *child* window with such a class (in case a PowerPoint build nests it),
  3. a visible window whose title contains one of `presenterWindowTitles` (default `Presenter View`),
  4. during a slide show, the one other `screenClass` window besides the audience window.

  The audience window comes from PowerPoint's object model (`SlideShowWindow.HWND`) and is never
  used, whatever the config says. While attached it re-checks every 2 s. This also covers Alt+F5,
  "Swap displays" and moved or resized windows.
- **Drawing the overlay.** A borderless window whose *owner* is the presenter view, so it
  always sits above it and nothing else. It is painted with per-pixel alpha
  (`UpdateLayeredWindow`), so the background can be fully transparent: only the digits, with an
  optional dark outline. It never takes focus, so the clicker and arrow keys still work, and
  clicks pass through it by default.
- **Display page.** `http://PC:9595/display` shows only the timer on black (same colours and
  blink as the overlay) with a fullscreen button bottom-left, e.g. for a confidence monitor or tablet.
- **Position.** Stored as a rectangle in % of the presenter view. Drag it on the web page
  (`http://PC:9595/`), where the presenter view is drawn as a plain box with its real proportions.
- **Sound.** When a running countdown reaches zero, it plays `soundFile` (a .wav on the PowerPoint
  PC) or a built-in triple beep. It uses the PC's default audio output, which at a venue may be
  the PA, so it is off by default.
- **Timing.** The countdown runs off a monotonic clock inside the add-in. Network messages
  are only commands, so a dropped message never makes the timer drift.
- **At zero.** Optionally blinks (a smooth 2 s fade), then counts up (`00:15`, or `-00:15` with `showMinus`), or
  stops at `00:00` if `countUp` is off. The colour at zero is red, or amber if red is off.

## Build (Mac)

```sh
brew install --cask dotnet-sdk        # once
./build.sh                             # -> dist/PPTimer-win/ and dist/PPTimer-win.zip
./build.sh dev                         # dev server + browser remote on http://localhost:9595/
```

## Install / update (Windows)

1. Copy `dist/PPTimer-win` to the laptop, via a network share, USB or the zip.
2. Close PowerPoint, then double-click **`install.cmd`**. Re-run it after every build. It installs the
   add-in for the current user, then checks network access. The first time, Windows asks for admin
   permission so it can add the URL reservation and a firewall rule for TCP 9595. Later updates skip
   that step. If you decline, the API only answers on `localhost`; run `install.cmd` again to retry.
   `uninstall.cmd` removes both the add-in and the network access.
3. Start PowerPoint. Check **File → Options → Add-ins → Manage: COM Add-ins → Go…**: PPTimer
   should be listed and ticked.

Files live in `%LOCALAPPDATA%\PPTimer\`: `bin\`, `config.json` and `pptimer.log`.

## API

All commands work as `GET` or `POST /api/<cmd>`. Arguments go in the query string, a JSON
body or a form body. Over WebSocket (`/ws`), send `{"cmd": "<cmd>", ...args, "id": "optional"}`.

| Command | Args | |
| --- | --- | --- |
| `start` `pause` `toggle` | | |
| `reset` | | back to duration, paused |
| `restart` | | back to duration, running |
| `set` | `time=5:00` or `seconds=300` or `minutes=5`, optional `start=true/false` | new duration |
| `add` | `time=-1:00` / `seconds=30` | adds or removes time |
| `speed` | `percent=105` or `rate=1.05`, or `step=5` / `step=-5` | how fast the countdown runs, 50–200 % of real time (see below) |
| `show` `hide` `togglevisible` | | overlay visibility |
| `settings` | any runtime key below | no args returns current settings |
| `testsound` | | plays the zero sound once |
| `state` | | `GET /api/state` |

Diagnostics (same token rules as the API):
- `GET /api/debug/windows`: PowerPoint's windows (class, title, rect, monitor, child classes), running slide shows
  with their audience window and "Use Presenter View" setting, monitors, and which window the overlay is attached to and why.
- `GET /api/debug/log` (Windows): the last 200 KB of `pptimer.log` as text (`?kb=1000` for more). Ask whoever reports a
  problem to open `http://PC-IP:9595/api/debug/log` and save the page, rather than photographing the screen.

WebSocket pushes:
- `{"type":"hello",...}` on connect.
- `{"type":"state", display, phase, running, remainingMs, remainingSeconds, durationMs, duration, progress, visible, presenterView, overtime, speedPercent, ...}` on every visible change, plus a heartbeat every 5 s.
- `{"type":"result", id, ok, error}` for each command.
- `{"type":"settings",...}` when settings change.
- `{"type":"event","event":"zero"}` when a running countdown reaches zero.

`phase` is one of `normal`, `warning`, `critical` or `expired`.

**Speed.** `speed` makes the countdown run faster or slower than real time, so an operator can quietly
shorten (or stretch) a talk. At 105 %, 10:00 lasts 9:31; at 95 %, 10:31. The time already elapsed is
kept; only the rest runs at the new speed. The overlay and `/display` show only the time. The speed
appears on the remote page, in the Mac menu, in `speedPercent` and in Companion. `set`, `reset` and
`restart` go back to 100 %, so a new segment never inherits the previous one's speed-up.

Note: a bare `curl -X POST` with no body gets `411 Length Required`. Use GET, or add `-d ''`.

```sh
curl "http://PC:9595/api/set?time=10:00&start=true"
curl "http://PC:9595/api/add?seconds=-30"
curl -d '{"xPercent":30,"yPercent":75,"soundEnabled":true}' http://PC:9595/api/settings
```

## config.json

| Key | Default | Runtime? | |
| --- | --- | --- | --- |
| `port` | 9595 | no | re-run `install.cmd` after changing |
| `apiToken` | "" | no | if set: `X-Api-Token` header or `?token=` |
| `presenterWindowClasses` | `["PodiumParent"]` | no | window class(es) to attach to, strongest first. Don't add `PPTFrameClass` (editing window) or `screenClass` (slide show) |
| `presenterWindowTitles` | `["Presenter View"]` | no | title fragments that identify the presenter view; add the translation for a localised PowerPoint |
| `clickThrough` | true | no | overlay ignores the mouse |
| `xPercent` / `yPercent` | 22 / 74 | yes | overlay top-left corner, % of the presenter view |
| `widthPercent` / `heightPercent` | 16 / 9 | yes | overlay size; the digits are fitted inside |
| `opacity` | 1 | yes | 0.2 – 1 |
| `transparentBackground` | true | yes | false = solid rounded box |
| `textOutline` | true | yes | dark outline around the digits |
| `warnEnabled` / `warnSeconds` | true / 180 | yes | amber from 3:00 |
| `criticalEnabled` / `criticalSeconds` | true / 60 | yes | red from 1:00 |
| `blinkAtZero` | true | yes | |
| `countUp` | true | yes | false = stop at 00:00 |
| `showMinus` | false | yes | `-00:15` instead of `00:15` while over time |
| `soundEnabled` | false | yes | |
| `soundFile` | "" | yes | .wav path on the PowerPoint PC; empty = built-in beep |
| `defaultDurationSeconds` | 300 | (auto) | last `set` value, restored at startup |

"No" means restart PowerPoint after editing the file.

## Companion module (dev)

```sh
cd companion-modules/companion-module-pptimer
corepack yarn install
```

In the Companion launcher's settings, set **Developer modules path** to
`…/PPtimer/companion-modules`. Then add a connection of type *pptimer* with the PC's IP and
port 9595. Presets are under *Timer control*, *Adjust time*, *Features on / off* and *Set duration*. For
development without Windows, point it at `localhost` with `./build.sh dev` running.

## Mac version

A menu bar app (`PPTimer.app`) instead of an add-in. It has the same API on port 9595, the same web
pages and the same `config.json` keys, so the Companion module works unchanged: point it at the
Mac's IP address. The menu bar shows the countdown (◉ = on a presenter view, ○ = none) and has
start/pause, reset, ±1 minute, show/hide, the remote URL and *Open at Login*.

```sh
./build.sh mac          # -> dist/PPTimer-mac/PPTimer.app and dist/PPTimer-mac.zip (universal)
./build.sh mac run      # build, quit the running copy, start the new one
```

Copy `PPTimer.app` to `/Applications` and open it. Files live in
`~/Library/Application Support/PPTimer/` (`config.json`, `pptimer.log`).

**Finding the presenter view** (polled 4× a second):
- **Keynote**: no permission needed. While a slideshow plays on two displays, the audience slides are
  a full-display window at window level 25, and the presenter display is a full-display window at
  level 9 on the other display. That is what PPTimer looks for. Playing on one display, or with
  mirrored displays, has no presenter display, so nothing is shown.
- **PowerPoint**: PPTimer asks PowerPoint for `bounds of every presenter view window` over Apple
  Events. The first time PowerPoint is running, macOS asks *"PPTimer wants access to control
  Microsoft PowerPoint"*: click **Allow**. If you clicked Don't Allow, the menu shows *Allow PowerPoint
  Access…* (System Settings → Privacy & Security → Automation → PPTimer → Microsoft PowerPoint).

**The overlay** is a borderless panel at screen-saver level on the presenter view's display. It never
takes focus, so the clicker and arrow keys still go to Keynote/PowerPoint, and clicks pass through it.

**Signing.** macOS remembers the PowerPoint permission per code signature, so `mac/scripts/sign.sh`
signs with a self-signed certificate kept in its own keychain
(`~/Library/Keychains/pptimer-signing.keychain-db`, created on the first build and added to your
keychain search list). Rebuilds keep the permission. It is not notarized: on another Mac,
right-click → **Open** the first time (or System Settings → Privacy & Security → *Open Anyway*). Set
`PPTIMER_SIGN_IDENTITY` to sign with a real Developer ID instead.

Mac differences in `config.json`: no `presenterWindowClasses`. `soundFile` can be any file macOS plays
(.wav, .aiff, .mp3, .m4a), and `~` is allowed. `GET /api/debug/windows` lists the displays and
Keynote/PowerPoint windows with their levels, which is useful if a new Keynote version changes them.

## Troubleshooting

- **Add-in not listed or not loading.** Check `pptimer.log`. If there's no log at all, the
  DLL never loaded: re-run `install.cmd`, and look under COM Add-ins and
  *Disabled Items* (File → Options → Add-ins → Manage: Disabled Items).
- **Overlay doesn't appear.** Get the log (`http://PC-IP:9595/api/debug/log`, or `pptimer.log`). It has:
  - at startup: PPTimer, PowerPoint and Windows versions, the detection settings, the monitors, and warnings for
    suspicious `presenterWindowClasses`;
  - "Slide show started: …" with the audience window handle and `usePresenterView` (False means the
    *Slide Show → Use Presenter View* box is off, so there is nothing to find);
  - "PowerPoint visible windows changed: …" with one line per window (handle, class, title, position, monitor,
    `AUDIENCE` for the slide show window) and its child window classes;
  - "Presenter view found: … via <rule>" and "Overlay shown at …" when it works, or after 3 s of a slide show
    "WARN Slide show running … but no presenter view found" with a likely cause and a full window dump.
- **config.json edited by hand and ignored.** The log says "Could not read config.json: … at line L, column C" and
  copies the file to `config.invalid.json` before anything can overwrite it. Lists are flat:
  `"presenterWindowClasses": ["PodiumParent", "Other"]`, not `["PodiumParent"],["Other"]`.
- **Companion can't connect.** Open `http://PC-IP:9595/` in a browser from the Companion
  machine. If that fails, check the log for "localhost only" and run `install.cmd` again, accepting the admin prompt.
