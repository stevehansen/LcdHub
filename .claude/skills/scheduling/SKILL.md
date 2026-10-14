---
name: scheduling
description: Prime on the LcdHub scheduling domain before working on it: Hub, PanelSlot, Producer/Frame, Rotation.Pick, alerts, pins ("Keep showing"), Today (the hub's own screen and its options), takeover (locked → Today, display off → blank), feeds (VideoFeed, WindowFeed) and per-panel settings. Use when the task touches rotation, rotate interval, alert priority, hidden apps, the tray menu's panel items, video or window mirroring, lock or monitor-off behaviour, or settings.json. Not for USB/device protocol (see panels) or the pipe wire format and client (see producer-protocol).
---

# Scheduling domain: priming

**Canonical spec:** [`docs/scheduling.md`](../../../docs/scheduling.md). Read it for depth: entities, all invariants, key files, gotchas. Terms of record: `UBIQUITOUS_LANGUAGE.md` § Choosing what a panel shows.

This domain decides each panel's **Pick**: blank, Today, an alerting producer, the pinned producer, the feed, or the rotation. It hands the result to `LcdPanel.Present` (panels) and gets frames from `Hub.Offer` (producer-protocol).

## Core invariants (get these right)

- **All pick rules live in `Rotation.Pick`.** It is pure and tested. Never special-case a pick in `Hub` or the menu.
- **Takeover beats everything.** Locked shows Today, and display off sends black, never nothing.
- **Today is the floor.** A panel never falls back to the vendor logo while the hub runs.
- **Feeds pause, not stop.** Their frames are presented under `_gate`, and only while `Pick == Rotation.Feed`.
- **Clone producer frames before `Present`.**
- **Pipe threads only store frames and post a refresh.** Everything else runs on the UI thread.

## Key files

- `Rotation.cs`: the rules. `Hub.cs`: state, refresh, feeds, menu commands. `Settings.cs`: per Panel id.
- `Panel/LcdFeed.cs`, `Panel/VideoFeed.cs`: feeds.

## Gotchas

- `Rotation.Today`, `Feed` and `Blank` start with `\0` so they can't collide with a producer name.
- Executable references: the `Rotation.Pick` checks in `Tests/Program.cs`. Add a case for any new rule.
