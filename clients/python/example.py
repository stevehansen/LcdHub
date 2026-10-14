"""A minimal LCD Hub producer: the time in big digits, in the hub's style.

    pip install pillow
    python example.py                  # joins the rotation
    python example.py --toast "Hi"     # shows "Hi" over the rotation for 5 seconds, then exits
"""
import sys
import time

from PIL import Image, ImageDraw, ImageFont

from lcdhub import HIDDEN, LcdHub


def draw(width, height, style, text):
    def rgb(c):
        return (c >> 16 & 0xFF, c >> 8 & 0xFF, c & 0xFF)

    image = Image.new("RGB", (width, height), rgb(style.background))
    font = ImageFont.truetype("segoeuib.ttf", height // 3)
    ImageDraw.Draw(image).text((width / 2, height / 2), text, font=font, fill=rgb(style.accent), anchor="mm")
    return image.tobytes("raw", "BGRX")


hub = LcdHub("Python clock")
deadline = time.monotonic() + 5
while not hub.panels and time.monotonic() < deadline:
    hub.poll()
    time.sleep(0.1)
if not hub.panels:
    sys.exit("LCD Hub isn't running, or has no panels.")

if len(sys.argv) > 2 and sys.argv[1] == "--toast":
    for p in hub.panels:
        hub.toast(p.id, draw(p.width, p.height, hub.style, sys.argv[2]), duration=5)
    # The hub forgets an app's frames when it disconnects, toasts included: stay until it's over.
    for _ in range(50):
        hub.poll()
        time.sleep(0.1)
    sys.exit()

shown = {}
while True:
    if hub.poll():
        shown.clear()  # the hub forgot our frames
    now = time.strftime("%H:%M")
    for p in hub.panels:
        if shown.get(p.id) == (now, hub.style) or hub.visibility(p.id) == HIDDEN:
            continue
        # Valid for two minutes: if this script hangs, the hub drops the stale time.
        hub.send(p.id, draw(p.width, p.height, hub.style, now), valid_for=120)
        shown[p.id] = (now, hub.style)
    time.sleep(1)
