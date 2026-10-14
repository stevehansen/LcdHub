using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LcdHub;

/// <summary>
/// Starts the producers listed in settings with the hub and keeps them running: one that exits is
/// started again, sooner after a long run than after a quick crash. They live in a job object
/// that closes with the hub, so they go with it, even when the hub is killed. An app already
/// running from the same exe (started by its own Run key, say) is left alone and only started
/// once it has gone.
/// </summary>
internal static class Launcher
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5), MaxRetry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(1), CheckElsewhere = TimeSpan.FromSeconds(30);
    // Never closed: the handle closing at the hub's exit is what ends the producers.
    private static IntPtr _job;
    // UI thread only: each kept entry (path and args) and how to stop keeping it.
    private static readonly Dictionary<string, CancellationTokenSource> _kept = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Keeps exactly <paramref name="apps"/> running: starts the entries not yet kept, and ends the
    /// apps it started for entries no longer listed. Unchanged entries keep running undisturbed.
    /// </summary>
    public static void Apply(IEnumerable<LaunchSettings> apps)
    {
        var wanted = new Dictionary<string, LaunchSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
            if (!string.IsNullOrWhiteSpace(app.Path)) wanted.TryAdd($"{Environment.ExpandEnvironmentVariables(app.Path)}\0{app.Args}", app);
        foreach (var key in _kept.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            _kept[key].Cancel();
            _kept.Remove(key);
        }
        foreach (var (key, app) in wanted)
        {
            if (_kept.ContainsKey(key)) continue;
            if (_job == IntPtr.Zero) _job = CreateKillOnCloseJob();
            var stop = _kept[key] = new CancellationTokenSource();
            _ = Task.Run(() => Keep(app, stop.Token));
        }
    }

    private static async Task Keep(LaunchSettings app, CancellationToken stop)
    {
        var path = Environment.ExpandEnvironmentVariables(app.Path!);
        var name = System.IO.Path.GetFileName(path);
        var retry = FirstRetry;
        try
        {
            while (true)
            {
                if (RunningElsewhere(path))
                {
                    await Task.Delay(CheckElsewhere, stop);
                    continue;
                }
                var started = Stopwatch.StartNew();
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(path, app.Args ?? "")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? "",
                    })!;
                    if (_job != IntPtr.Zero) NativeMethods.AssignProcessToJobObject(_job, process.Handle);
                    Diagnose.Log($"launch: started {name} ({process.Id})");
                    try { await process.WaitForExitAsync(stop); }
                    catch (OperationCanceledException)
                    {
                        Diagnose.Log($"launch: ending {name}, no longer in settings");
                        process.Kill(entireProcessTree: true);
                        throw;
                    }
                    Diagnose.Log($"launch: {name} exited with {process.ExitCode} after {started.Elapsed:g}");
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Diagnose.LogError($"launch {name}", ex);
                }
                retry = started.Elapsed >= HealthyRun ? FirstRetry : TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
                await Task.Delay(retry, stop);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static bool RunningElsewhere(string path)
    {
        foreach (var p in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(path)))
        {
            using (p)
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { } // another user's, or gone
            }
        }
        return false;
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = NativeMethods.CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { LimitFlags = Win32Consts.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE };
        if (job != IntPtr.Zero)
            NativeMethods.SetInformationJobObject(job, Win32Consts.JobObjectExtendedLimitInformation, ref info,
                (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        return job;
    }
}
