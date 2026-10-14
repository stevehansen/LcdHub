# Ubiquitous Language

## Hardware

| Term | Definition | Aliases to avoid |
|---|---|---|
| **Panel** | One plugged-in USB LCD that the hub drives | screen, display, device, LCD, monitor |
| **Panel id** | A panel's stable identity across replugs and restarts (`0416:5302`, `0416:5408:<serial>`) | device path, handle |
| **Wire** | The USB protocol family a panel speaks (HID or LY bulk) | driver, transport |
| **Link** | An open, handshaken connection to a panel over its wire | connection, session |
| **Keepalive** | Resending the current picture every second so the panel's firmware doesn't fall back to its own screen | refresh, heartbeat |
| **Upside down** | A panel mounted so that pictures must be sent rotated 180° | flipped, inverted |

## Producers and frames

| Term | Definition | Aliases to avoid |
|---|---|---|
| **Hub** | The single process that owns every panel and decides what each one shows | server, host, daemon |
| **Producer** | An app connected to the hub that sends frames (called "app" in the UI) | client, source, sender |
| **Producer name** | The name a producer gives in its hello; the key used for hiding it on a panel | id, title |
| **Frame** | One complete top-down BGRX picture at exactly a panel's size, from a producer | image, bitmap, picture, screen |
| **Alert** | A flag on a frame asking the hub to show it ahead of the rotation | priority, urgent, focus |
| **Withdraw** | Sending an empty frame: the producer has nothing for that panel until its next frame (`Clear`) | hide, remove |
| **Panel list** | The panels the hub announces to producers; any change makes it forget their frames | panels message |

## Choosing what a panel shows

| Term | Definition | Aliases to avoid |
|---|---|---|
| **Rotation** | Cycling a panel through its pool, each entry staying for one interval | carousel, switching, template |
| **Rotation interval** | How long one entry stays on a panel before the rotation moves on | rotate seconds, dwell |
| **Offer** | A producer's newest frame for a panel, counted as a rotation candidate (with its alert flag) | candidate, entry |
| **Pool** | The offers a panel rotates among right now: the alerting ones if any, otherwise all visible ones plus Today | queue, playlist |
| **Pin** | A producer kept on one panel instead of rotating ("Keep showing" in the menu). Only alerts and takeovers interrupt it, and it counts only while that producer has a frame there | focus, lock, sticky |
| **Hidden** | A producer excluded from one panel's rotation | disabled, muted |
| **Pick** | The rotation's decision of what a panel shows now: a producer, Today, the feed, or blank | selection, choice, current |
| **Today** | The hub's own picture: time, date and weather, themed by the day, season and holidays | clock, idle screen, default |
| **Feed** | A hub-owned moving source that replaces a panel's rotation: a video or a mirrored window | stream, source, mode |
| **Takeover** | A session state that overrides every panel: locked → Today, display off → blank | override, privacy mode |
| **Blank** | The all-black picture shown while the monitors are off | off, empty |

## Relationships

- The **Hub** owns zero or more **Panels**. Each **Panel** has exactly one **Link** at a time, or none while reconnecting.
- A **Producer** holds at most one **Frame** per **Panel**, which is its **Offer** for that panel.
- A **Panel** has at most one **Feed**, and a **Feed** belongs to exactly one **Panel**.
- The **Pick** is chosen in this order: **Takeover**, then **Alert** **Offers**, then the **Pin**, then **Feed**, then **Rotation** of the **Pool**. A **Panel** has a **Pin** or a **Feed**, never both.

## Example dialogue

> **Dev:** "When a usage-meter **Producer** raises an **Alert**, does the video **Feed** stop?"
> **Domain expert:** "It pauses. The **Pick** becomes the alerting **Producer**, and the **Feed** stays on the **Panel**, so it comes back as soon as the alert clears."
> **Dev:** "And if two **Producers** alert at once?"
> **Domain expert:** "The **Pool** is just the alerting **Offers**, so those two rotate every **Rotation interval**. The non-alerting ones and **Today** sit out."
> **Dev:** "What if the user locked the PC meanwhile?"
> **Domain expert:** "The **Takeover** beats everything: **Today** is shown and no **Frame** reaches the **Panel**, alert or not."

## Flagged ambiguities

- **"Screen"** meant three things: the hub's per-panel state class, the panel firmware's own fallback screen (its logo), and Windows monitors. *Resolved:* the class is now `PanelSlot`. Say "firmware screen" for the fallback and "monitor" for Windows displays, and don't name a type `Screen`.
- **"App" vs "Producer" vs "Client"**: the UI says "app", the code says `Producer`, and the library is `LcdHubClient`. Recommendation: use **Producer** in code and docs, keep "app" only in user-facing strings, and treat `LcdHubClient` as the producer's library rather than a domain term.
- **"Offer"** names both `Hub.Offer` (a producer handing in a frame) and `Rotation.Offer` (a rotation candidate). These are one idea seen from two sides, so keep the term, but don't use it for anything else.
- **Pick vs last sent**: `PanelSlot.Pick` is what the rotation chose, and `PanelSlot.LastSent` is the picture actually handed to the panel, used to avoid resending it. These were once called `Showing`/`Shown`; don't bring those names back.
- **"Source"** was used loosely for producers, feeds and video files. Avoid it, and say **Producer** or **Feed**.
