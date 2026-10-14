# Producer protocol

The contract between the hub and the apps that send it frames: the named pipe, its messages, and
the client library.

**Status:** current as of wire version 1. No single PRD (foundational domain).
**Priming skill:** [`.claude/skills/producer-protocol/SKILL.md`](../.claude/skills/producer-protocol/SKILL.md)

## What it is

`Client/LcdHubClient.cs` is the whole contract. Producers copy it or `<Compile Include>` it, and
the hub compiles the same file for its side, so the two sides can't disagree. This domain owns how
frames and panel lists cross the pipe. It does *not* choose between producers (see
[scheduling](scheduling.md)) or know anything about devices (see [panels](panels.md)).

## Core entities & relationships

`LcdHubClient --pipe "LcdHub"--> PipeServer -> Hub.Connect / Offer / Disconnect`

- **`LcdHubWire`**: message framing, the message types (Hello, Panels, Frame) and the pipe
  options. The doc comment on the class is the format spec.
- **`LcdHubClient`**: a producer's connection. It reconnects by itself, keeps only the newest
  frame per panel, and raises `PanelsChanged`.
- **`LcdHubPanel`**: id, width and height as announced to producers.
- **`PipeServer`**: an accept loop with one thread per producer. It checks the hello, then hands
  each frame to `Hub.Offer`.

## Invariants & rules

- **Any change to the wire bumps `LcdHubWire.Version`.** The hub refuses any other version, so an
  old producer quietly stays off the panels instead of corrupting them.
- **Both pipe ends open with `LcdHubWire.PipeOptions`** (`CurrentUserOnly | Asynchronous`).
  Without overlapped I/O, a write waits behind the pending read on the other thread: frames
  stall, and the hub's Panels broadcast under its gate can hang the UI thread.
- **Per-user pipe.** A hub run by another user is unreachable (`UnauthorizedAccessException`) by
  design.
- **Hello comes first.** Anything else first, or a version mismatch, closes the connection.
- **A frame must be exactly width × height of the panel, or empty.** An empty frame
  (`LcdHubClient.Clear`) withdraws the producer from that panel until its next frame, so it
  leaves the rotation and stands down any alert without disconnecting. The hub drops other sizes
  and unknown panel ids silently. It never scales.
- **Every Panels message means "redraw everything".** The hub has forgotten the producer's
  frames. The client clears its pending frames and fires `PanelsChanged`, including on connect,
  and with an empty list on disconnect.
- **`Send` never blocks and takes ownership of the array.** Frames sent while disconnected are
  dropped.
- **Messages are bounded** (`MaxMessageBytes`). A bad length or truncated body throws
  `IOException`, which ends that one producer's connection only.

## Key files

| File | Role |
|---|---|
| `Client/LcdHubClient.cs` | `LcdHubClient`, `LcdHubPanel`, `LcdHubWire` (format doc on the class) |
| `PipeServer.cs` | Accept loop, hello and version check, frames → `Hub.Offer` |
| `Hub.cs` | `Connect` (sends the first Panels message, dedupes names), `Offer`, `Disconnect` |

## Gotchas

- **The producer name is user-facing and persistent.** It appears in the tray menu and keys
  "hidden on this panel", so keep it stable across versions of the app.
- **`PanelsChanged` runs on the client's thread,** not the producer's UI thread.
- **Panels are sent under the hub's gate** in `Hub.Connect` and `SyncPanels`, so a newer list
  can't be overtaken by an older one. This only holds while writes never block, which is why the
  asynchronous pipe option matters.
- **Pixel layout:** top-down rows of little-endian `uint` BGRX, written as raw memory.
  Non-.NET producers must match it byte for byte.

## Executable references

`Tests/Program.cs`, the wire checks: hello, panels and full-frame and empty-frame round-trips,
back-to-back framing, clean EOF → null, truncated message throws, absurd length refused.

## Links

- Glossary: `UBIQUITOUS_LANGUAGE.md` § Producers and frames
- Related domains: [scheduling](scheduling.md) (what happens to an offered frame) and
  [panels](panels.md) (where panel ids and sizes come from).
