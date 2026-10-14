# Producer protocol

The contract between the hub and the apps that send it frames: the named pipe, its messages, and
the client library.

**Status:** current as of wire version 3 (toasts and frame lifetimes). No single PRD (foundational domain).
**Priming skill:** [`.claude/skills/producer-protocol/SKILL.md`](../.claude/skills/producer-protocol/SKILL.md)

## What it is

`Client/LcdHubClient.cs` is the whole contract. Producers copy it or `<Compile Include>` it, and
the hub compiles the same file for its side, so the two sides can't disagree. This domain owns how
frames, panel lists, the shared style and visibility cross the pipe. It does *not* choose between producers (see
[scheduling](scheduling.md)) or know anything about devices (see [panels](panels.md)).

## Core entities & relationships

`LcdHubClient --pipe "LcdHub"--> PipeServer -> Hub.Connect / Offer / Disconnect`

- **`LcdHubWire`**: message framing, the message types (Hello, Panels, Frame, Style,
  Visibility) and the pipe options. The doc comment on the class is the format spec.
- **`LcdHubClient`**: a producer's connection. It reconnects by itself, keeps only the newest
  frame per panel, and raises `PanelsChanged`, `StyleChanged` and `VisibilityChanged`.
- **`LcdHubStyle`**: dark or light, plus background, text and accent colours that every producer
  is asked to draw in, so the rotation doesn't jar. The hub makes it in `Style.cs` (`HubStyle`).
- **`LcdHubVisibility`**: per panel, Shown, Next or Hidden. Producers skip drawing a Hidden panel
  unless they are raising or standing down an alert.
- **`LcdHubPanel`**: id, width and height as announced to producers.
- **`PipeServer`**: an accept loop with one thread per producer. It checks the hello, then hands
  each frame to `Hub.Offer`.

## Invariants & rules

- **Additions never break a peer.** Each side ignores message types it doesn't know and bytes
  after the fields it reads, so a new message or a field appended to an existing one only raises
  `Version`. Only a change an older peer would misread raises `MinVersion`, below which the hub
  refuses a producer. The hub sends Style and Visibility to every producer; a v1 client ignores them.
- **Visibility defaults to Next on the client,** both before the hub has said anything and after
  every Panels message, so a hub too old to send it still gets every frame. The hub keeps
  `Producer.Told` and sends only changes, and clears it when it sends Panels.
- **A producer without a frame on a panel is told Next,** so a new producer still gets into the
  rotation. Otherwise: Shown for the pick, Next for what `Rotation.Pick` would choose when the
  turn ends, and Hidden otherwise, including while locked or with the monitors off.
- **The send loop ends when the reader thread does.** `IsConnected` only turns false after a
  failed write, so a producer with nothing new to send would never notice a hub restart.
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
- **A frame's extra fields go before the pixels,** because the pixels are the rest of the
  message. v3 flags a lifetime (`validFor`, or a toast's duration) with bit 2 and puts a u32 ms
  ahead of the pixels. A frame without one is byte for byte a v2 frame; a v2 hub drops one with a
  lifetime as the wrong size, so it never keeps a toast for good.
- **A toast is a second frame per panel,** kept beside the producer's ordinary one
  (`Producer.Toasts`), which rotates again once the toast expires. Like every frame, it goes when
  its producer disconnects, so a one-shot sender must stay connected for the toast's duration.
- **Frames with a lifetime expire on the hub** (`Hub.ExpireFrames`, on every refresh), as if
  withdrawn. That's how a producer guards against hanging: the hub can't tell a hung producer
  from one whose picture just didn't change.
- **The hub never writes to a producer under its gate.** A pipe write waits until the app reads,
  so `Producer.Send` queues and a writer thread per producer writes. A producer that doesn't read
  (busy, hung, or the Python client between polls) once froze the whole hub this way.
- **Every Panels message means "redraw everything".** The hub has forgotten the producer's
  frames. The client clears its pending frames and fires `PanelsChanged`, including on connect,
  and with an empty list on disconnect.
- **`Send` never blocks and takes ownership of the array.** Frames sent while disconnected are
  dropped.
- **Messages are bounded** (`MaxMessageBytes`). A bad length or truncated body throws
  `IOException`, which ends that one producer's connection only.

## Launched producers

`Launcher.cs` starts the `launch` entries in settings (path and args) and keeps them running.
`Launcher.Apply` takes the whole list, at start and on every settings reload: it starts new
entries and ends the apps it started for entries that are gone, leaving the rest alone.
After a run under 1 minute the retry delay doubles from 5 s up to 5 minutes; a longer run
resets it. An exe that is already running elsewhere (same full path) is checked again every
30 s instead of being started twice, because single-instance apps would otherwise exit and
retry forever. Apps are assigned to a `KILL_ON_JOB_CLOSE` job whose handle is never closed, so
the OS ends them with the hub. It doesn't change the pipe: a launched app connects like any other.

## Key files

| File | Role |
|---|---|
| `Client/LcdHubClient.cs` | `LcdHubClient`, `LcdHubPanel`, `LcdHubWire` (format doc on the class) |
| `Launcher.cs` | Starts and restarts the `launch` apps, inside a kill-on-close job |
| `PipeServer.cs` | Accept loop, hello and version check, frames → `Hub.Offer` |
| `Hub.cs` | `Connect` (sends the first Panels message, dedupes names), `Offer`, `Disconnect` |

## Gotchas

- **The producer name is user-facing and persistent.** It appears in the tray menu and keys
  "hidden on this panel", so keep it stable across versions of the app.
- **`PanelsChanged` runs on the client's thread,** not the producer's UI thread.
- **Panels are queued under the hub's gate** in `Hub.Connect` and `SyncPanels`, so a newer list
  can't be overtaken by an older one; the producer's writer thread keeps the order.
- **Other languages:** [`clients/python/lcdhub.py`](../clients/python/lcdhub.py) speaks the same
  wire with the standard library. Its handle is synchronous, so it has no reader thread and never
  blocks on a read: `poll()` reads only what `PeekNamedPipe` says has arrived.
- **Pixel layout:** top-down rows of little-endian `uint` BGRX, written as raw memory.
  Non-.NET producers must match it byte for byte.

## Executable references

`Tests/Program.cs`, the wire checks: hello, panels, style, visibility, and full-frame and empty-frame round-trips, trailing fields ignored,
back-to-back framing, clean EOF → null, truncated message throws, absurd length refused.

## Links

- Glossary: `UBIQUITOUS_LANGUAGE.md` § Producers and frames
- Related domains: [scheduling](scheduling.md) (what happens to an offered frame) and
  [panels](panels.md) (where panel ids and sizes come from).
