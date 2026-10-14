"""LCD Hub producer client for Python (Windows, standard library only).

The same contract as Client/LcdHubClient.cs, for apps that aren't .NET: connect to the hub,
learn the panels, send whole frames. Frames are top-down BGRX bytes, exactly width*height*4
long; with Pillow that's ``image.convert("RGB").tobytes("raw", "BGRX")``.

Unlike the C# client this one has no thread of its own. Call ``poll()`` now and then (each
loop of your app is fine): it connects or reconnects as needed and applies whatever the hub
said. It returns True when the panels changed, which means the hub has forgotten your frames
and you should draw every panel again.

The pipe is opened synchronously, and a synchronous pipe handle makes a write wait behind a
pending read, so this client never blocks on a read: it only reads what PeekNamedPipe says
has already arrived.
"""
from __future__ import annotations

import ctypes
import msvcrt
import struct
from ctypes import wintypes
from dataclasses import dataclass

PIPE = r"\\.\pipe\LcdHub"
VERSION = 3
MSG_HELLO, MSG_PANELS, MSG_FRAME, MSG_STYLE, MSG_VISIBILITY = 1, 2, 3, 4, 5
FLAG_ALERT, FLAG_TOAST, FLAG_LIFETIME = 1, 2, 4
HIDDEN, NEXT, SHOWN = 0, 1, 2

_peek = ctypes.windll.kernel32.PeekNamedPipe
_peek.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p,
                  ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
_peek.restype = wintypes.BOOL


@dataclass(frozen=True)
class Panel:
    id: str
    width: int
    height: int


@dataclass(frozen=True)
class Style:
    """The look the hub asks every app to share. Colours are 0xRRGGBB."""
    light: bool = False
    background: int = 0x141414
    text: int = 0xE6E6E6
    accent: int = 0x3B82F6


class LcdHub:
    def __init__(self, name: str):
        """``name`` is shown in the hub's menu and remembered there, so keep it stable."""
        self.name = name
        self.panels: list[Panel] = []
        self.style = Style()
        self._visibility: dict[str, int] = {}
        self._pipe = None
        self._buffer = b""

    @property
    def connected(self) -> bool:
        return self._pipe is not None

    def visibility(self, panel_id: str) -> int:
        """HIDDEN, NEXT or SHOWN. Skip drawing a HIDDEN panel unless you're alerting."""
        return self._visibility.get(panel_id, NEXT)

    def poll(self) -> bool:
        """Connects if needed and reads what the hub sent. True when the panels changed."""
        if self._pipe is None and not self._connect():
            return False
        changed = False
        try:
            while (message := self._read()) is not None:
                kind, body = message
                if kind == MSG_PANELS:
                    self.panels = _parse_panels(body)
                    self._visibility.clear()
                    changed = True
                elif kind == MSG_STYLE:
                    flags, bg, text, accent = struct.unpack_from("<BIII", body)
                    self.style = Style(bool(flags & 1), bg, text, accent)
                elif kind == MSG_VISIBILITY:
                    panel_id, at = _str(body, 0)
                    self._visibility[panel_id] = body[at]
                # Other types are from a newer hub, and ignored.
        except OSError:
            changed = self._drop() or changed
        return changed

    def send(self, panel_id: str, pixels: bytes, alert: bool = False, valid_for: float = 0) -> None:
        """Puts a frame on a panel. ``alert`` jumps the rotation until a frame without it.
        ``valid_for`` (seconds) has the hub drop the frame if no newer one came by then."""
        self._frame(panel_id, pixels, alert, False, valid_for)

    def toast(self, panel_id: str, pixels: bytes, duration: float = 5) -> None:
        """Shows a frame at once, over the rotation, for ``duration`` seconds."""
        self._frame(panel_id, pixels, False, True, duration if duration > 0 else 5)

    def clear(self, panel_id: str) -> None:
        """Takes your frame off a panel until you send again."""
        self._frame(panel_id, b"", False, False, 0)

    def close(self) -> None:
        self._drop()

    # ---- wire ----

    def _connect(self) -> bool:
        try:
            self._pipe = open(PIPE, "r+b", buffering=0)
            self._write(MSG_HELLO, struct.pack("<H", VERSION) + _pack_str(self.name))
            return True
        except OSError:
            self._pipe = None
            return False

    def _drop(self) -> bool:
        """Forgets the connection; True if there were panels, which are gone now."""
        if self._pipe is not None:
            try:
                self._pipe.close()
            except OSError:
                pass
        self._pipe, self._buffer = None, b""
        had = bool(self.panels)
        self.panels, self._visibility = [], {}
        return had

    def _frame(self, panel_id: str, pixels: bytes, alert: bool, toast: bool, lifetime: float) -> None:
        if self._pipe is None:
            return  # dropped while disconnected, as in the C# client
        ms = min(int(lifetime * 1000), 0xFFFFFFFF)
        flags = (FLAG_ALERT if alert else 0) | (FLAG_TOAST if toast else 0) | (FLAG_LIFETIME if ms else 0)
        body = _pack_str(panel_id) + bytes([flags]) + (struct.pack("<I", ms) if ms else b"") + pixels
        try:
            self._write(MSG_FRAME, body)
        except OSError:
            self._drop()

    def _write(self, kind: int, body: bytes) -> None:
        self._pipe.write(struct.pack("<IB", len(body) + 1, kind) + body)

    def _read(self):
        """The next whole message if it has arrived, else None; never blocks."""
        while True:
            if len(self._buffer) >= 5:
                (length,) = struct.unpack_from("<I", self._buffer)
                if len(self._buffer) >= 4 + length:
                    kind, body = self._buffer[4], self._buffer[5:4 + length]
                    self._buffer = self._buffer[4 + length:]
                    return kind, body
            available = wintypes.DWORD()
            handle = msvcrt.get_osfhandle(self._pipe.fileno())
            if not _peek(handle, None, 0, None, ctypes.byref(available), None):
                raise OSError("pipe closed")
            if available.value == 0:
                return None
            self._buffer += self._pipe.read(available.value)


def _pack_str(s: str) -> bytes:
    data = s.encode()
    return struct.pack("<H", len(data)) + data


def _str(body: bytes, at: int) -> tuple[str, int]:
    (n,) = struct.unpack_from("<H", body, at)
    return body[at + 2:at + 2 + n].decode(), at + 2 + n


def _parse_panels(body: bytes) -> list[Panel]:
    (count,) = struct.unpack_from("<H", body)
    panels, at = [], 2
    for _ in range(count):
        panel_id, at = _str(body, at)
        width, height = struct.unpack_from("<HH", body, at)
        panels.append(Panel(panel_id, width, height))
        at += 4
    return panels
