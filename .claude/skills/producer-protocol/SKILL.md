---
name: producer-protocol
description: Prime on the LcdHub producer protocol before working on it: LcdHubClient, LcdHubWire (Hello, Panels, Frame, Style and Visibility messages), LcdHubPanel, PipeServer, the version number, frame size validation, reconnects and PanelsChanged. Use when the task touches the named pipe, the wire format, writing or migrating a producer app, alerts sent by an app, or "frames don't arrive". Not for choosing between producers (see scheduling) or USB devices (see panels).
---

# Producer protocol: priming

**Canonical spec:** [`docs/producer-protocol.md`](../../../docs/producer-protocol.md). Read it for depth: the message rules, gotchas and tests. Terms of record: `UBIQUITOUS_LANGUAGE.md` § Producers and frames.

`Client/LcdHubClient.cs` is the whole contract, compiled by both the hub and the producers. What happens to a frame after `Hub.Offer` is scheduling.

## Core invariants (get these right)

- **Additions are backward-compatible.** Unknown message types and trailing bytes are ignored, so they only raise `Version`. Raise `MinVersion` (the hub refuses below it) only for a change an old peer would misread.
- **Visibility is Next until the hub says otherwise.** Skip drawing only when it is Hidden, and still send to raise or clear an alert.
- **Both ends use `LcdHubWire.PipeOptions` (`Asynchronous`).** With synchronous handles, writes stall behind reads.
- **A frame is exactly the panel's width × height, or empty to withdraw** (`Clear`). Other sizes are dropped silently.
- **A Panels message means "redraw everything".** The hub has forgotten your frames.

## Key files

- `Client/LcdHubClient.cs`: client and wire. The format doc is on `LcdHubWire`.
- `PipeServer.cs`: accept loop, hello and version check.

## Gotchas

- The producer name is persistent, because it keys "hidden" settings. Keep it stable.
- Executable references: the wire checks in `Tests/Program.cs`. Add a round-trip case for any new message.
