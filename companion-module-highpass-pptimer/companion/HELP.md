## PPTimer – PowerPoint presenter view countdown

Controls the **PPTimer** PowerPoint add-in, which draws a countdown on top of PowerPoint's
presenter view (visible to the presenter only, never on the audience screen).

### Setup

1. Install the PPTimer add-in on the presentation PC and run `setup-network.cmd` once (admin) so it accepts network connections.
2. Enter the PC's IP address here. The default port is **9595**. If you set `apiToken` in the add-in's `config.json`, enter the same token.
3. Drag presets from **Timer control**, **Adjust time**, **Features on / off** and **Set duration** onto your buttons.

### Actions

- Start, Pause, Start/pause toggle, Reset (to duration, paused), Restart (to duration, running)
- Set duration: `90`, `5:00` or `1:05:00`, optionally start or pause afterwards
- Add / remove time
- Overlay show / hide / toggle
- Overlay position / size / opacity (easier: drag it on the PPTimer web page, `http://<PC>:9595/`)
- Colour thresholds (amber / red, as `m:ss`)
- Turn a feature on / off / toggle: amber, red, blink at zero, count up after zero, minus sign, sound at zero, transparent background, outline
- Play test sound

### Variables

`$(pptimer:remaining)`, `remaining_seconds`, `duration`, `duration_seconds`, `progress_percent`,
`phase` (normal / warning / critical / expired), `status` (running / paused / offline), `running`,
`overlay_visible`, `presenter_view`

### Feedbacks

Timer phase, Running, Paused, Overlay visible, Presenter view detected, Feature enabled, Not connected
