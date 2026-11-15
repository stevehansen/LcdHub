using System.Collections.Concurrent;
using LcdHub.Client;

namespace LcdHub;

/// <summary>A connected app. Its frames are written by its pipe thread, under the hub's gate.</summary>
internal sealed class Producer
{
    // A pipe write waits until the app reads, and an app may not read for a while (busy,
    // hung, or a client without a reader thread). Messages queue here and a thread of the
    // producer's own writes them, so the hub never waits on an app while holding its gate.
    private readonly BlockingCollection<byte[]> _outbox = new();

    public Producer(string name, Action<byte[]> write)
    {
        Name = name;
        new Thread(() =>
        {
            try { foreach (var message in _outbox.GetConsumingEnumerable()) write(message); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true, Name = $"Producer writer {name}" }.Start();
    }

    public string Name { get; }
    // Panel id → the newest frame for it, and the newest toast, which shows over it until it expires.
    public readonly Dictionary<string, Frame> Frames = new();
    public readonly Dictionary<string, Frame> Toasts = new();

    /// <summary>The toast on this panel if there is one, else the ordinary frame.</summary>
    public Frame? FrameFor(string panelId) => Toasts.GetValueOrDefault(panelId) ?? Frames.GetValueOrDefault(panelId);
    // Panel id → the visibility last sent; a panel missing here is Next, as the client assumes.
    public readonly Dictionary<string, LcdHubVisibility> Told = new();
    // Its items in the status strips, by its own keys.
    public readonly Dictionary<string, StatusItem> Statuses = new();

    /// <summary>Never blocks. Best effort: a producer that has gone is reaped by its own pipe thread.</summary>
    public void Send(byte[] message)
    {
        try { _outbox.Add(message); }
        catch (InvalidOperationException) { } // closed
    }

    /// <summary>Ends the writer thread once it has written what's queued.</summary>
    public void Close() => _outbox.CompleteAdding();
}

/// <summary>One picture from a producer. Compared by reference: a new frame is a new object.</summary>
/// <param name="expiresAt">Environment.TickCount64 when the hub drops it, or 0 for never.</param>
internal sealed class Frame(uint[] pixels, bool alert, bool toast = false, long expiresAt = 0)
{
    public uint[] Pixels { get; } = pixels;
    public bool Alert { get; } = alert;
    public bool Toast { get; } = toast;
    public long ExpiresAt { get; } = expiresAt;
}

/// <summary>One line a producer keeps in the status strips.</summary>
/// <param name="ExpiresAt">Environment.TickCount64 when the hub drops it, or 0 for never.</param>
internal sealed record StatusItem(string Text, LcdHubStatusLevel Level, uint? Color, long ExpiresAt);

/// <summary>What a status strip looks like: redrawn when the items, the minute (with a clock), the page or the style change.</summary>
internal readonly record struct StatusKey(int Version, string? Minute, int Page, LcdHubStyle Style);

/// <summary>What Today looks like: redrawn when the minute, the weather or the options change.</summary>
internal readonly record struct TodayKey(string Minute, Weather? Weather, int Version);

/// <summary>A plugged-in panel, cut into regions by its layout. Written on the UI thread under the hub's gate.</summary>
internal sealed class PanelSlot(LcdPanel panel)
{
    public LcdPanel Panel { get; } = panel;
    public string Id => Panel.Device.Id;
    // The whole panel first (what is left of it), then the strips its layout cuts off.
    public List<Region> Regions = [];
    // A takeover's content (TodayKey or Rotation.Blank) while it holds the whole panel, so it's
    // sent once; null while the regions make up the panel.
    public object? Takeover;
}

/// <summary>
/// A part of a panel with a rotation of its own: the whole panel, or a strip its layout cuts off
/// (<see cref="Layout"/>). Producers see each as a panel; its id is theirs. Written on the UI
/// thread under the hub's gate.
/// </summary>
internal sealed class Region(PanelSlot slot, RegionRect rect)
{
    public PanelSlot Slot { get; } = slot;
    public RegionRect Rect { get; } = rect;
    public string Id => Rect.Id;
    public int Width => Rect.Width;
    public int Height => Rect.Height;
    /// <summary>The layout entry's name; null for the panel itself.</summary>
    public string? Name => Id.Length > Slot.Id.Length ? Id[(Slot.Id.Length + 1)..] : null;
    public LcdFeed? Feed;
    // A Rotation pick: a producer's name or one of Rotation's constants.
    public string? Pick;
    public long PickedAt;
    // What was last drawn here — a Frame, a TodayKey, Rotation.Blank — so it's drawn once.
    public object? LastSent;
    // What it shows, kept for putting the panel together. Never changed once set.
    public uint[]? Pixels;
    /// <summary>A status strip, drawn by the hub: no rotation, no feed, not told to producers.</summary>
    public bool IsStatus => Rect.Status;
    // A status strip's page count when last drawn.
    public int Pages = 1;
}

/// <summary>
/// Owns the panels and decides, every second and whenever a producer sends something, what
/// each one shows (<see cref="Rotation"/>). Producers' pipe threads only record frames and
/// ask for a refresh; panels, feeds and settings are handled on the UI thread.
/// </summary>
internal static class Hub
{
    private static readonly object _gate = new();
    private static readonly List<PanelSlot> _slots = new();
    private static readonly List<Producer> _producers = new();
    // While locked the panels show only Today; while the monitors are off, black. Black,
    // not letting go: without frames a panel falls back to its own, still-lit screen.
    private static bool _locked, _displayOff;
    private static HubSettings _settings = new();
    private static IntPtr _hwnd;
    private static int _refreshPosted;
    // Bumped when the Today options change, so Today is drawn again.
    private static int _todayVersion;
    // Bumped under the gate whenever any producer's status items change, so the strips are drawn again.
    private static int _statusVersion;
    private static LcdHubStyle _style = LcdHubStyle.Default;

    public static void Start(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _settings = HubSettings.Load(out _);
        _style = HubStyle.Current(_settings.Style);
        WeatherSource.Start(_settings.Today.WeatherLocation, PostRefresh);
        Launcher.Apply(_settings.Launch);
        Tick();
    }

    public static void Stop()
    {
        foreach (var slot in Slots)
        {
            foreach (var region in slot.Regions) SetFeed(region, null, save: false);
            slot.Panel.Dispose();
        }
        lock (_gate) { _slots.Clear(); }
    }

    /// <summary>UI thread, every second: notices panels plugged in or out, then refreshes.</summary>
    public static void Tick()
    {
        SyncPanels();
        SyncStyle();
        SyncBrightness();
        Refresh();
    }

    public static void SetTakeover(bool? locked = null, bool? displayOff = null)
    {
        lock (_gate)
        {
            _locked = locked ?? _locked;
            _displayOff = displayOff ?? _displayOff;
        }
        Refresh();
    }

    // ---------------- producers (pipe threads) ----------------

    public static Producer Connect(string name, Action<byte[]> send)
    {
        lock (_gate)
        {
            var unique = name;
            for (int i = 2; _producers.Exists(p => p.Name == unique); i++) unique = $"{name} ({i})";
            var producer = new Producer(unique, send);
            _producers.Add(producer);
            // Under the gate, so it can't overtake a newer list broadcast by SyncPanels.
            producer.Send(LcdHubWire.Panels(PanelListLocked()));
            producer.Send(LcdHubWire.Style(_style));
            Diagnose.Log($"hub: {unique} connected");
            return producer;
        }
    }

    public static void Disconnect(Producer producer)
    {
        lock (_gate)
        {
            _producers.Remove(producer);
            if (producer.Statuses.Count > 0) _statusVersion++;
        }
        producer.Close();
        Diagnose.Log($"hub: {producer.Name} disconnected");
        PostRefresh();
    }

    public static void Offer(Producer producer, LcdHubWire.FrameMessage m)
    {
        lock (_gate)
        {
            var region = FindRegionLocked(m.PanelId);
            if (region is null) return;
            var frames = m.Toast ? producer.Toasts : producer.Frames;
            // An empty frame withdraws: the producer has nothing to show there for now.
            if (m.Pixels.Length == 0) frames.Remove(m.PanelId);
            // A frame drawn for the wrong size (the panel has just been swapped) is dropped.
            else if (m.Pixels.Length == region.Width * region.Height)
                frames[m.PanelId] = new Frame(m.Pixels, m.Alert, m.Toast, m.LifetimeMs > 0 ? Environment.TickCount64 + m.LifetimeMs : 0);
            else return;
        }
        PostRefresh();
    }

    /// <summary>Sets or (with empty text) withdraws one of a producer's status items, shown on every status strip.</summary>
    public static void SetStatus(Producer producer, LcdHubWire.StatusMessage m)
    {
        lock (_gate)
        {
            if (m.Text.Length == 0)
            {
                if (!producer.Statuses.Remove(m.Key)) return;
            }
            else
            {
                var item = new StatusItem(m.Text, m.Level, m.Color, m.LifetimeMs > 0 ? Environment.TickCount64 + m.LifetimeMs : 0);
                // Only a change redraws, so a producer may repeat an item as often as it likes.
                if (producer.Statuses.GetValueOrDefault(m.Key) is { } old && old with { ExpiresAt = item.ExpiresAt } == item)
                {
                    producer.Statuses[m.Key] = item;
                    return;
                }
                producer.Statuses[m.Key] = item;
            }
            _statusVersion++;
        }
        PostRefresh();
    }

    /// <summary>Every producer's status items, worst level first, then by producer and key. Under the gate.</summary>
    private static List<StatusStrip.Line> StatusLinesLocked() =>
        _producers.SelectMany(p => p.Statuses.Select(s => (Producer: p.Name, s.Key, Item: s.Value)))
            .OrderByDescending(s => s.Item.Level)
            .ThenBy(s => s.Producer, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => new StatusStrip.Line(s.Item.Text, s.Item.Color ?? StatusStrip.LevelColor(s.Item.Level, _style)))
            .ToList();

    /// <summary>Drops frames, toasts and status items whose lifetime has run out. Under the gate.</summary>
    private static void ExpireFrames(long now)
    {
        foreach (var p in _producers)
            foreach (var (key, _) in p.Statuses.Where(s => s.Value.ExpiresAt != 0 && s.Value.ExpiresAt <= now).ToList())
            {
                p.Statuses.Remove(key);
                _statusVersion++;
                Diagnose.Log($"hub: {p.Name}'s status {key} expired");
            }
        foreach (var p in _producers)
            foreach (var frames in (Dictionary<string, Frame>[])[p.Frames, p.Toasts])
                foreach (var (panelId, f) in frames.Where(f => f.Value.ExpiresAt != 0 && f.Value.ExpiresAt <= now).ToList())
                {
                    frames.Remove(panelId);
                    Diagnose.Log($"hub: {p.Name}'s {(f.Toast ? "toast" : "frame")} on {panelId} expired");
                }
    }

    private static void PostRefresh()
    {
        if (Interlocked.Exchange(ref _refreshPosted, 1) == 0)
            NativeMethods.PostMessageW(_hwnd, Win32Consts.WM_APP_REFRESH, IntPtr.Zero, IntPtr.Zero);
    }

    // Producers are told about rotating regions: to them each is a panel. Status strips are the hub's.
    private static List<LcdHubPanel> PanelListLocked() =>
        _slots.SelectMany(s => s.Regions).Where(r => !r.IsStatus).Select(r => new LcdHubPanel(r.Id, r.Width, r.Height)).ToList();

    private static Region? FindRegionLocked(string id)
    {
        foreach (var slot in _slots)
            foreach (var region in slot.Regions)
                if (region.Id == id && !region.IsStatus) return region;
        return null;
    }

    // ---------------- what each panel shows (UI thread) ----------------

    public static void Refresh()
    {
        Interlocked.Exchange(ref _refreshPosted, 0);
        var now = Environment.TickCount64;
        var interval = TimeSpan.FromSeconds(_settings.RotateSeconds);
        var own = _settings.AppSeconds.ToDictionary(a => a.Key, a => TimeSpan.FromSeconds(a.Value));
        var clock = DateTime.Now;
        var today = new TodayKey(clock.ToString("yyyyMMddHHmm"), WeatherSource.Current, _todayVersion);
        lock (_gate)
        {
            ExpireFrames(now);
            // A takeover fills the whole panel, whatever its layout.
            object? takeover = _displayOff ? Rotation.Blank : _locked ? today : null;
            foreach (var slot in _slots)
            {
                bool switched = false, changed = false;
                foreach (var region in slot.Regions)
                {
                    if (region.IsStatus)
                    {
                        if (takeover is not null) continue;
                        var withClock = _settings.Panels.GetValueOrDefault(slot.Id)?.Layout.Find(e => e.Region == region.Name)?.Clock ?? true;
                        var key = new StatusKey(_statusVersion, withClock ? today.Minute : null,
                            (int)(now / (long)interval.TotalMilliseconds % region.Pages), _style);
                        if (Equals(key, region.LastSent)) continue;
                        region.LastSent = key;
                        if (StatusStrip.Paint(region.Width, region.Height, StatusLinesLocked(), withClock ? clock : null, _style,
                                key.Page, out region.Pages) is not { } strip) continue;
                        region.Pixels = strip;
                        changed = true;
                        continue;
                    }
                    var settings = _settings.Panels.GetValueOrDefault(region.Id);
                    var offers = new List<Rotation.Offer>();
                    foreach (var p in _producers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                        if (p.FrameFor(region.Id) is not null && settings?.Hidden.Contains(p.Name) != true)
                            offers.Add(new Rotation.Offer(p.Name, p.Frames.GetValueOrDefault(region.Id)?.Alert == true, p.Toasts.ContainsKey(region.Id)));

                    var pick = Rotation.Pick(offers, settings?.Pinned, settings?.Today ?? false, region.Feed is not null, _locked, _displayOff,
                        region.Pick, TimeSpan.FromMilliseconds(now - region.PickedAt), interval, own);
                    if (pick != region.Pick)
                    {
                        Diagnose.Log($"hub: {region.Id} shows {pick.TrimStart('\0')}");
                        (region.Pick, region.PickedAt, switched) = (pick, now, true);
                    }
                    if (region.Feed is { } feed) feed.Paused = pick != Rotation.Feed;
                    // Whatever would follow if this turn ended now.
                    var next = Rotation.Pick(offers, settings?.Pinned, settings?.Today ?? false, region.Feed is not null, _locked, _displayOff,
                        pick, TimeSpan.MaxValue, interval, own);
                    TellVisibility(region.Id, settings, pick, next);
                    if (takeover is not null) continue;

                    object? content = pick switch
                    {
                        Rotation.Feed => null,
                        Rotation.Blank => Rotation.Blank,
                        Rotation.Today => today,
                        _ => Content(_producers.Find(p => p.Name == pick)!, region.Id, offers.Exists(o => o.Alert)),
                    };
                    if (content is null) { region.LastSent = null; continue; } // the feed draws for itself
                    if (Equals(content, region.LastSent)) continue;
                    region.LastSent = content;
                    var pixels = content switch
                    {
                        Frame frame => frame.Pixels,
                        Rotation.Blank => new uint[region.Width * region.Height],
                        _ => Today.Paint(region.Width, region.Height, clock, today.Weather, _settings.Today, _style),
                    };
                    if (pixels is null) continue;
                    region.Pixels = pixels;
                    changed = true;
                }

                if (takeover is not null)
                {
                    if (Equals(takeover, slot.Takeover)) continue;
                    var fade = slot.Takeover?.GetType() != takeover.GetType();
                    slot.Takeover = takeover;
                    foreach (var region in slot.Regions) region.LastSent = null; // drawn again once it's over
                    var (w, h) = (slot.Panel.Width, slot.Panel.Height);
                    var pixels = takeover is TodayKey ? Today.Paint(w, h, clock, today.Weather, _settings.Today, _style) : new uint[w * h];
                    if (pixels is not null) slot.Panel.Present(pixels, fade: fade);
                    continue;
                }
                slot.Takeover = null;
                if (changed) slot.Panel.Present(Compose(slot), fade: switched);
            }
        }
    }

    /// <summary>
    /// The panel's picture: its only region's own array when that covers it (no copy), else the
    /// regions' pictures put together, black where one has none yet. Under the gate.
    /// </summary>
    private static uint[] Compose(PanelSlot slot)
    {
        var (w, h) = (slot.Panel.Width, slot.Panel.Height);
        if (slot.Regions is [{ Pixels: { } whole } only] && only.Width == w && only.Height == h) return whole;
        var pixels = new uint[w * h];
        foreach (var r in slot.Regions)
        {
            if (r.Pixels is not { } src) continue;
            for (int row = 0; row < r.Height; row++)
                src.AsSpan(row * r.Width, r.Width).CopyTo(pixels.AsSpan((r.Rect.Y + row) * w + r.Rect.X));
        }
        return pixels;
    }

    /// <summary>A producer's alert while alerts rule the panel, else its toast over its ordinary frame.</summary>
    private static Frame Content(Producer p, string panelId, bool alerting) =>
        alerting ? p.Frames[panelId] : p.FrameFor(panelId)!;

    /// <summary>
    /// Lets each producer skip drawing what nobody will see: Shown while on the panel, Next while
    /// up next or before its first frame here (so it can join), Hidden otherwise. Under the gate.
    /// </summary>
    private static void TellVisibility(string panelId, PanelSettings? settings, string pick, string next)
    {
        foreach (var p in _producers)
        {
            var visibility = p.Name == pick ? LcdHubVisibility.Shown
                : _locked || _displayOff || settings?.Hidden.Contains(p.Name) == true ? LcdHubVisibility.Hidden
                : p.Name == next || p.FrameFor(panelId) is null ? LcdHubVisibility.Next
                : LcdHubVisibility.Hidden;
            if (p.Told.GetValueOrDefault(panelId, LcdHubVisibility.Next) == visibility) continue;
            p.Told[panelId] = visibility;
            p.Send(LcdHubWire.Visibility(panelId, visibility));
            Diagnose.Log($"hub: {p.Name} on {panelId} is {visibility}");
        }
    }

    /// <summary>Called on feed threads. Under the gate, so a frame can't land after a switch away from the feed.</summary>
    private static void PresentFeedFrame(LcdFeed feed, uint[] frame)
    {
        lock (_gate)
        {
            foreach (var slot in _slots)
                foreach (var region in slot.Regions)
                    if (region.Feed == feed && region.Pick == Rotation.Feed)
                    {
                        region.Pixels = frame;
                        slot.Panel.Present(Compose(slot), motion: true);
                    }
        }
    }

    /// <summary>
    /// Keeps one slot per plugged-in panel: starts driving each new one, back on what it last
    /// showed, lets go of those unplugged, and tells producers when the list changed.
    /// </summary>
    private static void SyncPanels()
    {
        var devices = LcdPanel.FindDevices();
        var changed = false;
        foreach (var slot in Slots)
        {
            if (devices.Exists(d => d.Path == slot.Panel.Device.Path)) continue;
            DropRegions(slot);
            lock (_gate) { _slots.Remove(slot); }
            slot.Panel.Dispose();
            changed = true;
            Diagnose.Log($"hub: {slot.Id} gone");
        }
        foreach (var device in devices)
        {
            if (Slots.Any(s => s.Panel.Device.Path == device.Path)) continue;
            var slot = new PanelSlot(new LcdPanel(device));
            lock (_gate) { _slots.Add(slot); }
            changed = true;
            Diagnose.Log($"hub: {device.Id} found, {device.Width}x{device.Height}");
            CarveRegions(slot);
        }
        if (changed) AnnouncePanels();
    }

    /// <summary>Cuts the panel up by its layout and starts each region's saved video. Announce the panels afterwards.</summary>
    private static void CarveRegions(PanelSlot slot)
    {
        var rects = CarveLayout(slot);
        lock (_gate)
        {
            slot.Regions = rects.ConvertAll(r => new Region(slot, r));
            slot.Takeover = null;
        }
        foreach (var region in slot.Regions)
            if (!region.IsStatus && _settings.Panels.GetValueOrDefault(region.Id)?.VideoPath is { } video) PlayVideo(region, video);
    }

    /// <summary>The panel's regions by its layout; strips that don't fit go under ⚠ Settings.</summary>
    private static List<RegionRect> CarveLayout(PanelSlot slot)
    {
        var rects = Layout.Carve(slot.Id, slot.Panel.Width, slot.Panel.Height,
            _settings.Panels.GetValueOrDefault(slot.Id)?.Layout ?? [], out var skipped);
        foreach (var e in skipped)
        {
            var problem = $"{Strings.SettingsLayoutTooBig}: panels.{slot.Id}.layout \"{e.Region}\"";
            if (!_settings.Problems.Contains(problem)) _settings.Problems.Add(problem);
        }
        return rects;
    }

    /// <summary>Stops the panel's feeds and forgets producers' frames for its regions.</summary>
    private static void DropRegions(PanelSlot slot)
    {
        foreach (var region in slot.Regions) SetFeed(region, null, save: false);
        lock (_gate)
        {
            foreach (var region in slot.Regions)
                foreach (var p in _producers)
                {
                    p.Frames.Remove(region.Id);
                    p.Toasts.Remove(region.Id);
                }
            slot.Regions = [];
        }
    }

    /// <summary>Tells every producer the panels (regions) there are now; they redraw for all of them.</summary>
    private static void AnnouncePanels()
    {
        lock (_gate)
        {
            var panels = LcdHubWire.Panels(PanelListLocked());
            foreach (var p in _producers)
            {
                p.Send(panels);
                p.Told.Clear(); // the client is back to Next for every panel
            }
        }
    }

    private static void SyncStyle()
    {
        var style = HubStyle.Current(_settings.Style);
        lock (_gate)
        {
            if (style == _style) return;
            _style = style;
            var message = LcdHubWire.Style(style);
            foreach (var p in _producers) p.Send(message);
        }
        _todayVersion++;
        Diagnose.Log($"hub: style now {(style.Light ? "light" : "dark")}, accent {style.Accent:X6}");
    }

    /// <summary>Dims every panel toward night_brightness through the twilight after sunset, and back before sunrise.</summary>
    private static void SyncBrightness()
    {
        var now = DateTime.Now;
        var weather = WeatherSource.Current;
        // Today's times of yesterday's sunrise and sunset beat a guess, if the weather is stale.
        DateTime? OnToday(DateTime? t) => t is { } v ? now.Date + v.TimeOfDay : null;
        var daylight = Theme.Daylight(now, OnToday(weather?.Sunrise), OnToday(weather?.Sunset));
        var night = _settings.NightBrightness;
        var brightness = (int)Math.Round(night + (100 - night) * daylight);
        foreach (var slot in Slots) slot.Panel.Brightness = brightness;
    }

    // ---------------- the menu's view and commands (UI thread) ----------------

    public static List<PanelSlot> Slots
    {
        get { lock (_gate) return _slots.ToList(); }
    }

    /// <summary>Every panel's rotating regions, in panel order; a panel without a layout is one region.</summary>
    public static List<Region> Regions
    {
        get { lock (_gate) return _slots.SelectMany(s => s.Regions).Where(r => !r.IsStatus).ToList(); }
    }

    public static List<string> ProducerNames
    {
        get { lock (_gate) return _producers.Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    public static int RotateSeconds
    {
        get => _settings.RotateSeconds;
        set
        {
            _settings.RotateSeconds = value;
            _settings.Save();
        }
    }

    /// <summary>An app's own seconds on screen, or null when it takes <see cref="RotateSeconds"/> like the rest.</summary>
    public static int? AppSeconds(string producer) => _settings.AppSeconds.TryGetValue(producer, out var s) ? s : null;

    public static void SetAppSeconds(string producer, int? seconds)
    {
        if (seconds is { } s) _settings.AppSeconds[producer] = s;
        else _settings.AppSeconds.Remove(producer);
        _settings.Save();
    }

    /// <summary>Percent the panels dim to at night; 100 is off.</summary>
    public static int NightBrightness
    {
        get => _settings.NightBrightness;
        set
        {
            _settings.NightBrightness = value;
            _settings.Save();
            SyncBrightness();
        }
    }

    /// <summary>What was wrong with settings.json when it was last loaded.</summary>
    public static IReadOnlyList<string> SettingsProblems => _settings.Problems;

    /// <summary>
    /// Reads settings.json again and applies all of it at once, the weather place and the launched
    /// apps included. A file that can't be read changes nothing, so the hub's next save can't put
    /// defaults over a half-finished edit.
    /// </summary>
    /// <returns>False when the file couldn't be read; <see cref="SettingsProblems"/> says why.</returns>
    public static bool ReloadSettings()
    {
        var s = HubSettings.Load(out var unreadable);
        if (unreadable)
        {
            _settings.Problems.Clear();
            _settings.Problems.Add(Strings.SettingsNotReloaded);
            _settings.Problems.AddRange(s.Problems);
            return false;
        }
        var old = _settings;
        _settings = s;
        var recarved = false;
        foreach (var slot in Slots)
        {
            // A status strip's clock isn't here: the strip reads it each time it's drawn.
            static IEnumerable<(string?, string, int, string)> Strips(HubSettings settings, string id) =>
                (settings.Panels.GetValueOrDefault(id)?.Layout ?? []).Select(e => (e.Region, e.Edge, e.Size, e.Kind));
            if (!Strips(s, slot.Id).SequenceEqual(Strips(old, slot.Id)))
            {
                // A mirrored window isn't saved, so it ends here; videos start again at their new size.
                DropRegions(slot);
                CarveRegions(slot);
                recarved = true;
                continue;
            }
            CarveLayout(slot); // only to report a strip that still doesn't fit

            foreach (var region in slot.Regions)
            {
                if (region.IsStatus)
                {
                    lock (_gate) region.LastSent = null; // its clock may have changed
                    continue;
                }
                var video = s.Panels.GetValueOrDefault(region.Id)?.VideoPath;
                if (video == old.Panels.GetValueOrDefault(region.Id)?.VideoPath) continue;
                if (video is not null) PlayVideo(region, video);
                else if (region.Feed is VideoFeed) SetFeed(region, null, save: false);
            }
        }
        if (recarved) AnnouncePanels();
        WeatherSource.Start(s.Today.WeatherLocation, PostRefresh);
        Launcher.Apply(s.Launch);
        _todayVersion++;
        SyncStyle();
        Refresh();
        return true;
    }

    /// <summary>Writes the settings out, every key included, so the file shows what can be set.</summary>
    public static void SaveSettings() => _settings.Save();

    /// <summary><see cref="HubStyle.System"/>, dark or light.</summary>
    public static string StyleMode
    {
        get => _settings.Style.Mode;
        set
        {
            _settings.Style.Mode = value;
            _settings.Save();
            SyncStyle();
            Refresh();
        }
    }

    public static LcdHubStyle Style
    {
        get { lock (_gate) return _style; }
    }

    public static bool IsHidden(Region region, string producer) =>
        _settings.Panels.GetValueOrDefault(region.Id)?.Hidden.Contains(producer) == true;

    public static string? Pinned(Region region) => _settings.Panels.GetValueOrDefault(region.Id)?.Pinned;

    public static bool TodayInRotation(Region region) => _settings.Panels.GetValueOrDefault(region.Id)?.Today == true;

    /// <summary>The hub-wide Today options; call <see cref="TodayChanged"/> after changing them.</summary>
    public static TodaySettings TodayOptions => _settings.Today;

    public static void TodayChanged()
    {
        _settings.Save();
        _todayVersion++;
        Refresh();
    }

    public static void ToggleProducer(Region region, string producer)
    {
        var hidden = _settings.For(region.Id).Hidden;
        if (!hidden.Remove(producer)) hidden.Add(producer);
        _settings.Save();
        Refresh();
    }

    /// <summary>Keeps a producer on this region, unhiding it and stopping any feed; again to let go.</summary>
    public static void TogglePin(Region region, string producer)
    {
        var p = _settings.For(region.Id);
        var pin = p.Pinned != producer;
        p.Pinned = pin ? producer : null;
        if (pin) p.Hidden.Remove(producer);
        _settings.Save();
        if (pin && region.Feed is not null) SetFeed(region, null);
        else Refresh();
    }

    public static void ToggleToday(Region region)
    {
        var p = _settings.For(region.Id);
        p.Today = !p.Today;
        _settings.Save();
        Refresh();
    }

    public static void BackToRotation(Region region)
    {
        if (_settings.Panels.GetValueOrDefault(region.Id) is { Pinned: not null } p)
        {
            p.Pinned = null;
            _settings.Save();
        }
        SetFeed(region, null);
    }

    public static void PlayVideo(Region region, string path)
    {
        SetFeed(region, new VideoFeed(path, region.Slot.Panel, region.Width, region.Height, PresentFeedFrame, PostFeedEnded));
        _settings.For(region.Id).VideoPath = path;
        _settings.Save();
    }

    public static void MirrorWindow(Region region, IntPtr hwnd) =>
        SetFeed(region, new WindowFeed(hwnd, region.Slot.Panel, region.Width, region.Height, PresentFeedFrame, PostFeedEnded));

    /// <summary>Hands the regions whose feed has ended back to the rotation; returns the errors worth showing.</summary>
    public static List<string> ReapEndedFeeds()
    {
        var errors = new List<string>();
        foreach (var region in Regions)
        {
            if (region.Feed is not { HasEnded: true } ended) continue;
            if (ended.Error is { } error) errors.Add(error);
            SetFeed(region, null, save: ended.Error is not null);
        }
        return errors;
    }

    /// <param name="save">Forget a saved video; false where the region is only going away.</param>
    private static void SetFeed(Region region, LcdFeed? feed, bool save = true)
    {
        var old = region.Feed;
        lock (_gate)
        {
            region.Feed = feed;
            region.LastSent = null;
        }
        old?.Dispose();
        if (save && feed is null && _settings.Panels.GetValueOrDefault(region.Id) is { VideoPath: not null } p)
        {
            p.VideoPath = null;
            _settings.Save();
        }
        if (feed is not null && _settings.Panels.GetValueOrDefault(region.Id) is { Pinned: not null } pinned)
        {
            // A feed is chosen instead of the pinned producer, not underneath it.
            pinned.Pinned = null;
            _settings.Save();
        }
        if (feed is not null)
        {
            feed.Paused = true; // until Refresh decides it's on
            feed.Start();
        }
        Refresh();
    }

    private static void PostFeedEnded() =>
        NativeMethods.PostMessageW(_hwnd, Win32Consts.WM_APP_FEED_ENDED, IntPtr.Zero, IntPtr.Zero);
}
