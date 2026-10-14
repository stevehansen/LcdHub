using LcdHub.Client;

namespace LcdHub;

/// <summary>A connected app. Its frames are written by its pipe thread, under the hub's gate.</summary>
internal sealed class Producer(string name, Action<byte[]> send)
{
    public string Name { get; } = name;
    // Panel id → the newest frame for it.
    public readonly Dictionary<string, Frame> Frames = new();
    // Panel id → the visibility last sent; a panel missing here is Next, as the client assumes.
    public readonly Dictionary<string, LcdHubVisibility> Told = new();

    /// <summary>Best effort: a producer that has gone is reaped by its own pipe thread.</summary>
    public void Send(byte[] message)
    {
        try { send(message); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}

/// <summary>One picture from a producer. Compared by reference: a new frame is a new object.</summary>
internal sealed class Frame(uint[] pixels, bool alert)
{
    public uint[] Pixels { get; } = pixels;
    public bool Alert { get; } = alert;
}

/// <summary>What Today looks like: redrawn when the minute, the weather or the options change.</summary>
internal readonly record struct TodayKey(string Minute, Weather? Weather, int Version);

/// <summary>A plugged-in panel and what is on it. Written on the UI thread under the hub's gate.</summary>
internal sealed class PanelSlot(LcdPanel panel)
{
    public LcdPanel Panel { get; } = panel;
    public string Id => Panel.Device.Id;
    public LcdFeed? Feed;
    // A Rotation pick: a producer's name or one of Rotation's constants.
    public string? Pick;
    public long PickedAt;
    // What was last put on the panel — a Frame, a TodayKey, Rotation.Blank — so it's sent once.
    public object? LastSent;
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
    private static LcdHubStyle _style = LcdHubStyle.Default;

    public static void Start(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _settings = HubSettings.Load();
        _style = HubStyle.Current(_settings.Style);
        WeatherSource.Start(_settings.Today.WeatherLocation, PostRefresh);
        Tick();
    }

    public static void Stop()
    {
        foreach (var slot in Slots)
        {
            SetFeed(slot, null, save: false);
            slot.Panel.Dispose();
        }
        lock (_gate) { _slots.Clear(); }
    }

    /// <summary>UI thread, every second: notices panels plugged in or out, then refreshes.</summary>
    public static void Tick()
    {
        SyncPanels();
        SyncStyle();
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
        lock (_gate) { _producers.Remove(producer); }
        Diagnose.Log($"hub: {producer.Name} disconnected");
        PostRefresh();
    }

    public static void Offer(Producer producer, string panelId, bool alert, uint[] pixels)
    {
        lock (_gate)
        {
            var slot = _slots.Find(s => s.Id == panelId);
            if (slot is null) return;
            // An empty frame withdraws: the producer has nothing to show there for now.
            if (pixels.Length == 0) producer.Frames.Remove(panelId);
            // A frame drawn for the wrong size (the panel has just been swapped) is dropped.
            else if (pixels.Length == slot.Panel.Width * slot.Panel.Height) producer.Frames[panelId] = new Frame(pixels, alert);
            else return;
        }
        PostRefresh();
    }

    private static void PostRefresh()
    {
        if (Interlocked.Exchange(ref _refreshPosted, 1) == 0)
            NativeMethods.PostMessageW(_hwnd, Win32Consts.WM_APP_REFRESH, IntPtr.Zero, IntPtr.Zero);
    }

    private static List<LcdHubPanel> PanelListLocked() =>
        _slots.ConvertAll(s => new LcdHubPanel(s.Id, s.Panel.Width, s.Panel.Height));

    // ---------------- what each panel shows (UI thread) ----------------

    public static void Refresh()
    {
        Interlocked.Exchange(ref _refreshPosted, 0);
        var now = Environment.TickCount64;
        var interval = TimeSpan.FromSeconds(_settings.RotateSeconds);
        var clock = DateTime.Now;
        var today = new TodayKey(clock.ToString("yyyyMMddHHmm"), WeatherSource.Current, _todayVersion);
        lock (_gate)
        {
            foreach (var slot in _slots)
            {
                var settings = _settings.Panels.GetValueOrDefault(slot.Id);
                var offers = new List<Rotation.Offer>();
                foreach (var p in _producers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                    if (p.Frames.TryGetValue(slot.Id, out var f) && settings?.Hidden.Contains(p.Name) != true)
                        offers.Add(new Rotation.Offer(p.Name, f.Alert));

                var switched = false;
                var pick = Rotation.Pick(offers, settings?.Pinned, settings?.Today ?? false, slot.Feed is not null, _locked, _displayOff,
                    slot.Pick, TimeSpan.FromMilliseconds(now - slot.PickedAt), interval);
                if (pick != slot.Pick)
                {
                    Diagnose.Log($"hub: {slot.Id} shows {pick.TrimStart('\0')}");
                    (slot.Pick, slot.PickedAt, switched) = (pick, now, true);
                }
                if (slot.Feed is { } feed) feed.Paused = pick != Rotation.Feed;
                // Whatever would follow if this turn ended now.
                var next = Rotation.Pick(offers, settings?.Pinned, settings?.Today ?? false, slot.Feed is not null, _locked, _displayOff,
                    pick, interval, interval);
                TellVisibility(slot.Id, settings, pick, next);

                object? content = pick switch
                {
                    Rotation.Feed => null,
                    Rotation.Blank => Rotation.Blank,
                    Rotation.Today => today,
                    _ => _producers.Find(p => p.Name == pick)!.Frames[slot.Id],
                };
                if (content is null) { slot.LastSent = null; continue; } // the feed draws for itself
                if (Equals(content, slot.LastSent)) continue;
                slot.LastSent = content;
                var (w, h) = (slot.Panel.Width, slot.Panel.Height);
                var pixels = content switch
                {
                    Frame frame => frame.Pixels,
                    Rotation.Blank => new uint[w * h],
                    _ => Today.Paint(w, h, clock, today.Weather, _settings.Today, _style),
                };
                if (pixels is not null) slot.Panel.Present(pixels, fade: switched);
            }
        }
    }

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
                : p.Name == next || !p.Frames.ContainsKey(panelId) ? LcdHubVisibility.Next
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
                if (slot.Feed == feed && slot.Pick == Rotation.Feed) slot.Panel.Present(frame, motion: true);
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
            SetFeed(slot, null, save: false);
            lock (_gate)
            {
                _slots.Remove(slot);
                foreach (var p in _producers) p.Frames.Remove(slot.Id);
            }
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
            if (_settings.Panels.GetValueOrDefault(device.Id)?.VideoPath is { } video) PlayVideo(slot, video);
        }
        if (!changed) return;
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

    // ---------------- the menu's view and commands (UI thread) ----------------

    public static List<PanelSlot> Slots
    {
        get { lock (_gate) return _slots.ToList(); }
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

    public static bool IsHidden(PanelSlot slot, string producer) =>
        _settings.Panels.GetValueOrDefault(slot.Id)?.Hidden.Contains(producer) == true;

    public static string? Pinned(PanelSlot slot) => _settings.Panels.GetValueOrDefault(slot.Id)?.Pinned;

    public static bool TodayInRotation(PanelSlot slot) => _settings.Panels.GetValueOrDefault(slot.Id)?.Today == true;

    /// <summary>The hub-wide Today options; call <see cref="TodayChanged"/> after changing them.</summary>
    public static TodaySettings TodayOptions => _settings.Today;

    public static void TodayChanged()
    {
        _settings.Save();
        _todayVersion++;
        Refresh();
    }

    public static void ToggleProducer(PanelSlot slot, string producer)
    {
        var hidden = _settings.For(slot.Id).Hidden;
        if (!hidden.Remove(producer)) hidden.Add(producer);
        _settings.Save();
        Refresh();
    }

    /// <summary>Keeps a producer on this panel, unhiding it and stopping any feed; again to let go.</summary>
    public static void TogglePin(PanelSlot slot, string producer)
    {
        var p = _settings.For(slot.Id);
        var pin = p.Pinned != producer;
        p.Pinned = pin ? producer : null;
        if (pin) p.Hidden.Remove(producer);
        _settings.Save();
        if (pin && slot.Feed is not null) SetFeed(slot, null);
        else Refresh();
    }

    public static void ToggleToday(PanelSlot slot)
    {
        var p = _settings.For(slot.Id);
        p.Today = !p.Today;
        _settings.Save();
        Refresh();
    }

    public static void BackToRotation(PanelSlot slot)
    {
        if (_settings.Panels.GetValueOrDefault(slot.Id) is { Pinned: not null } p)
        {
            p.Pinned = null;
            _settings.Save();
        }
        SetFeed(slot, null);
    }

    public static void PlayVideo(PanelSlot slot, string path)
    {
        SetFeed(slot, new VideoFeed(path, slot.Panel, PresentFeedFrame, PostFeedEnded));
        _settings.For(slot.Id).VideoPath = path;
        _settings.Save();
    }

    public static void MirrorWindow(PanelSlot slot, IntPtr hwnd) =>
        SetFeed(slot, new WindowFeed(hwnd, slot.Panel, PresentFeedFrame, PostFeedEnded));

    /// <summary>Hands the panels whose feed has ended back to the rotation; returns the errors worth showing.</summary>
    public static List<string> ReapEndedFeeds()
    {
        var errors = new List<string>();
        foreach (var slot in Slots)
        {
            if (slot.Feed is not { HasEnded: true } ended) continue;
            if (ended.Error is { } error) errors.Add(error);
            SetFeed(slot, null, save: ended.Error is not null);
        }
        return errors;
    }

    /// <param name="save">Forget a saved video; false where the panel is only going away.</param>
    private static void SetFeed(PanelSlot slot, LcdFeed? feed, bool save = true)
    {
        var old = slot.Feed;
        lock (_gate)
        {
            slot.Feed = feed;
            slot.LastSent = null;
        }
        old?.Dispose();
        if (save && feed is null && _settings.Panels.GetValueOrDefault(slot.Id) is { VideoPath: not null } p)
        {
            p.VideoPath = null;
            _settings.Save();
        }
        if (feed is not null && _settings.Panels.GetValueOrDefault(slot.Id) is { Pinned: not null } pinned)
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
