// LCD Hub producer client. This one file is the whole contract with the hub: copy it into
// an app that wants to put pictures on the USB LCDs (or <Compile Include> it from a
// checkout of LcdHub), and the hub compiles the same file for its side of the wire.
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace LcdHub.Client;

/// <summary>A panel the hub drives. Frames for it must be exactly Width×Height.</summary>
public sealed record LcdHubPanel(string Id, int Width, int Height);

/// <summary>
/// The look the hub asks every producer to share, so pictures don't jar when the panel rotates
/// from one to the next. Colours are 0x00RRGGBB, as in a frame's pixels.
/// </summary>
public sealed record LcdHubStyle(bool Light, uint Background, uint Text, uint Accent)
{
    /// <summary>Until the hub says otherwise (or for good, from a hub too old to say).</summary>
    public static readonly LcdHubStyle Default = new(false, 0x141414, 0xE6E6E6, 0x3B82F6);
}

/// <summary>Whether a producer's frame for a panel is worth drawing now.</summary>
public enum LcdHubVisibility : byte
{
    /// <summary>Not on screen nor up next: the hub keeps the last frame; skip drawing unless alerting.</summary>
    Hidden,
    /// <summary>Up next, or the hub has no frame from this producer yet: keep it fresh.</summary>
    Next,
    /// <summary>On the panel now.</summary>
    Shown,
}

/// <summary>
/// Connects a producer to the LCD Hub and keeps it connected: retries while the hub isn't
/// running, reconnects when it restarts. The producer draws a finished frame per panel and
/// hands it to <see cref="Send"/>; the hub decides when it is on screen — rotating it with
/// other producers' frames, or straight away while <c>alert</c> is set.
/// <para>
/// Whenever the panel list changes, connecting and reconnecting included,
/// <see cref="PanelsChanged"/> fires and the hub has forgotten every frame this producer
/// sent before, so the handler should redraw for every panel. It runs on the client's own
/// thread. Sending never blocks on the hub: only the newest frame per panel is kept, and
/// frames sent while disconnected are dropped.
/// </para>
/// <para>
/// Optional, to fit in and save work: draw in <see cref="Style"/>'s colours, and skip drawing
/// a panel while <see cref="Visibility"/> says Hidden, except to raise an alert. Both
/// change events also run on the client's thread.
/// </para>
/// </summary>
public sealed class LcdHubClient : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly string _name;
    private readonly object _gate = new();
    private readonly Dictionary<string, (uint[] Pixels, bool Alert)> _pending = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private IReadOnlyList<LcdHubPanel> _panels = [];
    private readonly Dictionary<string, LcdHubVisibility> _visibility = new();
    private LcdHubStyle _style = LcdHubStyle.Default;

    /// <param name="name">Shown in the hub's menu and remembered there, so keep it stable.</param>
    public LcdHubClient(string name)
    {
        _name = name;
        _thread = new Thread(Run) { IsBackground = true, Name = "LcdHubClient" };
        _thread.Start();
    }

    /// <summary>Fires on the client's thread with the panels now attached; empty while disconnected.</summary>
    public event Action<IReadOnlyList<LcdHubPanel>>? PanelsChanged;

    public IReadOnlyList<LcdHubPanel> Panels
    {
        get { lock (_gate) return _panels; }
    }

    /// <summary>Fires on the client's thread when the hub's style changes.</summary>
    public event Action<LcdHubStyle>? StyleChanged;

    public LcdHubStyle Style
    {
        get { lock (_gate) return _style; }
    }

    /// <summary>Fires on the client's thread when a panel's <see cref="Visibility"/> changes.</summary>
    public event Action<string, LcdHubVisibility>? VisibilityChanged;

    /// <summary>Next until the hub says otherwise, so a hub too old to say gets every frame.</summary>
    public LcdHubVisibility Visibility(string panelId)
    {
        lock (_gate) return _visibility.GetValueOrDefault(panelId, LcdHubVisibility.Next);
    }

    /// <summary>
    /// Queues a top-down BGRX frame for a panel; takes ownership of the array. While
    /// <paramref name="alert"/> is set the hub shows this frame in preference to rotating;
    /// send a frame without it to stand down.
    /// </summary>
    public void Send(string panelId, uint[] pixels, bool alert = false)
    {
        lock (_gate)
        {
            if (_panels.Count == 0) return;
            _pending[panelId] = (pixels, alert);
        }
        _wake.Set();
    }

    /// <summary>
    /// Takes this producer's frame off a panel until the next <see cref="Send"/>, so it drops
    /// out of the rotation (and stands down any alert) without disconnecting.
    /// </summary>
    public void Clear(string panelId) => Send(panelId, []);

    public void Dispose()
    {
        _stop.Cancel();
        _wake.Set();
        _thread.Join(2000);
    }

    private void Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", LcdHubWire.PipeName, PipeDirection.InOut, LcdHubWire.PipeOptions);
                pipe.Connect(1000);
                LcdHubWire.Write(pipe, LcdHubWire.Hello(_name));
                var reader = new Thread(() => ReadPanels(pipe)) { IsBackground = true, Name = "LcdHubClient reader" };
                reader.Start();
                // The reader is first to see the hub go: IsConnected only turns false on a failed
                // write, and a producer with nothing new to send wouldn't write for a long time.
                while (reader.IsAlive && !_stop.IsCancellationRequested)
                {
                    _wake.WaitOne(1000);
                    List<KeyValuePair<string, (uint[] Pixels, bool Alert)>> frames;
                    lock (_gate)
                    {
                        frames = _pending.ToList();
                        _pending.Clear();
                    }
                    foreach (var (panelId, f) in frames)
                        LcdHubWire.Write(pipe, LcdHubWire.Frame(panelId, f.Alert, f.Pixels));
                }
            }
            catch (TimeoutException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { } // a hub run by another user
            SetPanels([]);
            _stop.Token.WaitHandle.WaitOne(RetryInterval);
        }
    }

    private void ReadPanels(Stream pipe)
    {
        try
        {
            // Types this client doesn't know are from a newer hub, and ignored.
            while (LcdHubWire.Read(pipe) is { } message)
            {
                if (message.Type == LcdHubWire.MsgPanels) SetPanels(LcdHubWire.ParsePanels(message.Body));
                else if (message.Type == LcdHubWire.MsgStyle) SetStyle(LcdHubWire.ParseStyle(message.Body));
                else if (message.Type == LcdHubWire.MsgVisibility) SetVisibility(LcdHubWire.ParseVisibility(message.Body));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        _wake.Set();
    }

    private void SetPanels(IReadOnlyList<LcdHubPanel> panels)
    {
        lock (_gate)
        {
            if (panels.Count == 0 && _panels.Count == 0) return;
            _panels = panels;
            _pending.Clear();
            _visibility.Clear();
        }
        PanelsChanged?.Invoke(panels);
    }

    private void SetStyle(LcdHubStyle style)
    {
        lock (_gate)
        {
            if (style == _style) return;
            _style = style;
        }
        StyleChanged?.Invoke(style);
    }

    private void SetVisibility((string PanelId, LcdHubVisibility Visibility) v)
    {
        lock (_gate) _visibility[v.PanelId] = v.Visibility;
        VisibilityChanged?.Invoke(v.PanelId, v.Visibility);
    }
}

/// <summary>
/// The hub's wire format, on the per-user pipe <see cref="PipeName"/>. Every message is a
/// little-endian u32 length of what follows, a u8 type, then its body; strings are a u16
/// byte count and UTF-8.
/// <list type="bullet">
/// <item><see cref="MsgHello"/>, producer → hub, first: u16 version, string name.</item>
/// <item><see cref="MsgPanels"/>, hub → producer, after hello and on every change: u16
/// count, then per panel a string id, u16 width, u16 height.</item>
/// <item><see cref="MsgFrame"/>, producer → hub: string panel id, u8 flags (bit 0 alert),
/// then the panel's width×height u32 top-down BGRX pixels, or none to withdraw the frame.</item>
/// <item><see cref="MsgStyle"/>, hub → producer, after hello and on every change (v2): u8
/// flags (bit 0 light), u32 background, u32 text, u32 accent.</item>
/// <item><see cref="MsgVisibility"/>, hub → producer, on every change (v2): string panel id,
/// u8 <see cref="LcdHubVisibility"/>. A Panels message resets every panel to Next.</item>
/// </list>
/// Additions never break a peer: each side ignores message types it doesn't know and bytes
/// after the fields it reads. <see cref="Version"/> says which messages a producer knows; only
/// a change an older peer would misread raises <see cref="MinVersion"/>, below which the hub
/// refuses a producer.
/// </summary>
public static class LcdHubWire
{
    public const string PipeName = "LcdHub";
    // Overlapped on both ends: each side reads on one thread while writing on another, and
    // a synchronous pipe handle makes the write wait for the pending read.
    public const PipeOptions PipeOptions = System.IO.Pipes.PipeOptions.CurrentUserOnly | System.IO.Pipes.PipeOptions.Asynchronous;
    public const ushort Version = 2, MinVersion = 1;
    public const byte MsgHello = 1, MsgPanels = 2, MsgFrame = 3, MsgStyle = 4, MsgVisibility = 5;
    private const byte FlagAlert = 1, FlagLight = 1;
    // A 1920×1080 frame with room to spare; anything bigger is a corrupt stream.
    private const int MaxMessageBytes = 16 << 20;

    public readonly record struct Message(byte Type, byte[] Body);

    public static byte[] Hello(string name)
    {
        var w = new Writer(MsgHello);
        w.U16(Version);
        w.Str(name);
        return w.Done();
    }

    public static byte[] Panels(IReadOnlyList<LcdHubPanel> panels)
    {
        var w = new Writer(MsgPanels);
        w.U16((ushort)panels.Count);
        foreach (var p in panels)
        {
            w.Str(p.Id);
            w.U16((ushort)p.Width);
            w.U16((ushort)p.Height);
        }
        return w.Done();
    }

    public static byte[] Frame(string panelId, bool alert, uint[] pixels)
    {
        var w = new Writer(MsgFrame, pixels.Length * 4 + 64);
        w.Str(panelId);
        w.U8(alert ? FlagAlert : (byte)0);
        w.Pixels(pixels);
        return w.Done();
    }

    public static byte[] Style(LcdHubStyle style)
    {
        var w = new Writer(MsgStyle);
        w.U8(style.Light ? FlagLight : (byte)0);
        w.U32(style.Background);
        w.U32(style.Text);
        w.U32(style.Accent);
        return w.Done();
    }

    public static byte[] Visibility(string panelId, LcdHubVisibility visibility)
    {
        var w = new Writer(MsgVisibility);
        w.Str(panelId);
        w.U8((byte)visibility);
        return w.Done();
    }

    public static LcdHubStyle ParseStyle(byte[] body)
    {
        var r = new Reader(body);
        return new LcdHubStyle((r.U8() & FlagLight) != 0, r.U32(), r.U32(), r.U32());
    }

    public static (string PanelId, LcdHubVisibility Visibility) ParseVisibility(byte[] body)
    {
        var r = new Reader(body);
        return (r.Str(), (LcdHubVisibility)r.U8());
    }

    public static (ushort Version, string Name) ParseHello(byte[] body)
    {
        var r = new Reader(body);
        return (r.U16(), r.Str());
    }

    public static List<LcdHubPanel> ParsePanels(byte[] body)
    {
        var r = new Reader(body);
        var n = r.U16();
        var panels = new List<LcdHubPanel>(n);
        for (int i = 0; i < n; i++) panels.Add(new LcdHubPanel(r.Str(), r.U16(), r.U16()));
        return panels;
    }

    public static (string PanelId, bool Alert, uint[] Pixels) ParseFrame(byte[] body)
    {
        var r = new Reader(body);
        var id = r.Str();
        var alert = (r.U8() & FlagAlert) != 0;
        return (id, alert, r.Pixels());
    }

    public static void Write(Stream stream, byte[] message)
    {
        stream.Write(message);
        stream.Flush();
    }

    /// <summary>The next message, or null at a clean end of stream.</summary>
    public static Message? Read(Stream stream)
    {
        Span<byte> head = stackalloc byte[5];
        if (!ReadExactly(stream, head)) return null;
        var len = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (len < 1 || len > MaxMessageBytes) throw new IOException($"bad message length {len}");
        var body = new byte[len - 1];
        if (!ReadExactly(stream, body)) throw new IOException("truncated message");
        return new Message(head[4], body);
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new IOException("truncated message");
            }
            read += n;
        }
        return true;
    }

    private sealed class Writer
    {
        private readonly MemoryStream _ms;

        public Writer(byte type, int capacity = 256)
        {
            _ms = new MemoryStream(capacity);
            _ms.Write([0, 0, 0, 0, type]);
        }

        public void U8(byte v) => _ms.WriteByte(v);

        public void U16(ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, v);
            _ms.Write(b);
        }

        public void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            _ms.Write(b);
        }

        public void Str(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length > ushort.MaxValue) throw new ArgumentException("string too long", nameof(s));
            U16((ushort)bytes.Length);
            _ms.Write(bytes);
        }

        public void Pixels(uint[] pixels)
        {
            // BGRX in memory is already little-endian u32 on every Windows machine.
            _ms.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()));
        }

        public byte[] Done()
        {
            var bytes = _ms.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(bytes, bytes.Length - 4);
            return bytes;
        }
    }

    private ref struct Reader(byte[] body)
    {
        private int _pos;

        public byte U8() => body[_pos++];

        public ushort U16()
        {
            var v = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(_pos, 2));
            _pos += 2;
            return v;
        }

        public uint U32()
        {
            var v = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(_pos, 4));
            _pos += 4;
            return v;
        }

        public string Str()
        {
            var n = U16();
            var s = Encoding.UTF8.GetString(body, _pos, n);
            _pos += n;
            return s;
        }

        public uint[] Pixels()
        {
            var rest = body.AsSpan(_pos);
            if (rest.Length % 4 != 0) throw new IOException("ragged pixel data");
            var pixels = new uint[rest.Length / 4];
            rest.CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()));
            _pos = body.Length;
            return pixels;
        }
    }
}
