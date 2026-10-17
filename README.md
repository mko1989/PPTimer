# PPTimer

Countdown timer drawn on top of the **presenter view** (PowerPoint on Windows; Keynote or PowerPoint on
a Mac) or on **any screen**, controlled over the network by Bitfocus Companion. On the presenter view the
audience never sees it. Both versions are standalone apps (a notification area app on Windows, a menu bar
app on a Mac), so PPTimer runs whether or not PowerPoint is open, and the same API, web pages, settings and
Companion module work with both.

```
Companion ──WebSocket──►  PPTimer.exe / PPTimer.app
                            ├─ HTTP/WebSocket API on :9595 (+ browser remote)
                            └─ always-on-top overlay on the active layout's target:
                               a presenter view, or a screen (optionally blacked out = fullscreen timer)
```

## Layouts

A **layout** is a saved preset: where the timer goes, its rectangle (in % of that area) and its look. One
is active at a time; switch from the tray / menu bar menu, the web remote, Companion or the API.

| Layout (default) | Target | |
| --- | --- | --- |
| **PowerPoint** | PowerPoint's presenter view | |
| **Keynote** | Keynote's presenter display | Mac only |
| **Screen** | the main screen, on top of anything | for other presenter software, a browser, ... |
| **Fullscreen** | the external screen, rest of it black | a confidence monitor driven by this computer |

Create more with **Save as new…** on the web remote (it copies the active layout), then change the
target, screen, position and look; every change is saved into the active layout. A layout's target is
one of `powerpoint`, `keynote`, `presenter` (either app) or `screen`; for `screen`, `screen` is `main`,
`external` (the first display that isn't a laptop's built-in panel), a number (1 = main, then left to
right, as listed on the web remote) or part of the display's name. With mirrored displays there is no
external screen, so a Fullscreen layout shows nothing rather than blacking out the presenter's screen.

## Repository

| Path | What |
| --- | --- |
| `addin/PPTimer/Core/` | Timer, settings and layouts, JSON, HTTP + WebSocket server, web pages. Platform-neutral. |
| `addin/PPTimer/Windows/` | The Windows app: tray icon, presenter view detection, overlay windows, screens. |
| `addin/DevServer/` | Runs `Core` on macOS so you can work on Companion and the web pages without Windows. |
| `addin/scripts/` | `install` / `uninstall` (.cmd wrappers + .ps1). |
| `mac/` | Mac menu bar app (Swift). Same API and web pages; see [Mac version](#mac-version). |
| `companion-modules/companion-module-highpass-pptimer/` | Companion module (base 2.x, needs Companion 4.3+). |
| `build.sh` | Builds the Windows app (`dist/PPTimer-win/` + `.zip`) or the Mac app (`./build.sh mac`). |

(`addin/` is named after the PowerPoint add-in that PPTimer 1.0.x was; `Windows/Connect.cs`,
`IDTExtensibility2.cs` and `AddinHost.cs` are left from it and no longer compiled.)

## How it works (Windows)

- **Finding the presenter view.** While a PowerPoint layout is active, every 400 ms it looks through the
  windows of `POWERPNT.EXE` and takes the first match of:
  1. a visible top-level window whose class is in `presenterWindowClasses` (default `PodiumParent`),
  2. a visible *child* window with such a class (in case a PowerPoint build nests it),
  3. a visible window whose title contains one of `presenterWindowTitles` (default `Presenter View`),
  4. during a slide show, the one other `screenClass` window besides the audience window.

  The audience window comes from PowerPoint's object model (`SlideShowWindow.HWND`), read from outside
  PowerPoint on a background thread (from the Running Object Table, or through PowerPoint's document window
  until PowerPoint registers there) and only while slide show windows are open. It is released after every
  read, so PPTimer never keeps PowerPoint running. The audience window is never used, whatever the config says.
  While attached it re-checks every 2 s. This also covers Alt+F5, "Swap displays" and moved windows.
- **Drawing the overlay.** A borderless, always-on-top window painted with per-pixel alpha
  (`UpdateLayeredWindow`), so the background can be fully transparent: only the digits, with an
  optional dark outline. It never takes focus, so the clicker and arrow keys still go to PowerPoint, and
  clicks pass through it. On a presenter view it hides while another program covers the spot (so it
  doesn't float over e.g. a browser the presenter opened) and while the presenter view is minimised.
  A blackout layout adds a plain black window behind it, covering the target.
- **Display page.** `http://PC:9595/display` shows only the timer on black (same colours and
  blink as the overlay) with a fullscreen button bottom-left, e.g. for a tablet or another computer's screen.
- **Position.** Stored per layout as a rectangle in % of the target. Drag it on the web page
  (`http://PC:9595/`), where the target is drawn as a box with its real proportions.
- **Sound.** When a running countdown reaches zero, it plays `soundFile` (a .wav on the presentation
  PC) or a built-in triple beep. It uses the PC's default audio output, which at a venue may be
  the PA, so it is off by default.
- **Timing.** The countdown runs off a monotonic clock inside PPTimer. Network messages
  are only commands, so a dropped message never makes the timer drift.
- **At zero.** Optionally blinks (a smooth 2 s fade), then counts up (`00:15`, or `-00:15` with `showMinus`), or
  stops at `00:00` if `countUp` is off. The colour at zero is red, or amber if red is off.

## Build (on the Mac)

```sh
brew install --cask dotnet-sdk        # once
./build.sh                             # Windows app -> dist/PPTimer-win/ and dist/PPTimer-win.zip
./build.sh dev                         # dev server + browser remote on http://localhost:9595/
./build.sh mac                         # Mac app, see below
```

## Install / update (Windows)

1. Copy `dist/PPTimer-win` to the laptop, via a network share, USB or the zip.
2. Double-click **`install.cmd`**. Re-run it after every build; it quits the running PPTimer first. It
   installs `PPTimer.exe` for the current user, adds a Start menu shortcut, makes PPTimer start with
   Windows (`install.cmd -NoAutostart` doesn't; the tray menu has *Start with Windows* too), starts it, and
   checks network access. The first time, Windows asks for admin permission so it can add the URL
   reservation and a firewall rule for TCP 9595. Later updates skip that step. If you decline, the API only
   answers on `localhost`; run `install.cmd` again to retry. `uninstall.cmd` removes all of it.
3. **Updating from 1.0.x (the PowerPoint add-in):** `install.cmd` unregisters the add-in. If PowerPoint is
   open at that moment, close it once, or the old add-in keeps port 9595 until PowerPoint quits.

PPTimer's icon is a ring in the notification area (bottom right, maybe under ^): grey when paused,
green when running, amber/red near the end, filled while the timer is placed on its target. Click it for the
menu: status and remote URLs, start/pause, reset, ±1 minute, speed, show/hide, **Layout**, the remote
and display pages, the settings folder, *Start with Windows* and Quit. Double-click opens the remote.

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
| `layout` | `name=Keynote`, or `step=1` / `step=-1` | switch layout (by name, or next / previous) |
| `savelayout` | `name=Stage left` | saves the active layout under a new name (or over an existing one) and switches to it |
| `renamelayout` | `name=Old`, `to=New` | `name` defaults to the active layout |
| `deletelayout` | `name=Old` | the last layout can't be deleted |
| `settings` | any runtime key below | no args returns current settings |
| `togglesetting` | `key=soundEnabled` (any on/off runtime key) | flips it against PPTimer's current value; returns the settings |
| `testsound` | | plays the zero sound once |
| `state` | | `GET /api/state` |

Diagnostics (same token rules as the API):
- `GET /api/debug/windows`: the active layout and target, PowerPoint's windows (class, title, rect, monitor, child
  classes), running slide shows with their audience window and "Use Presenter View" setting, monitors and screens,
  and which window the overlay is attached to and why.
- `GET /api/debug/log` (Windows): the last 200 KB of `pptimer.log` as text (`?kb=1000` for more). Ask whoever reports a
  problem to open `http://PC-IP:9595/api/debug/log` and save the page, rather than photographing the screen.

WebSocket pushes:
- `{"type":"hello",...}` on connect.
- `{"type":"state", display, phase, running, remainingMs, remainingSeconds, durationMs, duration, progress, visible, presenterView, overtime, speedPercent, layout, target, screens, ...}` on every visible change, plus a heartbeat every 5 s.
  `presenterView` (name kept from 1.0) is true while the active layout's target is found, with its size in
  `presenterWidth` / `presenterHeight`; `target` says what it is ("PowerPoint presenter view", "Screen 2 · DELL U2720Q"),
  and `screens` lists the displays (`id`, `name`, `width`, `height`, `main`, `external`) for the layout editor.
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
curl "http://PC:9595/api/layout?name=Fullscreen"
curl "http://PC:9595/api/settings?layout=Screen&screen=2&blackout=true"   # select, then change it
```

## config.json

| Key | Default | Runtime? | |
| --- | --- | --- | --- |
| `port` | 9595 | no | re-run `install.cmd` after changing |
| `apiToken` | "" | no | if set: `X-Api-Token` header or `?token=` |
| `presenterWindowClasses` | `["PodiumParent"]` | no | Windows: window class(es) to attach to, strongest first. Don't add `PPTFrameClass` (editing window) or `screenClass` (slide show) |
| `presenterWindowTitles` | `["Presenter View"]` | no | Windows: title fragments that identify the presenter view; add the translation for a localised PowerPoint |
| `clickThrough` | true | no | overlay ignores the mouse |
| `layout` | "PowerPoint" | yes | name of the active layout |
| `layouts` | see [Layouts](#layouts) | via commands | one object per line: `name`, `target`, `screen` and the keys below marked *layout* |
| `target` | | yes, *layout* | `powerpoint`, `keynote`, `presenter` or `screen` |
| `screen` | `main` | yes, *layout* | `main`, `external`, a number or part of a display name (target `screen`) |
| `xPercent` / `yPercent` | 22 / 74 | yes, *layout* | overlay top-left corner, % of the target |
| `widthPercent` / `heightPercent` | 16 / 9 | yes, *layout* | overlay size; the digits are fitted inside |
| `opacity` | 1 | yes, *layout* | 0.2 – 1 |
| `transparentBackground` | true | yes, *layout* | false = solid rounded box |
| `textOutline` | true | yes, *layout* | dark outline around the digits |
| `blackout` | false | yes, *layout* | the rest of the target is black (fullscreen timer) |
| `warnEnabled` / `warnSeconds` | true / 180 | yes | amber from 3:00 |
| `criticalEnabled` / `criticalSeconds` | true / 60 | yes | red from 1:00 |
| `blinkAtZero` | true | yes | |
| `countUp` | true | yes | false = stop at 00:00 |
| `showMinus` | false | yes | `-00:15` instead of `00:15` while over time |
| `soundEnabled` | false | yes | |
| `soundFile` | "" | yes | .wav path on the presentation PC; empty = built-in beep |
| `defaultDurationSeconds` | 300 | (auto) | last `set` value, restored at startup |

"No" means quit and restart PPTimer after editing the file. *Layout* keys are stored in each entry of
`layouts`; over the API (`settings`) they read and change the active layout, as the flat keys did before
1.1.0. A config.json from 1.0.x is upgraded on first start: its position and look become those of the
presenter view layouts.

## Companion module (dev)

```sh
cd companion-modules/companion-module-highpass-pptimer
corepack yarn install
corepack yarn package        # -> highpass-pptimer-<version>.tgz
```

In the Companion launcher's settings, set **Developer modules path** to
`…/PPtimer/companion-modules`. Then add a connection of type *pptimer* with the computer's IP and
port 9595. Presets are under *Timer control*, *Adjust time*, *Speed*, *Features on / off*, *Set duration* and
*Layouts*. For development without Windows, point it at `localhost` with `./build.sh dev` running.

## Mac version

A menu bar app (`PPTimer.app`). It has the same API on port 9595, the same web pages, layouts and
`config.json` keys, so the Companion module works unchanged: point it at the Mac's IP address. The menu
bar shows the countdown (◉ = placed on the layout's target, ○ = not found) and has start/pause, reset,
±1 minute, speed, show/hide, **Layout**, the remote URL and *Open at Login*.

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

**The overlay** is a borderless panel at screen-saver level on the layout's target (a blackout layout adds a
black panel just below it). It never takes focus, so the clicker and arrow keys still go to Keynote/PowerPoint,
and clicks pass through it. Screen layouts need no permission at all.

**Signing.** macOS remembers the PowerPoint permission per code signature, so `mac/scripts/sign.sh`
signs with a self-signed certificate kept in its own keychain
(`~/Library/Keychains/pptimer-signing.keychain-db`, created on the first build and added to your
keychain search list). Rebuilds keep the permission. It is not notarized: on another Mac,
right-click → **Open** the first time (or System Settings → Privacy & Security → *Open Anyway*). Set
`PPTIMER_SIGN_IDENTITY` to sign with a real Developer ID instead.

Mac differences in `config.json`: no `presenterWindowClasses`. `soundFile` can be any file macOS plays
(.wav, .aiff, .mp3, .m4a), and `~` is allowed. The default layouts include *Keynote*. `GET /api/debug/windows`
lists the displays and Keynote/PowerPoint windows with their levels, which is useful if a new Keynote version
changes them.

## Troubleshooting

- **No icon in the notification area.** Check *Show hidden icons* (^). Start PPTimer from the Start menu; if it
  says it is already running, the icon is hidden there. If it doesn't start at all, see `pptimer.log`.
- **Overlay doesn't appear.** First check the menu (or the remote's status line): which layout is active, and is its
  target found? A Fullscreen layout needs a second, non-mirrored display; a PowerPoint layout needs a slide show
  with presenter view. Then get the log (`http://PC-IP:9595/api/debug/log`, or `pptimer.log`). It has:
  - at startup: the PPTimer and Windows versions, the detection settings, the screens, and warnings for
    suspicious `presenterWindowClasses`;
  - "Layout '…'" and "Timer target: …" whenever they change, and "Timer shown …" / "Timer hidden: <why>";
  - "Slide show started: …" with the audience window handle and `usePresenterView` (False means the
    *Slide Show → Use Presenter View* box is off, so there is nothing to find);
  - "PowerPoint visible windows changed: …" with one line per window (handle, class, title, position, monitor,
    `AUDIENCE` for the slide show window) and its child window classes;
  - "Presenter view found: … via <rule>" when it works, or after 3 s of a slide show
    "WARN Slide show running … but no presenter view found" with a likely cause and a full window dump;
  - "PowerPoint object model read failed" if PPTimer can't read the slide shows (then only rules 1–3 work).
- **config.json edited by hand and ignored.** The log says "Could not read config.json: … at line L, column C" and
  copies the file to `config.invalid.json` before anything can overwrite it. Lists are flat:
  `"presenterWindowClasses": ["PodiumParent", "Other"]`, not `["PodiumParent"],["Other"]`.
- **Companion can't connect.** Open `http://PC-IP:9595/` in a browser from the Companion
  machine. If that fails, check the tray menu for "Only this PC can connect" (run `install.cmd` again,
  accepting the admin prompt) or "Not listening on port 9595" (another program, such as PowerPoint with the
  1.0.x add-in still loaded, has the port).
