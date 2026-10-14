# CLAUDE.md

## Build / Run

```powershell
dotnet build
dotnet run --project .\LcdHub.csproj -- --diagnose   # log to %TEMP%\lcd-hub.log
dotnet run --project .\Tests\LcdHub.Tests.csproj     # console checks; throws on the first failure
```

Target: `net10.0-windows` WinExe. Win32 only (no WinForms/WPF), AOT-friendly (`LibraryImport`, function-pointer COM).

## Architecture

The hub is the single owner of the USB LCDs. WinUSB is exclusive and HID frames would interleave, so no other process may drive them. Apps ("producers") render whole frames at each panel's size and push them over a pipe. The hub only schedules what to show.

- `Client/LcdHubClient.cs`: the entire producer contract (client + `LcdHubWire`). Producers copy it or `<Compile Include>` it, and the hub compiles the same file. Additions (new message types, appended fields) are ignored by older peers and only raise `LcdHubWire.Version`. A change an old peer would misread raises `MinVersion`, and the hub refuses producers below it.
- `PipeServer.cs`: the `LcdHub` pipe uses `CurrentUserOnly` and runs one thread per producer. Those threads only call `Hub.Connect/Offer/Disconnect`.
- `Hub.cs`: the state and scheduling.
  - A single `_gate` guards the panel slots, the producers and their frames.
  - Pipe threads store frames (validated against the panel size) and post `WM_APP_REFRESH`.
  - The UI thread does everything else: the 1 s `Tick` (device sync + refresh), feeds and settings.
- `Rotation.cs`: a pure `Pick` function holding all the rules (display off → blank, locked → Today, alerts, feed, rotation). It is tested; keep the rules there.
- `Panel/`: the device side.
  - `LcdPanel`: the device drivers, JPEG and keepalive.
  - `LcdFeed`/`WindowFeed`/`VideoFeed`: hub-owned moving sources.
- `samples/`: example producers (console apps). Each links the client plus `samples/Common` (a GDI `Canvas` and the `Sample.Run` loop). They are excluded from the hub's compile glob, and must stay free of keys and personal data.
- `Launcher.cs`: starts the `launch` apps from settings and restarts them with backoff. They sit in a kill-on-close job object, so they end with the hub even when it is killed. It skips an exe that is already running elsewhere.
- `MainWindow.cs`: a hidden popup window (so it gets `TaskbarCreated`), the tray menu, the WTS lock and display-state notifications, and the startup registry entry.

## Ubiquitous Language

`UBIQUITOUS_LANGUAGE.md` (repo root) is the canonical glossary: Panel, Producer, Frame, Alert, Rotation, Pick, Feed, Takeover. Use those terms in code, comments and docs, and say "app" only in user-facing strings. Check its "Flagged ambiguities" before naming anything new, notably "Screen" (never a type name; say firmware screen or monitor) and app/producer/client. Update the glossary when you introduce or rename a domain concept.

## Domain Documentation (Living Specs)

Each domain has a living spec, `docs/<domain>.md`, paired with a thin priming skill,
`.claude/skills/<domain>/SKILL.md`. They are indexed in `docs/README.md`. The domains are
**panels**, **scheduling** and **producer-protocol**.

**Same-PR sync rule:** a change to a domain's behavior updates its spec in the same PR, and its
skill too if a load-bearing invariant changes. The same applies to `UBIQUITOUS_LANGUAGE.md`.

## Gotchas

- `LcdPanel.Present` takes ownership and may reverse the array in place, so producer frames are **cloned** before presenting.
- Feed frames are presented under `_gate`, and only while that slot's `Pick == Rotation.Feed`.
- The hub forgets a producer's frames whenever the panel list changes. The client's `PanelsChanged` tells producers to redraw.
- Keep managed callbacks in static fields (`_wndProcKeepAlive`, `_windowEnumKeepAlive`).
- User-visible strings go in `Localization/Strings.cs`. JSON goes through `SettingsJsonContext`.
