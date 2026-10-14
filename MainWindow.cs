using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LcdHub;

/// <summary>
/// The hidden window that owns the message loop: the tray icon and its menu, the 1 s tick,
/// and the lock / display-off notifications that <see cref="Hub"/> acts on.
/// </summary>
internal static class MainWindow
{
    private const string ClassName = "LcdHubWindow";
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupRegistryKey = "LcdHub";
    private static readonly int[] RotateChoices = [5, 10, 15, 30, 60];
    // Night brightness, percent; 100 is off.
    private static readonly int[] DimChoices = [100, 70, 50, 30];
    private const int MaxPanels = 4, MaxProducers = 40, MaxWindows = 40;

    // Menu ids: global ones below 1000; per panel 1000 + index*200 + item.
    private const int IdRotateFirst = 10, IdStartup = 20, IdExit = 30, IdTodayFirst = 40, IdStyleFirst = 50;
    private const int IdSettingsOpen = 60, IdSettingsHelp = 61, IdSettingsProblems = 62, IdSettingsReload = 63, IdDimFirst = 70, IdPreview = 80;
    // Per app: base + producer index * stride + 0 (like the others) or 1 + a RotateChoices index.
    private const int IdAppSecondsBase = 400, IdAppSecondsStride = 10;
    private static readonly string[] StyleModes = [HubStyle.System, HubStyle.Dark, HubStyle.Light];
    private const int IdPanelBase = 1000, IdPanelStride = 200;
    private const int ItemRotate = 0, ItemToday = 1, ItemVideo = 2, ItemProducer = 10, ItemPin = 50, ItemWindow = 100;

    private static WndProcDelegate? _wndProcKeepAlive;
    private static IntPtr _hwnd;
    private static IntPtr _powerNotify;
    private static uint _taskbarCreatedMsg;
    private static string? _tooltip;
    private static bool _trayLit;
    private static bool _sessionLocked, _displayOff;

    // What the open menu was built from, so a command's id maps back to what was clicked.
    private static List<PanelSlot> _menuSlots = [];
    private static List<string> _menuProducers = [];
    private static List<(IntPtr Hwnd, string Title)> _menuWindows = [];

    public static void Run()
    {
        var hInstance = NativeMethods.GetModuleHandleW(null);
        _wndProcKeepAlive = WndProc;
        var className = Marshal.StringToHGlobalUni(ClassName);
        var wc = new WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
            HInstance = hInstance,
            LpszClassName = className,
        };
        NativeMethods.RegisterClassExW(ref wc);
        // A hidden popup rather than a message-only window: only top-level windows hear TaskbarCreated.
        _hwnd = NativeMethods.CreateWindowExW(Win32Consts.WS_EX_TOOLWINDOW, ClassName, Strings.AppName, Win32Consts.WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        _taskbarCreatedMsg = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

        NativeMethods.WTSRegisterSessionNotification(_hwnd, Win32Consts.NOTIFY_FOR_THIS_SESSION);
        // Answers straight away with the current state, so a hub started with the monitors off knows.
        _powerNotify = NativeMethods.RegisterPowerSettingNotification(_hwnd, Win32Consts.GUID_CONSOLE_DISPLAY_STATE,
            Win32Consts.DEVICE_NOTIFY_WINDOW_HANDLE);

        UpdateTray(Win32Consts.NIM_ADD);
        Hub.Start(_hwnd);
        PipeServer.Start();
        NativeMethods.SetTimer(_hwnd, (UIntPtr)Win32Consts.TIMER_TICK, 1000, IntPtr.Zero);

        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }
        Marshal.FreeHGlobal(className);
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam)
    {
        if (msg == _taskbarCreatedMsg && msg != 0)
        {
            Diagnose.Log("TaskbarCreated → re-adding the tray icon");
            _tooltip = null;
            UpdateTray(Win32Consts.NIM_ADD);
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case Win32Consts.WM_TIMER:
                Hub.Tick();
                UpdateTray(Win32Consts.NIM_MODIFY);
                return IntPtr.Zero;

            case Win32Consts.WM_APP_REFRESH:
                Hub.Refresh();
                return IntPtr.Zero;

            case Win32Consts.WM_APP_TRAY:
                if ((uint)lparam.ToInt64() is Win32Consts.WM_LBUTTONUP or Win32Consts.WM_RBUTTONUP) ShowMenu(hwnd);
                return IntPtr.Zero;

            case Win32Consts.WM_COMMAND:
                HandleCommand(hwnd, (ushort)(wparam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;

            case Win32Consts.WM_APP_FEED_ENDED:
                foreach (var error in Hub.ReapEndedFeeds())
                    NativeMethods.MessageBoxW(hwnd, $"{Strings.VideoFailed}: {error}", Strings.AppName,
                        Win32Consts.MB_OK | Win32Consts.MB_ICONERROR);
                return IntPtr.Zero;

            case Win32Consts.WM_WTSSESSION_CHANGE:
            {
                var ev = (int)wparam.ToInt64();
                if (ev is Win32Consts.WTS_SESSION_LOCK or Win32Consts.WTS_SESSION_UNLOCK)
                {
                    _sessionLocked = ev == Win32Consts.WTS_SESSION_LOCK;
                    Hub.SetTakeover(locked: _sessionLocked);
                }
                return IntPtr.Zero;
            }

            case Win32Consts.WM_POWERBROADCAST:
                // lparam is POWERBROADCAST_SETTING: 16-byte GUID, DWORD length, then the data.
                if ((int)wparam.ToInt64() == Win32Consts.PBT_POWERSETTINGCHANGE
                    && Marshal.PtrToStructure<Guid>(lparam) == Win32Consts.GUID_CONSOLE_DISPLAY_STATE)
                {
                    _displayOff = Marshal.ReadInt32(lparam, 20) == 0;
                    Hub.SetTakeover(displayOff: _displayOff);
                }
                break;

            case Win32Consts.WM_DESTROY:
                Hub.Stop();
                var nid = TrayData();
                NativeMethods.Shell_NotifyIconW(Win32Consts.NIM_DELETE, ref nid);
                NativeMethods.WTSUnRegisterSessionNotification(hwnd);
                if (_powerNotify != IntPtr.Zero) NativeMethods.UnregisterPowerSettingNotification(_powerNotify);
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProcW(hwnd, msg, wparam, lparam);
    }

    // ---------------- tray ----------------

    private static NOTIFYICONDATAW TrayData() => new()
    {
        CbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        HWnd = _hwnd,
        UID = 1,
        SzTip = "",
        SzInfo = "",
        SzInfoTitle = "",
    };

    /// <summary>Adds the icon, or refreshes its tooltip and colour when they changed.</summary>
    private static void UpdateTray(uint message)
    {
        var panels = Hub.Slots.Count;
        var producers = Hub.ProducerNames.Count;
        var tooltip = panels == 0
            ? $"{Strings.AppName} — {Strings.NoPanels}"
            : $"{Strings.AppName} — {panels} {(panels == 1 ? Strings.PanelSuffix : Strings.PanelsSuffix)}, " +
              $"{producers} {(producers == 1 ? Strings.ProducerSuffix : Strings.ProducersSuffix)}";
        var lit = panels > 0;
        if (message == Win32Consts.NIM_MODIFY && tooltip == _tooltip && lit == _trayLit) return;
        (_tooltip, _trayLit) = (tooltip, lit);

        var nid = TrayData();
        nid.UFlags = Win32Consts.NIF_ICON | Win32Consts.NIF_MESSAGE | Win32Consts.NIF_TIP;
        nid.UCallbackMessage = Win32Consts.WM_APP_TRAY;
        nid.SzTip = tooltip;
        nid.HIcon = TrayIcon.Create(lit);
        NativeMethods.Shell_NotifyIconW(message, ref nid);
        // The shell keeps its own copy.
        NativeMethods.DestroyIcon(nid.HIcon);
    }

    // ---------------- menu ----------------

    private static void ShowMenu(IntPtr hwnd)
    {
        _menuSlots = Hub.Slots.Take(MaxPanels).ToList();
        _menuProducers = Hub.ProducerNames.Take(MaxProducers).ToList();
        _menuWindows = _menuSlots.Count == 0 ? [] : Gdi.EnumerateAppWindows().Take(MaxWindows).ToList();

        var menu = NativeMethods.CreatePopupMenu();
        if (_menuSlots.Count == 0)
            NativeMethods.AppendMenuW(menu, Win32Consts.MF_STRING | Win32Consts.MF_GRAYED, UIntPtr.Zero, Strings.NoPanels);
        for (int i = 0; i < _menuSlots.Count; i++)
        {
            var slot = _menuSlots[i];
            NativeMethods.AppendMenuW(menu, Win32Consts.MF_POPUP, (UIntPtr)(nuint)CreatePanelMenu(slot, i),
                $"{slot.Panel.Width}×{slot.Panel.Height}");
        }
        if (_menuSlots.Count > 0) NativeMethods.AppendMenuW(menu, Win32Consts.MF_STRING, (UIntPtr)IdPreview, Strings.ShowPreview);
        NativeMethods.AppendMenuW(menu, Win32Consts.MF_SEPARATOR, UIntPtr.Zero, null);

        var today = NativeMethods.CreatePopupMenu();
        var o = Hub.TodayOptions;
        NativeMethods.AppendMenuW(today, o.WeatherLocation is null ? Win32Consts.MF_STRING | Win32Consts.MF_GRAYED : Checked(o.Weather),
            (UIntPtr)IdTodayFirst, o.WeatherLocation is null ? Strings.TodayWeatherOff : MenuText($"{Strings.TodayWeatherIn} {o.WeatherLocation}"));
        NativeMethods.AppendMenuW(today, Checked(o.Date), (UIntPtr)(IdTodayFirst + 1), Strings.TodayDate);
        NativeMethods.AppendMenuW(today, Checked(o.Holidays), (UIntPtr)(IdTodayFirst + 2), Strings.TodayHolidays);
        NativeMethods.AppendMenuW(today, Checked(o.Sky), (UIntPtr)(IdTodayFirst + 3), Strings.TodaySky);
        NativeMethods.AppendMenuW(today, Checked(o.Effects), (UIntPtr)(IdTodayFirst + 4), Strings.TodayEffects);
        NativeMethods.AppendMenuW(menu, Win32Consts.MF_POPUP, (UIntPtr)(nuint)today, Strings.Today);

        var style = NativeMethods.CreatePopupMenu();
        string[] styleNames = [Strings.StyleSystem, Strings.StyleDark, Strings.StyleLight];
        for (int i = 0; i < StyleModes.Length; i++)
            NativeMethods.AppendMenuW(style, Checked(Hub.StyleMode == StyleModes[i]), (UIntPtr)(IdStyleFirst + i), styleNames[i]);
        NativeMethods.AppendMenuW(menu, Win32Consts.MF_POPUP, (UIntPtr)(nuint)style, Strings.Style);

        var rotate = NativeMethods.CreatePopupMenu();
        for (int i = 0; i < RotateChoices.Length; i++)
            NativeMethods.AppendMenuW(rotate, Checked(Hub.RotateSeconds == RotateChoices[i]), (UIntPtr)(IdRotateFirst + i),
                $"{RotateChoices[i]} {Strings.SecondsSuffix}");
        if (_menuProducers.Count > 0) NativeMethods.AppendMenuW(rotate, Win32Consts.MF_SEPARATOR, UIntPtr.Zero, null);
        for (int p = 0; p < _menuProducers.Count; p++)
        {
            var own = Hub.AppSeconds(_menuProducers[p]);
            var app = NativeMethods.CreatePopupMenu();
            var first = IdAppSecondsBase + p * IdAppSecondsStride;
            NativeMethods.AppendMenuW(app, Checked(own is null), (UIntPtr)first, Strings.LikeTheOthers);
            for (int i = 0; i < RotateChoices.Length; i++)
                NativeMethods.AppendMenuW(app, Checked(own == RotateChoices[i]), (UIntPtr)(first + 1 + i), $"{RotateChoices[i]} {Strings.SecondsSuffix}");
            NativeMethods.AppendMenuW(rotate, Win32Consts.MF_POPUP | Checked(own is not null), (UIntPtr)(nuint)app,
                MenuText(own is null ? _menuProducers[p] : $"{_menuProducers[p]}: {own} {Strings.SecondsSuffix}"));
        }
        NativeMethods.AppendMenuW(menu, Win32Consts.MF_POPUP, (UIntPtr)(nuint)rotate, Strings.RotateEvery);
        var dim = NativeMethods.CreatePopupMenu();
        for (int i = 0; i < DimChoices.Length; i++)
            NativeMethods.AppendMenuW(dim, Checked(Hub.NightBrightness == DimChoices[i]), (UIntPtr)(IdDimFirst + i),
                DimChoices[i] == 100 ? Strings.DimOff : $"{DimChoices[i]}%");
        NativeMethods.AppendMenuW(menu, Win32Consts.MF_POPUP, (UIntPtr)(nuint)dim, Strings.DimAtNight);
        var settings = NativeMethods.CreatePopupMenu();
        var problems = Hub.SettingsProblems.Count;
        if (problems > 0)
        {
            NativeMethods.AppendMenuW(settings, Win32Consts.MF_STRING, (UIntPtr)IdSettingsProblems, $"⚠ {Strings.SettingsProblems} ({problems})…");
            NativeMethods.AppendMenuW(settings, Win32Consts.MF_SEPARATOR, UIntPtr.Zero, null);
        }
        NativeMethods.AppendMenuW(settings, Win32Consts.MF_STRING, (UIntPtr)IdSettingsOpen, Strings.OpenSettings);
        NativeMethods.AppendMenuW(settings, Win32Consts.MF_STRING, (UIntPtr)IdSettingsReload, Strings.ReloadSettings);
        NativeMethods.AppendMenuW(settings, Win32Consts.MF_STRING, (UIntPtr)IdSettingsHelp, Strings.SettingsHelp);
        NativeMethods.AppendMenuW(menu, Win32Consts.MF_POPUP, (UIntPtr)(nuint)settings,
            problems > 0 ? $"⚠ {Strings.Settings}" : Strings.Settings);
        NativeMethods.AppendMenuW(menu, Checked(IsStartupEnabled()), (UIntPtr)IdStartup, Strings.StartWithWindows);
        NativeMethods.AppendMenuW(menu, Win32Consts.MF_SEPARATOR, UIntPtr.Zero, null);
        NativeMethods.AppendMenuW(menu, Win32Consts.MF_STRING, (UIntPtr)IdExit, Strings.Exit);

        NativeMethods.GetCursorPos(out var pt);
        // Without this the menu won't close when clicking elsewhere.
        NativeMethods.SetForegroundWindow(hwnd);
        NativeMethods.TrackPopupMenu(menu, Win32Consts.TPM_RIGHTBUTTON, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
        // Destroys the submenus too.
        NativeMethods.DestroyMenu(menu);
    }

    private static IntPtr CreatePanelMenu(PanelSlot slot, int index)
    {
        var id = (int item) => (UIntPtr)(IdPanelBase + index * IdPanelStride + item);
        var menu = NativeMethods.CreatePopupMenu();
        var pinned = Hub.Pinned(slot);
        NativeMethods.AppendMenuW(menu, Checked(slot.Feed is null && pinned is null), id(ItemRotate), Strings.Rotate);
        NativeMethods.AppendMenuW(menu, Win32Consts.MF_SEPARATOR, UIntPtr.Zero, null);
        if (_menuProducers.Count == 0)
            NativeMethods.AppendMenuW(menu, Win32Consts.MF_STRING | Win32Consts.MF_GRAYED, UIntPtr.Zero, Strings.NoProducers);
        for (int i = 0; i < _menuProducers.Count; i++)
            NativeMethods.AppendMenuW(menu, Checked(!Hub.IsHidden(slot, _menuProducers[i])), id(ItemProducer + i),
                MenuText(_menuProducers[i]));
        NativeMethods.AppendMenuW(menu, Checked(Hub.TodayInRotation(slot)), id(ItemToday), Strings.Today);
        if (_menuProducers.Count > 0)
        {
            var pins = NativeMethods.CreatePopupMenu();
            for (int i = 0; i < _menuProducers.Count; i++)
                NativeMethods.AppendMenuW(pins, Checked(pinned == _menuProducers[i]), id(ItemPin + i), MenuText(_menuProducers[i]));
            NativeMethods.AppendMenuW(menu, Win32Consts.MF_POPUP | Checked(pinned is not null), (UIntPtr)(nuint)pins, Strings.KeepShowing);
        }
        NativeMethods.AppendMenuW(menu, Win32Consts.MF_SEPARATOR, UIntPtr.Zero, null);
        NativeMethods.AppendMenuW(menu, Checked(slot.Feed is VideoFeed), id(ItemVideo), Strings.VideoFile);

        var windows = NativeMethods.CreatePopupMenu();
        if (_menuWindows.Count == 0)
            NativeMethods.AppendMenuW(windows, Win32Consts.MF_STRING | Win32Consts.MF_GRAYED, UIntPtr.Zero, Strings.NoWindows);
        for (int i = 0; i < _menuWindows.Count; i++)
            NativeMethods.AppendMenuW(windows, Checked(slot.Feed is WindowFeed wf && wf.Hwnd == _menuWindows[i].Hwnd),
                id(ItemWindow + i), MenuText(_menuWindows[i].Title));
        NativeMethods.AppendMenuW(menu, Win32Consts.MF_POPUP | Checked(slot.Feed is WindowFeed), (UIntPtr)(nuint)windows,
            Strings.MirrorWindow);
        return menu;
    }

    private static uint Checked(bool on) => Win32Consts.MF_STRING | (on ? Win32Consts.MF_CHECKED : 0);

    private static string MenuText(string s) => (s.Length > 60 ? s[..57] + "…" : s).Replace("&", "&&");

    private static void HandleCommand(IntPtr hwnd, int id)
    {
        if (id >= IdRotateFirst && id < IdRotateFirst + RotateChoices.Length)
        {
            Hub.RotateSeconds = RotateChoices[id - IdRotateFirst];
            return;
        }
        if (id >= IdAppSecondsBase && id < IdAppSecondsBase + _menuProducers.Count * IdAppSecondsStride)
        {
            var choice = (id - IdAppSecondsBase) % IdAppSecondsStride;
            if (choice <= RotateChoices.Length)
                Hub.SetAppSeconds(_menuProducers[(id - IdAppSecondsBase) / IdAppSecondsStride], choice == 0 ? null : RotateChoices[choice - 1]);
            return;
        }
        if (id >= IdTodayFirst && id < IdTodayFirst + 5)
        {
            var o = Hub.TodayOptions;
            switch (id - IdTodayFirst)
            {
                case 0: o.Weather = !o.Weather; break;
                case 1: o.Date = !o.Date; break;
                case 2: o.Holidays = !o.Holidays; break;
                case 3: o.Sky = !o.Sky; break;
                case 4: o.Effects = !o.Effects; break;
            }
            Hub.TodayChanged();
            return;
        }
        if (id >= IdDimFirst && id < IdDimFirst + DimChoices.Length)
        {
            Hub.NightBrightness = DimChoices[id - IdDimFirst];
            return;
        }
        if (id >= IdStyleFirst && id < IdStyleFirst + StyleModes.Length)
        {
            Hub.StyleMode = StyleModes[id - IdStyleFirst];
            return;
        }
        switch (id)
        {
            case IdSettingsOpen:
                Hub.SaveSettings();
                Open(HubSettings.FilePath);
                return;
            case IdSettingsHelp:
                Open(HubSettings.HelpUrl);
                return;
            case IdPreview:
                Preview.Open();
                return;
            case IdSettingsReload:
                Hub.ReloadSettings();
                if (Hub.SettingsProblems.Count == 0)
                    NativeMethods.MessageBoxW(hwnd, Strings.SettingsReloaded, Strings.Settings, Win32Consts.MB_OK | Win32Consts.MB_ICONINFORMATION);
                else
                    ShowSettingsProblems(hwnd);
                return;
            case IdSettingsProblems:
                ShowSettingsProblems(hwnd);
                return;
        }
        if (id == IdStartup)
        {
            SetStartupEnabled(!IsStartupEnabled());
            return;
        }
        if (id == IdExit)
        {
            NativeMethods.DestroyWindow(hwnd);
            return;
        }
        if (id < IdPanelBase) return;

        var index = (id - IdPanelBase) / IdPanelStride;
        var item = (id - IdPanelBase) % IdPanelStride;
        // The panel may have gone while the menu was open.
        if (index >= _menuSlots.Count || !Hub.Slots.Contains(_menuSlots[index])) return;
        var slot = _menuSlots[index];
        switch (item)
        {
            case ItemRotate: Hub.BackToRotation(slot); break;
            case ItemToday: Hub.ToggleToday(slot); break;
            case ItemVideo:
                // The dialog pumps messages, so check again that the panel is still here.
                if (PickVideoFile(hwnd) is { } path && Hub.Slots.Contains(slot)) Hub.PlayVideo(slot, path);
                break;
            case >= ItemWindow when item - ItemWindow < _menuWindows.Count:
                var target = _menuWindows[item - ItemWindow].Hwnd;
                if (NativeMethods.IsWindow(target)) Hub.MirrorWindow(slot, target);
                break;
            case >= ItemPin when item - ItemPin < _menuProducers.Count:
                Hub.TogglePin(slot, _menuProducers[item - ItemPin]);
                break;
            case >= ItemProducer when item - ItemProducer < _menuProducers.Count:
                Hub.ToggleProducer(slot, _menuProducers[item - ItemProducer]);
                break;
        }
    }

    /// <summary>Opens a file or URL with its default app; a .json without one opens in Notepad.</summary>
    private static void ShowSettingsProblems(IntPtr hwnd) =>
        NativeMethods.MessageBoxW(hwnd, string.Join("\n\n", Hub.SettingsProblems) + "\n\n" + Strings.SettingsProblemsHint,
            Strings.SettingsProblems, Win32Consts.MB_OK | Win32Consts.MB_ICONWARNING);

    private static void Open(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception)
        {
            try { System.Diagnostics.Process.Start("notepad.exe", $"\"{target}\""); }
            catch (System.ComponentModel.Win32Exception ex) { Diagnose.LogError("open " + target, ex); }
        }
    }

    private static string? PickVideoFile(IntPtr owner)
    {
        const int maxPath = 2048;
        var filter = Marshal.StringToHGlobalUni(
            $"{Strings.VideoFilter}\0*.mp4;*.m4v;*.mov;*.mkv;*.webm;*.wmv;*.avi\0{Strings.AllFilesFilter}\0*.*\0\0");
        var title = Marshal.StringToHGlobalUni(Strings.VideoDialogTitle);
        var file = Marshal.AllocHGlobal(maxPath * sizeof(char));
        try
        {
            Marshal.WriteInt16(file, 0);
            var ofn = new OPENFILENAMEW
            {
                LStructSize = (uint)Marshal.SizeOf<OPENFILENAMEW>(),
                HwndOwner = owner,
                LpstrFilter = filter,
                LpstrFile = file,
                NMaxFile = maxPath,
                LpstrTitle = title,
                Flags = Win32Consts.OFN_EXPLORER | Win32Consts.OFN_FILEMUSTEXIST | Win32Consts.OFN_PATHMUSTEXIST | Win32Consts.OFN_NOCHANGEDIR,
            };
            return NativeMethods.GetOpenFileNameW(ref ofn) ? Marshal.PtrToStringUni(file) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(file);
            Marshal.FreeHGlobal(title);
            Marshal.FreeHGlobal(filter);
        }
    }

    // ---------------- startup registry ----------------

    private static string StartupValue => $"\"{Environment.ProcessPath}\"";

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath);
            return string.Equals(key?.GetValue(StartupRegistryKey) as string, StartupValue, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void SetStartupEnabled(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true);
            if (key is null) return;
            if (enable) key.SetValue(StartupRegistryKey, StartupValue, RegistryValueKind.String);
            else key.DeleteValue(StartupRegistryKey, throwOnMissingValue: false);
        }
        catch (Exception e) { Diagnose.LogError("startup registry", e); }
    }
}
