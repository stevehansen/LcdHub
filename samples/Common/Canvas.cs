using System.Runtime.InteropServices;

namespace LcdHub.Samples;

/// <summary>
/// Just enough drawing for a panel: GDI text, boxes and lines on a top-down 32-bit DIB, read
/// back as the BGRX frame the hub wants. Colors are 0xRRGGBB. Sizes are pixels, so scale
/// them from <see cref="Width"/>/<see cref="Height"/> to suit any panel.
/// </summary>
public sealed unsafe partial class Canvas : IDisposable
{
    public enum Align { Left, Center, Right }

    public int Width { get; }
    public int Height { get; }

    private readonly IntPtr _dc, _bitmap, _oldBitmap;
    private readonly uint* _bits;

    public Canvas(int width, int height, uint background = 0x101418)
    {
        Width = width;
        Height = height;
        var bi = new BITMAPINFOHEADER { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        _dc = CreateCompatibleDC(IntPtr.Zero);
        _bitmap = CreateDIBSection(_dc, ref bi, 0, out var bits, IntPtr.Zero, 0);
        _bits = (uint*)bits;
        _oldBitmap = SelectObject(_dc, _bitmap);
        SetBkMode(_dc, 1); // TRANSPARENT
        Box(0, 0, width, height, background);
    }

    public void Box(int x, int y, int w, int h, uint color)
    {
        int left = Math.Max(0, x), right = Math.Min(Width, x + w);
        if (right <= left) return;
        for (int row = Math.Max(0, y); row < Math.Min(Height, y + h); row++)
            new Span<uint>(_bits + row * Width + left, right - left).Fill(color);
    }

    /// <summary>Fills a box that fades from <paramref name="top"/> to <paramref name="bottom"/>.</summary>
    public void Gradient(int x, int y, int w, int h, uint top, uint bottom)
    {
        for (int row = 0; row < h; row++)
        {
            uint Mix(int shift) => (uint)(((top >> shift & 0xFF) * (h - row) + (bottom >> shift & 0xFF) * row) / h) << shift;
            Box(x, y + row, w, 1, Mix(16) | Mix(8) | Mix(0));
        }
    }

    /// <summary>Draws one line of text whose cell is <paramref name="size"/> pixels tall, top at <paramref name="y"/>.</summary>
    public void Text(string text, int x, int y, int size, uint color, Align align = Align.Left, bool bold = false)
    {
        var font = CreateFontW(-size, 0, 0, 0, bold ? 700 : 400, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE */, 0, "Segoe UI");
        var old = SelectObject(_dc, font);
        SetTextColor(_dc, Bgr(color));
        GetTextExtentPoint32W(_dc, text, text.Length, out var extent);
        var left = align switch { Align.Center => x - extent.cx / 2, Align.Right => x - extent.cx, _ => x };
        TextOutW(_dc, left, y, text, text.Length);
        SelectObject(_dc, old);
        DeleteObject(font);
    }

    /// <summary>Draws a line through the points (e.g. a price chart).</summary>
    public void Line(ReadOnlySpan<(int X, int Y)> points, uint color, int thickness)
    {
        if (points.Length < 2) return;
        var pen = CreatePen(0, thickness, Bgr(color));
        var old = SelectObject(_dc, pen);
        MoveToEx(_dc, points[0].X, points[0].Y, IntPtr.Zero);
        for (int i = 1; i < points.Length; i++) LineTo(_dc, points[i].X, points[i].Y);
        SelectObject(_dc, old);
        DeleteObject(pen);
    }

    /// <summary>The finished frame. GDI leaves the unused top byte undefined, so it is cleared here.</summary>
    public uint[] Pixels()
    {
        GdiFlush();
        var pixels = new uint[Width * Height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = _bits[i] & 0x00FFFFFF;
        return pixels;
    }

    public void Dispose()
    {
        SelectObject(_dc, _oldBitmap);
        DeleteObject(_bitmap);
        DeleteDC(_dc);
    }

    private static uint Bgr(uint rgb) => (rgb & 0xFF) << 16 | (rgb & 0xFF00) | (rgb >> 16 & 0xFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateCompatibleDC(IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [LibraryImport("gdi32.dll")] private static partial IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(IntPtr obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(IntPtr hdc, int mode);
    [LibraryImport("gdi32.dll")] private static partial uint SetTextColor(IntPtr hdc, uint color);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool TextOutW(IntPtr hdc, int x, int y, string text, int length);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetTextExtentPoint32W(IntPtr hdc, string text, int length, out SIZE size);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreatePen(int style, int width, uint color);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool MoveToEx(IntPtr hdc, int x, int y, IntPtr point);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool LineTo(IntPtr hdc, int x, int y);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GdiFlush();
}
