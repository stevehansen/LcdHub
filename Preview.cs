using System.Runtime.InteropServices;

namespace LcdHub;

/// <summary>
/// A window showing what every panel shows right now, stacked and scaled to the window's
/// width, so the panels can be checked without looking inside the case. UI thread only; one
/// window at a time.
/// </summary>
internal static class Preview
{
    private const string ClassName = "LcdHubPreview";
    private const int Width = 640, Gap = 8, RefreshMs = 250;
    private const uint TimerRefresh = 1;

    private static WndProcDelegate? _wndProcKeepAlive;
    private static IntPtr _hwnd, _background;

    public static void Open()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(_hwnd);
            return;
        }
        var hInstance = NativeMethods.GetModuleHandleW(null);
        if (_wndProcKeepAlive is null)
        {
            _wndProcKeepAlive = WndProc;
            _background = NativeMethods.CreateSolidBrush(0x101010);
            var wc = new WNDCLASSEXW
            {
                CbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
                HInstance = hInstance,
                HbrBackground = _background,
                // Lives as long as the process, like the class.
                LpszClassName = Marshal.StringToHGlobalUni(ClassName),
            };
            NativeMethods.RegisterClassExW(ref wc);
        }
        var style = Win32Consts.WS_OVERLAPPEDWINDOW | Win32Consts.WS_VISIBLE;
        _hwnd = NativeMethods.CreateWindowExW(0, ClassName, Strings.PreviewTitle, style,
            Win32Consts.CW_USEDEFAULT, Win32Consts.CW_USEDEFAULT, Width, Width, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;
        FitToPanels(style);
        NativeMethods.SetTimer(_hwnd, (UIntPtr)TimerRefresh, RefreshMs, IntPtr.Zero);
    }

    /// <summary>Sizes the window so every panel fits at <see cref="Width"/> (scaled for DPI).</summary>
    private static void FitToPanels(uint style)
    {
        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var (width, height) = ((int)(Width * scale), 0);
        foreach (var slot in Hub.Slots) height += width * slot.Panel.Height / slot.Panel.Width + (int)(Gap * scale);
        var rect = new RECT { Right = width, Bottom = Math.Max(height, width / 4) };
        NativeMethods.AdjustWindowRectEx(ref rect, style, false, 0);
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, rect.Right - rect.Left, rect.Bottom - rect.Top,
            Win32Consts.SWP_NOMOVE | Win32Consts.SWP_NOZORDER);
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam)
    {
        switch (msg)
        {
            case Win32Consts.WM_TIMER:
                NativeMethods.InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            case Win32Consts.WM_ERASEBKGND:
                return 1; // Paint covers it, without the flicker of erasing first
            case Win32Consts.WM_PAINT:
                Paint(hwnd);
                return IntPtr.Zero;
            case Win32Consts.WM_DESTROY:
                _hwnd = IntPtr.Zero;
                return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProcW(hwnd, msg, wparam, lparam);
    }

    private static unsafe void Paint(IntPtr hwnd)
    {
        var dc = NativeMethods.BeginPaint(hwnd, out var ps);
        NativeMethods.GetClientRect(hwnd, out var client);
        NativeMethods.FillRect(dc, ref client, _background);
        NativeMethods.SetStretchBltMode(dc, Win32Consts.HALFTONE);
        var gap = (int)(Gap * NativeMethods.GetDpiForWindow(hwnd) / 96.0);
        var y = 0;
        foreach (var slot in Hub.Slots)
        {
            var (w, h) = (slot.Panel.Width, slot.Panel.Height);
            var height = client.Right * h / w;
            if (slot.Panel.Shown is { } pixels)
            {
                var bmi = new BITMAPINFO
                {
                    BmiHeader = new BITMAPINFOHEADER
                    {
                        BiSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), BiWidth = w, BiHeight = -h, BiPlanes = 1, BiBitCount = 32,
                    },
                    BmiColors = new uint[4],
                };
                fixed (uint* bits = pixels)
                    NativeMethods.StretchDIBits(dc, 0, y, client.Right, height, 0, 0, w, h, (IntPtr)bits, ref bmi,
                        Win32Consts.DIB_RGB_COLORS, Win32Consts.SRCCOPY);
            }
            y += height + gap;
        }
        NativeMethods.EndPaint(hwnd, in ps);
    }
}
