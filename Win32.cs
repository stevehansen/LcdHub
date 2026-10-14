using System.Runtime.InteropServices;

namespace LcdHub;

#pragma warning disable CS0649 // Field is never assigned — set via P/Invoke

// ------ Types ------

[StructLayout(LayoutKind.Sequential)]
public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
{
    public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
    public uint LimitFlags;
    public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public UIntPtr Affinity;
    public uint PriorityClass, SchedulingClass;
    public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct PAINTSTRUCT
{
    public IntPtr Hdc;
    public int FErase;
    public RECT RcPaint;
    public int FRestore;
    public int FIncUpdate;
    public fixed byte RgbReserved[32];
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct MSG
{
    public IntPtr Hwnd;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public POINT Pt;
    public uint LPrivate;
}

[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFOHEADER
{
    public uint BiSize;
    public int BiWidth;
    public int BiHeight;
    public ushort BiPlanes;
    public ushort BiBitCount;
    public uint BiCompression;
    public uint BiSizeImage;
    public int BiXPelsPerMeter;
    public int BiYPelsPerMeter;
    public uint BiClrUsed;
    public uint BiClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFO
{
    public BITMAPINFOHEADER BmiHeader;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public uint[] BmiColors;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct WNDCLASSEXW
{
    public uint CbSize;
    public uint Style;
    public IntPtr LpfnWndProc;
    public int CbClsExtra;
    public int CbWndExtra;
    public IntPtr HInstance;
    public IntPtr HIcon;
    public IntPtr HCursor;
    public IntPtr HbrBackground;
    public IntPtr LpszMenuName;
    public IntPtr LpszClassName;
    public IntPtr HIconSm;
}

[StructLayout(LayoutKind.Sequential)]
public struct ICONINFO
{
    public int FIcon;
    public int XHotspot;
    public int YHotspot;
    public IntPtr HbmMask;
    public IntPtr HbmColor;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct NOTIFYICONDATAW
{
    public uint CbSize;
    public IntPtr HWnd;
    public uint UID;
    public uint UFlags;
    public uint UCallbackMessage;
    public IntPtr HIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string SzTip;
    public uint DwState;
    public uint DwStateMask;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string SzInfo;
    public uint UTimeoutOrVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string SzInfoTitle;
    public uint DwInfoFlags;
    public Guid GuidItem;
    public IntPtr HBalloonIcon;
}

[StructLayout(LayoutKind.Sequential)]
public struct OPENFILENAMEW
{
    public uint LStructSize;
    public IntPtr HwndOwner;
    public IntPtr HInstance;
    public IntPtr LpstrFilter;
    public IntPtr LpstrCustomFilter;
    public uint NMaxCustFilter;
    public uint NFilterIndex;
    public IntPtr LpstrFile;
    public uint NMaxFile;
    public IntPtr LpstrFileTitle;
    public uint NMaxFileTitle;
    public IntPtr LpstrInitialDir;
    public IntPtr LpstrTitle;
    public uint Flags;
    public ushort NFileOffset;
    public ushort NFileExtension;
    public IntPtr LpstrDefExt;
    public IntPtr LCustData;
    public IntPtr LpfnHook;
    public IntPtr LpTemplateName;
    public IntPtr PvReserved;
    public uint DwReserved;
    public uint FlagsEx;
}

public delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);

public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

// ------ Constants ------

public static class Win32Consts
{
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_ERASEBKGND = 0x0014;
    public const int CW_USEDEFAULT = unchecked((int)0x80000000);
    public const uint SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const int GWL_EXSTYLE = -20;

    // Messages
    public const uint WM_APP = 0x8000;
    public const uint WM_APP_REFRESH = WM_APP + 1;
    public const uint WM_APP_TRAY = WM_APP + 2;
    public const uint WM_APP_FEED_ENDED = WM_APP + 3;

    public const uint WM_TIMER = 0x0113;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_WTSSESSION_CHANGE = 0x02B1;
    public const uint WM_POWERBROADCAST = 0x0218;

    // WM_WTSSESSION_CHANGE / WM_POWERBROADCAST
    public const uint NOTIFY_FOR_THIS_SESSION = 0;
    public const int WTS_SESSION_LOCK = 0x7;
    public const int WTS_SESSION_UNLOCK = 0x8;
    public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;
    // Data is a DWORD: 0 = off, 1 = on, 2 = dimmed.
    public static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    // Timers
    public const uint TIMER_TICK = 1;
    public const int JobObjectExtendedLimitInformation = 9;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    // Message box
    public const uint MB_OK = 0x0;
    public const uint MB_ICONERROR = 0x10;
    public const uint MB_ICONWARNING = 0x30;
    public const uint MB_ICONINFORMATION = 0x40;

    // Menus
    public const uint MF_STRING = 0x0;
    public const uint MF_CHECKED = 0x8;
    public const uint MF_GRAYED = 0x1;
    public const uint MF_POPUP = 0x10;
    public const uint MF_SEPARATOR = 0x800;
    public const uint TPM_RIGHTBUTTON = 0x0002;

    // Window enumeration / capture
    public const uint GW_OWNER = 4;
    public const uint PW_CLIENTONLY = 1;
    public const uint PW_RENDERFULLCONTENT = 2;
    public const uint DWMWA_CLOAKED = 14;

    // GetOpenFileName
    public const uint OFN_NOCHANGEDIR = 0x8;
    public const uint OFN_PATHMUSTEXIST = 0x800;
    public const uint OFN_FILEMUSTEXIST = 0x1000;
    public const uint OFN_EXPLORER = 0x80000;

    // GDI
    public const uint SRCCOPY = 0x00CC0020;
    public const int HALFTONE = 4;
    public const uint TRANSPARENT = 1;
    public const uint DIB_RGB_COLORS = 0;

    // Font
    public const int FW_NORMAL = 400;
    public const int FW_MEDIUM = 500;
    public const int FW_BOLD = 700;
    public const uint DEFAULT_CHARSET = 1;
    public const uint OUT_TT_PRECIS = 4;
    public const uint CLIP_DEFAULT_PRECIS = 0;
    public const uint CLEARTYPE_QUALITY = 5;
    public const uint DEFAULT_PITCH = 0;
    public const uint FF_DONTCARE = 0;

    // DrawText flags
    public const uint DT_CENTER = 0x1;
    public const uint DT_VCENTER = 0x4;
    public const uint DT_SINGLELINE = 0x20;
    public const uint DT_NOPREFIX = 0x800;

    // Tray
    public const uint NIF_ICON = 0x02;
    public const uint NIF_MESSAGE = 0x01;
    public const uint NIF_TIP = 0x04;
    public const uint NIM_ADD = 0x0;
    public const uint NIM_MODIFY = 0x1;
    public const uint NIM_DELETE = 0x2;

    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    public const int ERROR_ALREADY_EXISTS = 183;
}

// ------ P/Invoke ------

internal static partial class NativeMethods
{
    private const string USER32 = "user32.dll";
    private const string KERNEL32 = "kernel32.dll";
    private const string GDI32 = "gdi32.dll";

    // ---- kernel32 ----
    [LibraryImport(KERNEL32, EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandleW(string? lpModuleName);

    [LibraryImport(KERNEL32, EntryPoint = "CreateJobObjectW")]
    public static partial IntPtr CreateJobObjectW(IntPtr attributes, IntPtr name);

    [LibraryImport(KERNEL32, EntryPoint = "SetInformationJobObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [LibraryImport(KERNEL32, EntryPoint = "AssignProcessToJobObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [LibraryImport(KERNEL32, EntryPoint = "CreateMutexW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr CreateMutexW(IntPtr attributes, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);

    // ---- user32 ----
    [LibraryImport(USER32, EntryPoint = "RegisterClassExW")]
    public static partial ushort RegisterClassExW(ref WNDCLASSEXW wcex);

    [LibraryImport(USER32, EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport(USER32, EntryPoint = "DefWindowProcW")]
    public static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport(USER32, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport(USER32, EntryPoint = "GetMessageW")]
    public static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport(USER32, EntryPoint = "DispatchMessageW")]
    public static partial IntPtr DispatchMessageW(ref MSG lpMsg);

    [LibraryImport(USER32)]
    public static partial void PostQuitMessage(int nExitCode);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport(USER32, EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessageW(string lpString);

    [LibraryImport(USER32, EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [LibraryImport(USER32)]
    public static partial IntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [LibraryImport(USER32)]
    public static partial IntPtr CreatePopupMenu();

    [LibraryImport(USER32, EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(IntPtr hMenu);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT pt);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr hWnd, out RECT rect);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);

    [DllImport(USER32, ExactSpelling = true)]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO iconInfo);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(IntPtr value);

    [LibraryImport(USER32)]
    public static partial IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);

    [LibraryImport(USER32)]
    public static partial IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport(USER32, EntryPoint = "DrawTextW", CharSet = CharSet.Unicode)]
    public static extern int DrawTextW(IntPtr hdc, string lpString, int nCount, ref RECT rect, uint uFormat);

    [DllImport(USER32, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport(USER32)]
    public static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [LibraryImport(USER32, EntryPoint = "GetWindowLongW")]
    public static partial int GetWindowLongW(IntPtr hWnd, int nIndex);

    [LibraryImport(USER32, EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

    [LibraryImport(USER32, EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassNameW(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [LibraryImport(USER32, EntryPoint = "GetWindowThreadProcessId")]
    public static partial uint GetWindowProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [LibraryImport(USER32)]
    public static partial IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, in Guid powerSettingGuid, uint flags);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterPowerSettingNotification(IntPtr handle);

    [LibraryImport(USER32)]
    public static partial int FillRect(IntPtr hdc, ref RECT rect, IntPtr hbrush);

    [LibraryImport(USER32)]
    public static partial IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(IntPtr hwnd, in PAINTSTRUCT ps);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(IntPtr hwnd, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustWindowRectEx(ref RECT rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle);

    [LibraryImport(USER32)]
    public static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport(USER32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

    [LibraryImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

    // ---- wtsapi32 ----
    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSRegisterSessionNotification(IntPtr hWnd, uint flags);

    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    // ---- gdi32 ----
    [LibraryImport(GDI32)]
    public static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport(GDI32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr hdc);

    [DllImport(GDI32, ExactSpelling = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [LibraryImport(GDI32)]
    public static partial IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

    [LibraryImport(GDI32)]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [LibraryImport(GDI32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr hObject);

    [LibraryImport(GDI32)]
    public static partial IntPtr CreateSolidBrush(uint crColor);

    [LibraryImport(GDI32)]
    public static partial int SetBkMode(IntPtr hdc, uint mode);

    [LibraryImport(GDI32)]
    public static partial uint SetTextColor(IntPtr hdc, uint color);

    [LibraryImport(GDI32, EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateFontW(
        int height, int width, int escapement, int orientation,
        int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outputPrecision, uint clipPrecision, uint quality,
        uint pitchAndFamily, string faceName);

    [LibraryImport(GDI32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GdiFlush();

    [LibraryImport(GDI32)]
    public static partial int SetStretchBltMode(IntPtr hdc, int mode);

    [LibraryImport(GDI32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr lppt);

    [LibraryImport(GDI32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool StretchBlt(IntPtr hdcDst, int xDst, int yDst, int wDst, int hDst,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [DllImport(GDI32, ExactSpelling = true)]
    public static extern int StretchDIBits(IntPtr hdc, int xDst, int yDst, int wDst, int hDst,
        int xSrc, int ySrc, int wSrc, int hSrc, IntPtr bits, ref BITMAPINFO bmi, uint usage, uint rop);

    // ---- shell32 ----
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW data);
}

#pragma warning restore CS0649
