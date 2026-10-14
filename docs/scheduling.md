# Scheduling

Deciding, for each panel, what it shows right now: a producer's frame, Today, a feed, or
blank.

**Status:** current as of the initial release. No single PRD (foundational domain).
**Priming skill:** [`.claude/skills/scheduling/SKILL.md`](../.claude/skills/scheduling/SKILL.md)

## What it is

The hub's core. It holds the newest **Frame** from each **Producer** per panel, the hub-owned
**Feeds**, the **Takeover** state and the per-panel settings, and turns them into a **Pick**
every second and whenever a frame arrives. It does *not* talk USB (see [panels](panels.md)) or
parse the pipe (see [producer-protocol](producer-protocol.md)).

## Core entities & relationships

`Hub -> PanelSlot (Panel, Feed?, Pick, LastSent)` and `Hub -> Producer -> Frame per panel id`

- **`Hub`** (static): the panel slots, the producers and the takeover flags, all under one gate.
- **`PanelSlot`**: a plugged-in panel plus what is on it. `Pick` is the rotation's decision, and
  `LastSent` is what was last presented, so an unchanged picture goes out once.
- **`Producer` / `Frame`**: a connected app and its newest picture per panel. Frames are compared
  by reference, so each new frame is a new object.
- **`Rotation.Pick`**: a pure function from (offers, Today in rotation, has feed, locked, display
  off, current pick, time shown, interval) to the next pick.
- **`LcdFeed`** (`VideoFeed`, `WindowFeed`): a moving picture made on its own thread at the
  panel's size, which replaces the rotation.
- **`HubSettings` / `PanelSettings`**: the rotation interval and the hub-wide `TodaySettings`, plus per Panel id the Today toggle,
  hidden producers, the pinned producer and the saved video path.

## Invariants & rules

- **The pick precedence lives only in `Rotation.Pick`.** In order: display off → blank, locked →
  Today, alerting producers, the pinned producer, feed, then the rotation of the producers plus Today. Never
  special-case a pick in `Hub` or the menu. Owned by `Rotation.cs`.
- **A takeover beats everything.** While locked, no producer frame and no feed frame reaches a
  panel. Display off sends black rather than stopping, because a panel left alone lights up its
  own logo.
- **Today is the floor.** With nothing to rotate, the pool is Today, so a panel never
  falls back to the vendor logo while the hub runs.
- **An alert lasts until the producer sends a non-alert frame** for that panel, withdraws its
  frame, or disconnects.
  Alerts pre-empt mid-interval; the alerting producers rotate among themselves.
- **A pin holds only while its producer has a frame there.** Otherwise the panel rotates as usual.
  Pin and feed exclude each other: choosing one clears the other (`Hub.TogglePin`, `Hub.SetFeed`),
  and **Rotate apps** clears both.
- **A pick stays for a whole interval** unless it drops out of the pool.
- **A feed pauses, it isn't stopped,** while an alert or takeover holds the panel.
  `PresentFeedFrame` presents only under the gate and only while `Pick == Rotation.Feed`, so a
  late feed frame can't overwrite the switch away.
- **A switch of pick fades.** `Refresh` passes `fade: true` when the pick changed, and the panel
  blends from what it shows over 400 ms. `Present` keeps the array and never changes it, so producer
  frames go in uncloned.
- **Visibility follows the pick.** After each pick, `TellVisibility` tells each producer Shown,
  Next (the pick that would follow this turn, or no frame here yet) or Hidden (see
  [producer-protocol](producer-protocol.md)).
- **The style is checked every tick** (`HubStyle.Current`: settings, then the Windows app mode and
  accent). A change is broadcast and redraws Today, which uses the style when the sky is off.
- **Pipe threads only store frames and post `WM_APP_REFRESH`.** Panels, feeds and settings change
  on the UI thread. Refresh posts are coalesced through `_refreshPosted`.
- **Panel list changes drop that panel's frames.** Producers learn of the change and redraw (see
  [producer-protocol](producer-protocol.md)).
- **Settings are keyed by Panel id and by producer name.** A mirrored window isn't saved, since
  it can't be found again. A video that ended with an error is forgotten. Owned by
  `Settings.cs` and `Hub.SetFeed`.

## Key files

| File | Role |
|---|---|
| `Rotation.cs` | `Pick`: every rule about what shows |
| `Hub.cs` | Slots, producers, `Refresh`, `SyncPanels`, feeds, menu commands |
| `Settings.cs` | `%APPDATA%\LcdHub\settings.json` via `SettingsJsonContext` |
| `Panel/LcdFeed.cs` | `LcdFeed` base (pause, ended), `LcdCanvas` scaling, `WindowFeed` |
| `Panel/VideoFeed.cs` | Media Foundation decode (GPU if available), loops, skips late frames |
| `MainWindow.cs` | Tray menu, WTS lock and display-state notifications → `SetTakeover` |
| `Today.cs`, `Theme.cs` | The Today picture and its look (sky, season, holidays, particles) |
| `Weather.cs` | `WeatherSource`: Open-Meteo every 15 min in the background, for the configured place |

## Gotchas

- **Rotation constants start with `\0`** (`Rotation.Today`, `Feed`, `Blank`) so that they can't
  collide with a producer name. Trim them before logging.
- **Today is drawn once per `TodayKey`** (minute, weather reading, options version). Anything
  else it shows must go into that key, or the panel won't change. A new weather reading posts a
  refresh from the weather thread.
- **Duplicate producer names get a ` (2)` suffix,** and a suffixed name is a different key in
  `Hidden`.
- **The menu can outlive its slot.** `HandleCommand` rechecks `Hub.Slots.Contains`, because the
  video file dialog pumps messages.
- **`ReapEndedFeeds` runs on `WM_APP_FEED_ENDED`.** A feed's `ended` fires on the feed thread,
  which mustn't touch the slots.

## Executable references

`Tests/Program.cs`, the `Rotation.Pick` checks: rotation order and interval, wrap-around, 
Today's turn, a departed producer, alerts pre-empting and rotating, alert over feed, pins, locked →
Today, display off → blank. Run `dotnet run --project Tests/LcdHub.Tests.csproj`.

## Links

- Glossary: `UBIQUITOUS_LANGUAGE.md` § Choosing what a panel shows, § Producers and frames
- Related domains: [panels](panels.md) (presents the picked frame) and
  [producer-protocol](producer-protocol.md) (delivers frames and alerts).
