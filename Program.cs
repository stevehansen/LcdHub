namespace LcdHub;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (Array.Exists(args, a => a == "--diagnose") && Diagnose.Init() is { } path)
            Diagnose.Log($"startup args=[{string.Join(',', args)}] log_path={path}");

        NativeMethods.SetProcessDpiAwarenessContext(Win32Consts.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        // Only one process can drive the panels.
        var mutex = NativeMethods.CreateMutexW(IntPtr.Zero, true, @"Global\LcdHub");
        if (mutex == IntPtr.Zero || System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == Win32Consts.ERROR_ALREADY_EXISTS)
        {
            Diagnose.Log("already running");
            return 1;
        }

        MainWindow.Run();
        GC.KeepAlive(mutex);
        return 0;
    }
}
