namespace LcdHub;

/// <summary>
/// A moving picture for an LCD panel — a video file, a mirrored window — made on a thread of
/// its own, at the panel's size and no faster than it takes them. Frames go only to the sink
/// given at construction, so the owner decides whether they reach the panel (not while the
/// session is locked, say). Pausing stops the work, not just the output. <c>ended</c> fires
/// once, from the feed thread, when the source is gone for good; <see cref="Error"/> then says
/// why if that wasn't a normal end.
/// </summary>
internal abstract class LcdFeed : IDisposable
{
    private readonly Thread _thread;
    private readonly LcdPanel _panel;
    private readonly Action<LcdFeed, uint[]> _sink;
    private readonly Action _ended;
    private readonly ManualResetEventSlim _unpaused = new(true);
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private volatile bool _hasEnded;

    protected LcdFeed(LcdPanel panel, Action<LcdFeed, uint[]> sink, Action ended)
    {
        _panel = panel;
        _sink = sink;
        _ended = ended;
        _thread = new Thread(Main) { IsBackground = true, Name = GetType().Name };
    }

    public string? Error { get; protected set; }

    /// <summary>The size of the frames this feed emits: its panel's.</summary>
    protected int Width => _panel.Width;
    protected int Height => _panel.Height;

    /// <summary>
    /// The panel hasn't taken the last frame yet, so skip making the next: the 9.16" takes
    /// ~24 fps, and a 60 fps video would otherwise scale more than half its frames for nothing.
    /// </summary>
    protected bool PanelBusy => _panel.HasPendingFrame;

    public bool HasEnded => _hasEnded;

    public void Start()
    {
        if (_unpaused.IsSet) _clock.Start();
        _thread.Start();
    }

    public bool Paused
    {
        set
        {
            if (value) { _unpaused.Reset(); _clock.Stop(); }
            else { _clock.Start(); _unpaused.Set(); }
        }
    }

    /// <summary>Time spent unpaused since Start.</summary>
    protected TimeSpan Clock => _clock.Elapsed;

    /// <summary>Hands a finished LCD frame to the owner; takes ownership of the array.</summary>
    protected void Emit(uint[] frame) => _sink(this, frame);

    /// <summary>Blocks while paused. False once the feed is being disposed.</summary>
    protected bool WaitWhilePaused()
    {
        _unpaused.Wait();
        return !_stop.IsSet;
    }

    /// <summary>Sleeps, waking early on dispose. False once the feed is being disposed.</summary>
    protected bool Sleep(TimeSpan duration) =>
        duration <= TimeSpan.Zero ? !_stop.IsSet : !_stop.Wait(duration);

    /// <summary>Produces frames until the source ends or <see cref="WaitWhilePaused"/>/<see cref="Sleep"/> say stop.</summary>
    protected abstract void Run();

    private void Main()
    {
        try { Run(); }
        catch (Exception ex)
        {
            Diagnose.Log($"lcd feed {GetType().Name} died: {ex}");
            Error ??= ex.Message;
        }
        if (_stop.IsSet) return;
        _hasEnded = true;
        _ended();
    }

    public void Dispose()
    {
        _stop.Set();
        _unpaused.Set();
        _thread.Join(2000);
    }
}

/// <summary>
/// A reusable panel-sized drawing surface that scales a source picture onto the LCD. Fill
/// crops the source to the panel's shape; fit letterboxes it in black. Not thread-safe; one
/// per feed.
/// </summary>
internal sealed unsafe class LcdCanvas : IDisposable
{
    private readonly int W, H;
    private readonly IntPtr _dc, _dib, _oldBmp, _bits;

    public LcdCanvas(int width, int height)
    {
        (W, H) = (width, height);
        _dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        _dib = Gdi.CreateTopDownDib(_dc, W, H, out _bits);
        _oldBmp = NativeMethods.SelectObject(_dc, _dib);
        NativeMethods.SetStretchBltMode(_dc, Win32Consts.HALFTONE);
        NativeMethods.SetBrushOrgEx(_dc, 0, 0, IntPtr.Zero); // required after switching to HALFTONE
    }

    public bool IsValid => _dib != IntPtr.Zero;

    public uint[] DrawFrom(IntPtr srcDc, int width, int height, bool fill)
    {
        var (dst, src) = Layout(width, height, fill);
        NativeMethods.StretchBlt(_dc, dst.Left, dst.Top, dst.Right - dst.Left, dst.Bottom - dst.Top,
            srcDc, src.Left, src.Top, src.Right - src.Left, src.Bottom - src.Top, Win32Consts.SRCCOPY);
        return Snapshot();
    }

    /// <summary>
    /// Draws 32-bpp BGRX pixels. <paramref name="scan0"/> is the top row; a negative
    /// <paramref name="pitch"/> means rows run upwards in memory (a bottom-up image).
    /// </summary>
    public uint[] DrawFrom(IntPtr scan0, int width, int height, int pitch, bool fill)
    {
        var (dst, src) = Layout(width, height, fill);
        var bottomUp = pitch < 0;
        var bmi = new BITMAPINFO
        {
            BmiHeader = new BITMAPINFOHEADER
            {
                BiSize = (uint)sizeof(BITMAPINFOHEADER),
                BiWidth = Math.Abs(pitch) / 4,
                BiHeight = bottomUp ? height : -height,
                BiPlanes = 1,
                BiBitCount = 32,
            },
            BmiColors = new uint[4],
        };
        // A DIB starts at its lowest address, which for a bottom-up image is the last row.
        var bits = bottomUp ? scan0 + pitch * (height - 1) : scan0;
        // Layout crops symmetrically, so the source y means the same from either edge.
        NativeMethods.StretchDIBits(_dc, dst.Left, dst.Top, dst.Right - dst.Left, dst.Bottom - dst.Top,
            src.Left, src.Top, src.Right - src.Left, src.Bottom - src.Top, bits, ref bmi, Win32Consts.DIB_RGB_COLORS, Win32Consts.SRCCOPY);
        return Snapshot();
    }

    private (RECT Dst, RECT Src) Layout(int width, int height, bool fill)
    {
        var full = new RECT { Left = 0, Top = 0, Right = W, Bottom = H };
        var whole = new RECT { Left = 0, Top = 0, Right = width, Bottom = height };
        // Compare width/height with W/H by cross-multiplying, in long to stay exact.
        var wider = (long)width * H > (long)W * height;
        if (fill)
        {
            if (wider)
            {
                var cw = (int)((long)height * W / H);
                return (full, new RECT { Left = (width - cw) / 2, Top = 0, Right = (width - cw) / 2 + cw, Bottom = height });
            }
            var ch = (int)((long)width * H / W);
            return (full, new RECT { Left = 0, Top = (height - ch) / 2, Right = width, Bottom = (height - ch) / 2 + ch });
        }

        new Span<uint>((void*)_bits, W * H).Clear();
        if (wider)
        {
            var dh = (int)((long)height * W / width);
            return (new RECT { Left = 0, Top = (H - dh) / 2, Right = W, Bottom = (H - dh) / 2 + dh }, whole);
        }
        var dw = (int)((long)width * H / height);
        return (new RECT { Left = (W - dw) / 2, Top = 0, Right = (W - dw) / 2 + dw, Bottom = H }, whole);
    }

    private uint[] Snapshot()
    {
        NativeMethods.GdiFlush();
        var pixels = new uint[W * H];
        new ReadOnlySpan<uint>((void*)_bits, pixels.Length).CopyTo(pixels);
        return pixels;
    }

    public void Dispose()
    {
        NativeMethods.SelectObject(_dc, _oldBmp);
        if (_dib != IntPtr.Zero) NativeMethods.DeleteObject(_dib);
        NativeMethods.DeleteDC(_dc);
    }
}

/// <summary>
/// Mirrors a window's client area, letterboxed. Captures through DWM, so it works for
/// GPU-drawn and covered windows; a minimized one just holds its last frame. Ends when the
/// window is destroyed.
/// </summary>
internal sealed class WindowFeed : LcdFeed
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 30);

    public WindowFeed(IntPtr hwnd, LcdPanel panel, Action<LcdFeed, uint[]> sink, Action ended)
        : base(panel, sink, ended) => Hwnd = hwnd;

    public IntPtr Hwnd { get; }

    protected override void Run()
    {
        // PrintWindow draws at the window's own DPI — a DPI-unaware window at 96 even where
        // DWM stretches it to 150% — so measure it in that same space.
        NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.GetWindowDpiAwarenessContext(Hwnd));
        using var canvas = new LcdCanvas(Width, Height);
        var srcDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        IntPtr srcDib = IntPtr.Zero, oldBmp = IntPtr.Zero;
        int dibW = 0, dibH = 0;
        try
        {
            while (WaitWhilePaused())
            {
                if (!NativeMethods.IsWindow(Hwnd)) return;
                var started = Clock;
                if (!PanelBusy && !NativeMethods.IsIconic(Hwnd) && NativeMethods.GetClientRect(Hwnd, out var rc)
                    && rc.Right > 0 && rc.Bottom > 0)
                {
                    if (rc.Right != dibW || rc.Bottom != dibH)
                    {
                        if (srcDib != IntPtr.Zero)
                        {
                            NativeMethods.SelectObject(srcDc, oldBmp);
                            NativeMethods.DeleteObject(srcDib);
                        }
                        (dibW, dibH) = (rc.Right, rc.Bottom);
                        srcDib = Gdi.CreateTopDownDib(srcDc, dibW, dibH, out _);
                        oldBmp = NativeMethods.SelectObject(srcDc, srcDib);
                    }
                    if (srcDib != IntPtr.Zero && canvas.IsValid
                        && NativeMethods.PrintWindow(Hwnd, srcDc, Win32Consts.PW_CLIENTONLY | Win32Consts.PW_RENDERFULLCONTENT))
                        Emit(canvas.DrawFrom(srcDc, dibW, dibH, fill: false));
                }
                if (!Sleep(FrameInterval - (Clock - started))) return;
            }
        }
        finally
        {
            if (srcDib != IntPtr.Zero)
            {
                NativeMethods.SelectObject(srcDc, oldBmp);
                NativeMethods.DeleteObject(srcDib);
            }
            NativeMethods.DeleteDC(srcDc);
        }
    }
}
