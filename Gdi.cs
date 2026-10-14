using System.Runtime.InteropServices;

namespace LcdHub;

internal static class Gdi
{
    internal static IntPtr CreateTopDownDib(IntPtr dc, int width, int height, out IntPtr bits)
    {
        var bmi = new BITMAPINFO
        {
            BmiHeader = new BITMAPINFOHEADER
            {
                BiSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                BiWidth = width,
                BiHeight = -height,
                BiPlanes = 1,
                BiBitCount = 32,
            },
            BmiColors = new uint[4],
        };
        return NativeMethods.CreateDIBSection(dc, ref bmi, Win32Consts.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
    }

    /// <summary>
    /// Runs <paramref name="paint"/> on a width×height top-down 32-bpp surface and returns its
    /// pixels, or null if GDI couldn't make one.
    /// </summary>
    public static unsafe uint[]? Render(int width, int height, Action<IntPtr> paint)
    {
        var dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        var dib = CreateTopDownDib(dc, width, height, out var bits);
        if (dib == IntPtr.Zero)
        {
            NativeMethods.DeleteDC(dc);
            return null;
        }
        var oldBmp = NativeMethods.SelectObject(dc, dib);
        try
        {
            paint(dc);
            NativeMethods.GdiFlush();
            var pixels = new uint[width * height];
            new ReadOnlySpan<uint>((void*)bits, pixels.Length).CopyTo(pixels);
            return pixels;
        }
        finally
        {
            NativeMethods.SelectObject(dc, oldBmp);
            NativeMethods.DeleteObject(dib);
            NativeMethods.DeleteDC(dc);
        }
    }

    private static EnumWindowsProc? _windowEnumKeepAlive;

    /// <summary>
    /// Top-level windows a user would call open, front to back: visible, titled, unowned, not
    /// tool windows, not cloaked (on another desktop, or a suspended store app), not ours.
    /// </summary>
    public static List<(IntPtr Hwnd, string Title)> EnumerateAppWindows()
    {
        var result = new List<(IntPtr, string)>();
        var ownPid = (uint)Environment.ProcessId;
        var buf = new char[256];
        _windowEnumKeepAlive = (hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.GetWindow(hwnd, Win32Consts.GW_OWNER) != IntPtr.Zero)
                return true;
            if (((uint)NativeMethods.GetWindowLongW(hwnd, Win32Consts.GWL_EXSTYLE) & Win32Consts.WS_EX_TOOLWINDOW) != 0)
                return true;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, Win32Consts.DWMWA_CLOAKED, out var cloaked, sizeof(int)) >= 0 && cloaked != 0)
                return true;
            NativeMethods.GetWindowProcessId(hwnd, out var pid);
            if (pid == ownPid) return true;
            // The desktop itself.
            if (new string(buf, 0, NativeMethods.GetClassNameW(hwnd, buf, buf.Length)) == "Progman") return true;
            var n = NativeMethods.GetWindowTextW(hwnd, buf, buf.Length);
            if (n > 0) result.Add((hwnd, new string(buf, 0, n)));
            return true;
        };
        NativeMethods.EnumWindows(_windowEnumKeepAlive, IntPtr.Zero);
        _windowEnumKeepAlive = null; // release closure
        return result;
    }
}
