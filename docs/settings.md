# Settings

LCD Hub keeps its settings in `%APPDATA%\LcdHub\settings.json`. The tray menu writes most of
them. A few (the weather place, the accent colour, launched apps) are only set by editing the
file. **Restart the hub after editing it by hand.**

- **Tray ▸ Settings ▸ Open settings file** writes the file with every key and its current
  value, then opens it.
- The file points at [`settings.schema.json`](../settings.schema.json) through `$schema`, so
  editors such as VS Code check it as you type, complete keys and explain each one on hover.
- If the hub finds a problem at start (an unknown key, a bad value, a launch exe that doesn't
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
| `today` | | What Today shows (below). |
| `style` | | The look every app is asked to share (below). |
| `launch` | `[]` | Apps the hub starts and keeps running (below). |
| `panels` | `{}` | Per-panel choices, written by each panel's tray menu (below). |

### `today`

The same on every panel. Tray: **Today**.

| Key | Default | Meaning |
|---|---|---|
| `weather_location` | none | A place name for [Open-Meteo](https://open-meteo.com) to look up, e.g. `"Antwerp"`. Without one there's no weather. Restart after changing it. |
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

An app that exits is started again after 5 seconds. If it keeps exiting within a minute, the
wait doubles each time, up to 5 minutes. The apps close when the hub does, even if the hub
crashes. An exe that is already running (for example from its own *Start with Windows*) is
left alone and only started once it has exited. Apps inherit the hub's environment, so set
keys such as `PHARE_API_KEY` as user environment variables. Restart after changing the list.

### `panels`

Keyed by panel id, such as `"0416:5302"`. Written by the panel's tray menu, so it rarely needs
editing.

| Key | Default | Meaning |
|---|---|---|
| `today` | `false` | Today takes its own turn in the rotation (it always shows when nothing else does). |
| `hidden` | `[]` | Apps, by name, left out of this panel's rotation. |
| `pinned` | none | An app, by name, kept on this panel instead of rotating. Alerts still interrupt it. |
| `video_path` | none | A video playing on this panel instead of the rotation. |
