namespace LcdHub;

/// <summary>The tray icon: a small panel outline, drawn at runtime so it can switch between lit and unlit. app.ico (the exe's icon) is the same shape.</summary>
internal static class TrayIcon
{
    private const int Size = 32;

    /// <summary>A new HICON; the caller destroys it once the shell has copied it.</summary>
    public static unsafe IntPtr Create(bool lit)
    {
        var dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        var color = Gdi.CreateTopDownDib(dc, Size, Size, out var bits);
        NativeMethods.DeleteDC(dc);
        var px = new Span<uint>((void*)bits, Size * Size);
        const uint frame = 0xFFD8D8D8;
        var screen = lit ? 0xFF3A8EE6u : 0xFF404040u;
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            // A wide bezel (rows 7–24), its screen inside, on a short stand.
            var inBezel = y is >= 7 and <= 24 && x is >= 1 and <= 30;
            var inScreen = y is >= 9 and <= 22 && x is >= 3 and <= 28;
            var inStand = (y is >= 25 and <= 27 && x is >= 14 and <= 17) || (y == 28 && x is >= 9 and <= 22);
            px[y * Size + x] = inScreen ? screen : inBezel || inStand ? frame : 0;
        }
        var mask = NativeMethods.CreateBitmap(Size, Size, 1, 1, IntPtr.Zero);
        var info = new ICONINFO { FIcon = 1, HbmColor = color, HbmMask = mask };
        var icon = NativeMethods.CreateIconIndirect(ref info);
        NativeMethods.DeleteObject(color);
        NativeMethods.DeleteObject(mask);
        return icon;
    }
}
