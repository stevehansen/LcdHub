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

`Hub -> PanelSlot (Panel, Takeover) -> Region (Feed?, Pick, LastSent, Pixels)` and `Hub -> Producer -> Frame per region id`

- **`Hub`** (static): the panel slots, the producers and the takeover flags, all under one gate.
- **`PanelSlot`**: a plugged-in panel, cut into **Regions** by its layout (`Layout.Carve`, pure
  and tested). `Takeover` is what a takeover last put on the whole panel.
- **`Region`**: a part of a panel with a rotation of its own; a panel without a layout is one
  region with the panel's id. Producers are told regions as panels. `Pick` is the rotation's
  decision, `LastSent` what was last drawn there, so an unchanged picture is drawn once, and
  `Pixels` that picture, for `Compose` to put the panel together.
- **Status strip**: a region whose layout entry has `kind: "status"` (`Region.IsStatus`). It has no
  pick and no feed and isn't announced to producers; `StatusStrip.Paint` draws the time and every
  producer's **status items** (`Producer.Statuses`), and `StatusStrip.Flow` (pure, tested) wraps
  them into lines and pages.
- **`Producer` / `Frame`**: a connected app and its newest picture per panel. Frames are compared
  by reference, so each new frame is a new object.
- **`Rotation.Pick`**: a pure function from (offers, Today in rotation, has feed, locked, display
  off, current pick, time shown, interval, what the panel's other regions show) to the next pick.
- **`LcdFeed`** (`VideoFeed`, `WindowFeed`): a moving picture made on its own thread at the
  region's size, which replaces that region's rotation.
- **`HubSettings` / `PanelSettings`**: the rotation interval and the hub-wide `TodaySettings`, plus per panel or region id the Today toggle,
  hidden producers, the pinned producer and the saved video path, and per panel its layout.

## Invariants & rules

- **The pick precedence lives only in `Rotation.Pick`.** In order: display off → blank, locked →
  Today, alerting producers, producers showing a toast, the pinned producer, feed, then the rotation of the producers plus Today. Never
  special-case a pick in `Hub` or the menu. Owned by `Rotation.cs`.
- **A takeover beats everything.** While locked, no producer frame and no feed frame reaches a
  panel. Display off sends black rather than stopping, because a panel left alone lights up its
  own logo. A takeover fills the whole panel, whatever its layout (`PanelSlot.Takeover`); the
  regions are drawn again when it ends.
- **Each region rotates on its own.** `Rotation.Pick` runs per region with that region's settings,
  so pins, Today, toasts and alerts are region-local; an alert never spreads to the rest of the
  panel. `Compose` stitches the regions' pictures into one panel frame, and hands a lone region
  that covers the panel straight through, uncopied.
- **A panel's rotations don't show the same thing twice.** When a region's turn ends (or it
  picks for the first time), the rotation takes the next producer, or Today, that no other region
  of the panel shows; only if every other one is shown elsewhere does it take the next anyway,
  so two regions swap rather than stick. Alerts, toasts and a pin show regardless, and a turn
  is never cut short by another region. Regions start on the same producer and share
  `rotate_seconds`, so without this they would run in lockstep.
- **Status strips are the same everywhere.** Every status strip shows all producers' status
  items, worst level first, then by producer name and key. A strip is drawn once per
  `StatusKey` (items version `_statusVersion`, minute with a clock, page, style); pages turn every
  `rotate_seconds`, and the time starts every page. A takeover covers status strips too.
- **Today is the floor.** With nothing to rotate, the pool is Today, so a panel never
  falls back to the vendor logo while the hub runs.
- **An alert lasts until the producer sends a non-alert frame** for that panel, withdraws its
  frame, or disconnects.
  Alerts pre-empt mid-interval; the alerting producers rotate among themselves.
- **A pin holds only while its producer has a frame there.** Otherwise the panel rotates as usual.
  Pin and feed exclude each other: choosing one clears the other (`Hub.TogglePin`, `Hub.SetFeed`),
  and **Rotate apps** clears both.
- **A toast shows over a pin and a feed, never over an alert,** and only until its lifetime runs
  out. While alerts rule a panel, an alerting producer shows its alert, not its toast (`Hub.Content`).
- **A pick stays for its whole turn** unless it drops out of the pool: the producer's own
  `app_seconds`, or `rotate_seconds`. `shownFor: TimeSpan.MaxValue` asks `Pick` what follows
  once the turn is over, which is how Next is told.
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
  [producer-protocol](producer-protocol.md)). A layout changed by Reload settings counts as one:
  that panel's regions are dropped and carved again (`DropRegions`, `CarveRegions`), then
  `AnnouncePanels`.
- **Settings are keyed by panel or region id and by producer name.** A mirrored window isn't saved, since
  it can't be found again. A video that ended with an error is forgotten. Owned by
  `Settings.cs` and `Hub.SetFeed`.

## Key files

| File | Role |
|---|---|
| `Rotation.cs` | `Pick`: every rule about what shows |
| `Layout.cs` | `Carve`: a panel's layout → regions |
| `StatusStrip.cs` | `Paint` and `Flow`: a status strip's picture and its lines and pages |
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
- **The menu can outlive its region.** `HandleCommand` rechecks `Hub.Regions.Contains`, because the
  video file dialog pumps messages, and a reload may carve the panel again.
- **A feed in a split panel composes every frame,** a new panel-sized array each time, since
  `Present` keeps the array it's given.
- **`ReapEndedFeeds` runs on `WM_APP_FEED_ENDED`.** A feed's `ended` fires on the feed thread,
  which mustn't touch the slots.

## Executable references

`Tests/Program.cs`, the `Rotation.Pick` checks: rotation order and interval, wrap-around, 
Today's turn, a departed producer, alerts pre-empting and rotating, alert over feed, pins, locked →
Today, display off → blank, skipping what other regions show; the `Layout.Carve` checks and layout settings problems;
and the `StatusStrip.Flow` checks. Run `dotnet run --project Tests/LcdHub.Tests.csproj`.

## Links

- Glossary: `UBIQUITOUS_LANGUAGE.md` § Choosing what a panel shows, § Producers and frames
- Related domains: [panels](panels.md) (presents the picked frame) and
  [producer-protocol](producer-protocol.md) (delivers frames and alerts).
