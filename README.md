# PPTimer

Countdown timer drawn on top of PowerPoint's **presenter view** (Windows), controlled over the
network by Bitfocus Companion. The audience screen never shows it.

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
| `addin/scripts/` | `install` / `uninstall` / `setup-network` (.cmd wrappers + .ps1). |
| `companion-module-highpass-pptimer/` | Companion module (base 2.x, needs Companion 4.3+). |
| `build.sh` | Builds the add-in and produces `dist/PPTimer-win/` + `.zip`. |

### Why a COM add-in rather than a VSTO project

VSTO is a wrapper around the same COM add-in mechanism (`IDTExtensibility2` plus registry
keys). Its project system only builds in Visual Studio on Windows. This project uses the plain
.NET SDK, so it builds on the Mac with `dotnet build` and has no VSTO runtime to install. It
has the same capabilities.

## How it works

- **Finding the presenter view.** Every 100 ms (on PowerPoint's UI thread) it looks for a
  visible top-level window of class `PodiumParent` in the PowerPoint process. This also
  covers Alt+F5, "Swap displays" and moved or resized windows.
- **Drawing the overlay.** A borderless window whose *owner* is the presenter view, so it
  always sits above it and nothing else. It is painted with per-pixel alpha
  (`UpdateLayeredWindow`), so the background can be fully transparent: only the digits, with an
  optional dark outline. It never takes focus, so the clicker and arrow keys still work, and
  clicks pass through it by default.
- **Position.** Stored as a rectangle in % of the presenter view. Drag it on the web page
  (`http://PC:9595/`), where the presenter view is drawn as a plain box with its real proportions.
- **Sound.** When a running countdown reaches zero, it plays `soundFile` (a .wav on the PowerPoint
  PC) or a built-in triple beep. It uses the PC's default audio output, which at a venue may be
  the PA, so it is off by default.
- **Timing.** The countdown runs off a monotonic clock inside the add-in. Network messages
  are only commands, so a dropped message never makes the timer drift.
- **At zero.** Optionally blinks (a smooth 2 s fade), then counts up (`00:15`, or `-00:15` with `showMinus`), or
  stops at `00:00` if `countUp` is off. The colour at zero is red, or amber if red is off.


## Install / update (Windows)

1. Download and unzip `/PPTimer-win.zip` from releases to the laptop.
2. Close PowerPoint, then double-click **`install.cmd`**. No admin needed.
3. Once only, double-click **`setup-network.cmd`**. It asks for admin and adds the URL
   reservation and a firewall rule for TCP 9595. Without it the API only answers on `localhost`.
4. Start PowerPoint. Check **File → Options → Add-ins → Manage: COM Add-ins → Go…**: PPTimer
   should be listed and ticked.

Files live in `%LOCALAPPDATA%\PPTimer\`: `bin\`, `config.json` and `pptimer.log`.


## Companion module

Get the Companion module from releases `pptimer-1.0.0.tgz` and add it to Companion via Modules -> Import module package.

## Build (Mac)

```sh
brew install --cask dotnet-sdk        # once
./build.sh                             # -> dist/PPTimer-win/ and dist/PPTimer-win.zip
./build.sh dev                         # dev server + browser remote on http://localhost:9595/
```


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
| `show` `hide` `togglevisible` | | overlay visibility |
| `settings` | any runtime key below | no args returns current settings |
| `testsound` | | plays the zero sound once |
| `state` | | `GET /api/state` |

`GET /api/debug/windows` lists PowerPoint's top-level windows, for checking the presenter view class name.

WebSocket pushes:
- `{"type":"hello",...}` on connect.
- `{"type":"state", display, phase, running, remainingMs, remainingSeconds, durationMs, duration, progress, visible, presenterView, overtime, ...}` on every visible change, plus a heartbeat every 5 s.
- `{"type":"result", id, ok, error}` for each command.
- `{"type":"settings",...}` when settings change.
- `{"type":"event","event":"zero"}` when a running countdown reaches zero.

`phase` is one of `normal`, `warning`, `critical` or `expired`.

Note: a bare `curl -X POST` with no body gets `411 Length Required`. Use GET, or add `-d ''`.

```sh
curl "http://PC:9595/api/set?time=10:00&start=true"
curl "http://PC:9595/api/add?seconds=-30"
curl -d '{"xPercent":30,"yPercent":75,"soundEnabled":true}' http://PC:9595/api/settings
```

## config.json

| Key | Default | Runtime? | |
| --- | --- | --- | --- |
| `port` | 9595 | no | re-run `setup-network.cmd` after changing |
| `apiToken` | "" | no | if set: `X-Api-Token` header or `?token=` |
| `presenterWindowClasses` | `["PodiumParent"]` | no | window class(es) to attach to |
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


## Troubleshooting

- **Add-in not listed or not loading.** Check `pptimer.log`. If there's no log at all, the
  DLL never loaded: re-run `install.cmd`, and look under COM Add-ins and
  *Disabled Items* (File → Options → Add-ins → Manage: Disabled Items).
- **Overlay doesn't appear.** The log prints "PowerPoint visible window classes: …" whenever
  they change. Start a slide show with presenter view (Alt+F5 on a single screen) and check
  which class appears. If it isn't `PodiumParent`, put it in `presenterWindowClasses`.
- **Companion can't connect.** Open `http://PC-IP:9595/` in a browser from the Companion
  machine. If that fails, check the log for "localhost only" and run `setup-network.cmd`.
