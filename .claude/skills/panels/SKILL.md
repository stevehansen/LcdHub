---
name: panels
description: Prime on the LcdHub panels domain before working on it: LcdDevice, LcdPanel, Link (HidLink/LyLink), JpegEncoder, device discovery, the stable panel id, keepalive, upside-down mounting and JPEG size limits. Use when the task touches USB, HID, WinUSB, a new Thermalright model, handshakes, "panel shows the logo", frame encoding or FindDevices. Not for what a panel shows or when (see scheduling) or the pipe to producers (see producer-protocol).
---

# Panels domain: priming

**Canonical spec:** [`docs/panels.md`](../../../docs/panels.md). Read it for depth: entities, all invariants, gotchas, and adding a device. Terms of record: `UBIQUITOUS_LANGUAGE.md` § Hardware.

This is the device side. `LcdPanel` turns finished BGRX frames into a lit panel. It never decides what to show (scheduling), and never sees producers (producer-protocol).

## Core invariants (get these right)

- **One process per panel.** WinUSB is exclusive, and HID frames would interleave. A failed open is usually TRCC.
- **Keepalive.** Resend every second, otherwise the firmware falls back to its logo.
- **The Panel id is persistent.** It keys saved settings, so never change its format.
- **`Present` takes ownership** and may reverse the array in place.

## Key files

- `Panel/LcdPanel.cs`: everything device-specific, including `FindDevices` and the `Link` subclasses.

## Gotchas

- An LY panel left with an unread ACK refuses everything until it is drained. Keep the drain before the handshake.
- Orientation comes from the device handshake. Don't flip pictures anywhere else.
