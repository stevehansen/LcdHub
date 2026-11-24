# Settings

LCD Hub keeps its settings in `%APPDATA%\LcdHub\settings.json`. The tray menu writes most of
them. A few (the weather place, the accent colour, launched apps) are only set by editing the
file.

- **Tray ▸ Settings ▸ Open settings file** writes the file with every key and its current
  value, then opens it.
- **Tray ▸ Settings ▸ Reload settings** applies your edits at once, every key included (the
  weather place, launched apps, a panel's video), and says what's wrong with them, if anything.
  A file that isn't valid JSON changes nothing, so fix it and reload again.
- The file points at [`settings.schema.json`](../settings.schema.json) through `$schema`, so
  editors such as VS Code check it as you type, complete keys and explain each one on hover.
- If the hub finds a problem at start or on reload (an unknown key, a bad value, a launch exe that doesn't
  exist), **Settings** in the tray shows ⚠ and lists it. Bad values fall back to their defaults;
  unknown keys are kept in the file but ignored.
- A file that isn't valid JSON is copied to `settings.json.bad` before the hub writes anything,
  so nothing you typed is lost. Fix it there and copy it back.

## Example

```json
{
  "$schema": "https://raw.githubusercontent.com/stevehansen/LcdHub/main/settings.schema.json",
  "rotate_seconds": 15,
  "today": { "weather_location": "Antwerp", "effects": false },
  "style": { "mode": "dark", "accent": "#3B82F6" },
  "launch": [
    { "path": "%USERPROFILE%\\Tools\\LcdHub\\Stocks.exe", "args": "MSFT NVDA --alert 5" }
  ]
}
```

## Reference

### Top level

| Key | Default | Meaning |
|---|---|---|
| `rotate_seconds` | `10` | How long each app stays on a panel before the rotation moves on. Tray: **Rotate every**. |
| `app_seconds` | `{}` | Apps, by name, that stay on a panel for their own number of seconds instead of `rotate_seconds`, e.g. `{ "Stocks": 30 }`. Tray: **Rotate every** ▸ *app*. |
| `night_brightness` | `50` | Percent the panels dim to between dusk and dawn, fading over the 40 minutes after sunset and before sunrise. Sun times come from the weather (`today.weather_location`), else 7:00 and 19:00. `100` is off. Tray: **Dim at night**. |
| `today` | | What Today shows (below). |
| `style` | | The look every app is asked to share (below). |
| `launch` | `[]` | Apps the hub starts and keeps running (below). |
| `panels` | `{}` | Per-panel choices, written by each panel's tray menu (below). |

### `today`

The same on every panel. Tray: **Today**.

| Key | Default | Meaning |
|---|---|---|
| `weather_location` | none | A place name for [Open-Meteo](https://open-meteo.com) to look up, e.g. `"Antwerp"`. Without one there's no weather. |
| `weather` | `true` | Show the weather (needs `weather_location`). |
| `date` | `true` | Show the date under the time. |
| `holidays` | `true` | Show a holiday's name under the date, on the day. |
| `sky` | `true` | Sky colours for the time of day. Off uses the style's background and text colours. |
| `effects` | `true` | Stars, the sun arc and the season's or holiday's particles. |

### `style`

Sent to every app, so that the rotation doesn't jump between looks. Apps that predate it ignore
it. Tray: **Appearance**.

| Key | Default | Meaning |
|---|---|---|
| `mode` | `"system"` | `"system"` follows Windows' app mode; or `"dark"` or `"light"`. |
| `accent` | none | `"#RRGGBB"`. Without one, the Windows accent colour. |

### `launch`

Apps the hub starts with itself. Each entry has:

| Key | Meaning |
|---|---|
| `path` | The app's own `.exe` (not `dotnet run`). Environment variables like `%USERPROFILE%` are expanded. |
| `args` | Command-line arguments, optional. |
| `tiresias` | Variables sealed in Tiresias (`tir`) that the app needs, such as `["PHARE_API_KEY"]`, optional. The app is then started through `tir run --only …`, so only it gets them, each time after you approve it in Tiresias. |

An app that exits is started again after 5 seconds. If it keeps exiting within a minute, the
wait doubles each time, up to 5 minutes. The apps close when the hub does, even if the hub
crashes. An exe that is already running (for example from its own *Start with Windows*) is
left alone and only started once it has exited. Apps inherit the hub's environment; keys such
as `PHARE_API_KEY` are better sealed in Tiresias and listed under `tiresias` than set as user
environment variables. On a reload, new entries start and the apps of removed entries are
closed; unchanged ones keep running, and a changed `tiresias` list counts as a new entry.

```json
"launch": [
  { "path": "%USERPROFILE%\\Tools\\LcdHub\\Uptime\\Uptime.exe", "tiresias": ["PHARE_API_KEY"] }
]
```

### `panels`

Keyed by panel id, such as `"0416:5302"`, or by region id (below). Written by the tray menu,
so apart from `layout` it rarely needs editing.

| Key | Default | Meaning |
|---|---|---|
| `today` | `false` | Today takes its own turn in the rotation (it always shows when nothing else does). |
| `hidden` | `[]` | Apps, by name, left out of this panel's rotation. |
| `pinned` | none | An app, by name, kept on this panel instead of rotating. Alerts still interrupt it. |
| `video_path` | none | A video playing on this panel instead of the rotation, scaled to its size. |
| `layout` | `[]` | Strips cut off the panel's edges, each a region with its own rotation (below). Only on a panel. |

#### Regions (`layout`)

Each entry cuts a strip off one edge of what is left of the panel, in order:

| Key | Default | Meaning |
|---|---|---|
| `region` | required | Its name, unique on the panel and without `/`. |
| `edge` | `"left"` | `left`, `right`, `top` or `bottom` of what is left. |
| `size` | required | Its width (left, right) or height (top, bottom) in pixels, at least 64 (16 for a status strip). |
| `kind` | `"rotation"` | `rotation`: a region with its own rotation. `status`: a status strip (below). |
| `show` | all, in order | What a status strip shows, in order (below). |

What is left keeps the panel's id, so the panel's own `today`, `hidden`, `pinned` and
`video_path` apply to it. Each strip is set up under its own entry, `"<panel id>/<region>"`,
with the same keys (except `layout`). Apps see every region as a panel of its own size, and each
rotates on its own, alerts included. While the session is locked or the monitors are off, the
whole panel shows Today or goes black, whatever its layout. A strip that would leave less than
64 pixels is skipped and listed under ⚠ Settings. Reload settings applies a changed layout at
once: apps redraw for the new sizes and videos restart; a mirrored window stops.

```json
"panels": {
  "0416:5408:20258EE9F67A": {
    "layout": [ { "region": "side", "edge": "left", "size": 480 } ],
    "pinned": "Cortex"
  },
  "0416:5408:20258EE9F67A/side": { "today": true }
}
```

A **status strip** (`"kind": "status"`) has no rotation: the hub draws what its `show` list
names, in that order:

| Entry | Shows |
|---|---|
| `time` | The time. It starts every page, wherever it is in the list. |
| `items` | Every app's status items (short lines such as "Fable 7d 100% → Mon 09:00"), worst first. |
| `date` | The day and date, e.g. "Sun 15 Nov". |
| `weather` | The temperature and sky at `today.weather_location`, once known. |
| `apps` | The connected apps' names, dimmed: what is running. |

The default is all of them, in the order above, so the less important end up on the right.
Everything flows left to right and wraps; what doesn't fit is paged every `rotate_seconds`.
Every status strip on every panel shows the same items. Apps don't see it as a panel, so it has
no entry of its own under `panels`.

```json
"layout": [ { "region": "strip", "edge": "bottom", "size": 40, "kind": "status", "show": ["time", "items", "weather"] } ]
```
