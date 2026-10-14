# Panels

Finding the supported USB LCDs, driving each one over its wire, and keeping it lit.

**Status:** current as of the initial release. No single PRD (foundational domain).
**Priming skill:** [`.claude/skills/panels/SKILL.md`](../.claude/skills/panels/SKILL.md)

## What it is

The device side of the hub. A **Panel** takes a finished top-down BGRX picture at its own size.
Everything else stays inside `Panel/LcdPanel.cs`: the wire, the handshake, which way up it is
mounted, JPEG encoding, reconnecting and the keepalive. It does *not* decide what a panel shows;
that belongs to [scheduling](scheduling.md).

## Core entities & relationships

`LcdDevice -> LcdPanel -> Link (HidLink | LyLink) -> JpegEncoder`

- **`LcdDevice`**: one plugged-in panel as found by `FindDevices`. Its `Path` identifies this
  connection, and its **Panel id** (`Id`) survives replugs and restarts.
- **`LcdPanel`**: drives one device on a sender thread of its own. `Present` queues a frame.
- **`Link`**: an open, handshaken connection over one **Wire**. A link knows whether the panel is
  **upside down**, wraps a JPEG in its wire's framing (`Frame`), and sends it (`Write`).
- **`JpegEncoder`**: BGRX → JPEG through the GDI+ flat API.

## Invariants & rules

- **One process per panel.** WinUSB allows a single opener, and HID frames from two writers
  interleave. A failed open is usually TRCC holding the device. Owned by `Panel/LcdPanel.cs`
  (`HidLink.Connect`, `LyLink.Connect`).
- **Keepalive: resend every second, changed or not.** Both firmwares fall back to their own screen
  a few seconds after the last frame. Owned by `LcdPanel.Run`.
- **The Panel id must stay stable.** It keys per-panel settings in
  [scheduling](scheduling.md). For `0416:5302` it is the bare USB id; for `0416:5408` it also
  carries the USB serial. Changing the format orphans every saved choice. Owned by `FindDevices`.
- **`Present` keeps the array and never changes it.** The panel keeps the upright picture it
  shows (`shown`), and an upside-down panel encodes a reversed copy. Callers mustn't change the
  array afterwards either.
- **`fade: true` blends over 400 ms** from whatever is on the panel (a fade in progress included)
  to the new frame. Steps go out back to back at motion quality, and the last one at the frame's
  own quality. A fade request survives a newer frame replacing the pending one. `--diagnose` logs
  the step count.
- **Only the newest frame is kept.** A caller that outruns the panel just loses frames;
  `HasPendingFrame` lets a feed skip making them.
- **JPEGs are capped.** The 9.16" silently keeps its old picture when sent one that is too big,
  so the encoder steps quality down until the frame fits. Motion frames start at lower quality.
  See the constants at the top of `LcdPanel`.

## Key files

| File | Role |
|---|---|
| `Panel/LcdPanel.cs` | `LcdDevice`, `LcdPanel`, the `Link`s, `JpegEncoder`, the P/Invoke surface |
| `Diagnose.cs` | `--diagnose` log; the panel also writes an upright copy of each picture to `%TEMP%` |

## Gotchas

- **LY handshake after a crash.** A sender stopped between a frame and its ACK leaves a reply
  unread, and the firmware then refuses everything until that reply is read. `LyLink` drains
  replies before its handshake.
- **The LY bulk endpoint varies.** At least one unit takes `0x09` where trcc-linux uses `0x01`, so
  the link queries the pipes rather than hard-coding the endpoint.
- **Orientation comes from the device.** The 9.16" reports a sub-model in its handshake reply, and
  TRCC sends turned 180° for all but a few of them. Don't flip pictures anywhere else.
- **The panel can't be screenshotted.** Use `--diagnose` and look at
  `%TEMP%\lcd-hub-lcd-<w>x<h>.jpg`.

## Adding a device

Add its USB id and size to `FindDevices`. If it speaks a new protocol, add a `LcdWire` value and a
`Link` subclass. Protocol notes:
[thermalright-trcc-linux](https://github.com/Lexonight1/thermalright-trcc-linux).

## Executable references

None: the device layer has no automated tests, since it needs the hardware. Verify on a real panel
with `--diagnose`.

## Links

- Glossary: `UBIQUITOUS_LANGUAGE.md` § Hardware
- Related domains: [scheduling](scheduling.md) (decides what each panel shows, and owns the
  per-panel settings keyed by Panel id) and [producer-protocol](producer-protocol.md) (announces
  panel ids and sizes to producers).
