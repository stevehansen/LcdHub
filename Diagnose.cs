namespace LcdHub;

/// <summary>
/// Opt-in diagnostic log to %TEMP%\lcd-hub.log. Silent when disabled.
/// </summary>
public static class Diagnose
{
    private static readonly object _gate = new();
    private static StreamWriter? _writer;

    public static bool IsEnabled => _writer is not null;

    public static string? Init()
    {
        var path = Path.Combine(Path.GetTempPath(), "lcd-hub.log");
        try
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream) { AutoFlush = true };
            Log("diagnostic logging enabled");
            return path;
        }
        catch
        {
            _writer = null;
            return null;
        }
    }

    public static void Log(string message)
    {
        var writer = _writer;
        if (writer is null) return;

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            try { writer.WriteLine($"[{ts}] {message}"); }
            catch { /* best effort */ }
        }
    }

    public static void LogError(string context, Exception error) =>
        Log($"{context}: {error.Message}");
}
