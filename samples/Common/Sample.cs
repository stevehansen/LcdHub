using LcdHub.Client;

namespace LcdHub.Samples;

/// <summary>
/// The loop every sample shares: fetch, draw for each panel, repeat. The client stays
/// connected across fetches, and a panel change (hub restart included) redraws from the last
/// data straight away. A failed fetch is logged and retried at the next interval, and the last
/// good picture stays up meanwhile.
/// </summary>
public static class Sample
{
    public static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "User-Agent", "LcdHub-sample/1.0" } },
    };

    /// <param name="name">The producer name the hub shows in its menu.</param>
    /// <param name="draw">Draws the data on a panel-sized canvas and says whether it is an
    /// alert, or returns null to show nothing on that panel.</param>
    /// <param name="redraw">Also redraws, without fetching, on each wall-clock multiple of this
    /// (a minute, for a clock).</param>
    public static async Task Run<T>(string name, TimeSpan every, Func<Task<T>> fetch, Func<Canvas, T, bool?> draw,
        TimeSpan? redraw = null)
    {
        using var client = new LcdHubClient(name);
        var gate = new object();
        T? data = default;
        var hasData = false;

        void Push()
        {
            lock (gate)
            {
                if (!hasData) return;
                foreach (var panel in client.Panels)
                {
                    using var canvas = new Canvas(panel.Width, panel.Height);
                    var alert = draw(canvas, data!);
                    if (alert is null) client.Clear(panel.Id);
                    else client.Send(panel.Id, canvas.Pixels(), alert.Value);
                }
            }
        }

        client.PanelsChanged += _ => Push();
        if (redraw is { } r)
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var intoPeriod = DateTime.Now.TimeOfDay.Ticks % r.Ticks;
                    await Task.Delay(TimeSpan.FromTicks(r.Ticks - intoPeriod) + TimeSpan.FromMilliseconds(20));
                    Push();
                }
            });
        Console.WriteLine($"{name}: sending to LcdHub every {every}. Ctrl+C stops.");
        using var timer = new PeriodicTimer(every);
        do
        {
            try
            {
                var fresh = await fetch();
                lock (gate) { data = fresh; hasData = true; }
                Push();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                Console.Error.WriteLine($"{name}: {ex.Message}");
            }
        } while (await timer.WaitForNextTickAsync());
    }

    /// <summary>A font size that fits a panel of any shape: a fraction of its shorter side.</summary>
    public static int Size(this Canvas c, double fraction) => (int)(Math.Min(c.Width, c.Height) * fraction);
}
