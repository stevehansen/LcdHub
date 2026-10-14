// Phare (phare.io) uptime monitors, as an alert only: nothing is on the panel while every
// monitor is up, and the ones that are down take it over until they recover.
//   set PHARE_API_KEY=...            (Phare: Settings → API keys; read access is enough)
//   set PHARE_PROJECT_ID=123         (only for an organization-wide key)
//   dotnet run --project samples/Uptime
using System.Text.Json.Nodes;
using LcdHub.Samples;

var key = Environment.GetEnvironmentVariable("PHARE_API_KEY");
if (string.IsNullOrEmpty(key))
{
    Console.Error.WriteLine("Set PHARE_API_KEY to a Phare API key first.");
    return 1;
}
Sample.Http.DefaultRequestHeaders.Authorization = new("Bearer", key);
if (Environment.GetEnvironmentVariable("PHARE_PROJECT_ID") is { Length: > 0 } project)
    Sample.Http.DefaultRequestHeaders.Add("X-Phare-Project-Id", project);

await Sample.Run("Uptime", TimeSpan.FromMinutes(1), Down, (c, down) =>
{
    if (down.Count == 0) return null;
    int title = c.Size(0.11), row = Math.Min(c.Size(0.09), (c.Height - 2 * title) / Math.Max(1, down.Count));
    c.Box(0, 0, c.Width, c.Height, 0x300808);
    c.Text(down.Count == 1 ? "1 monitor down" : $"{down.Count} monitors down", c.Width / 2, title / 2, title, 0xFF5050, Canvas.Align.Center, bold: true);
    for (int i = 0; i < down.Count; i++)
    {
        var (name, status) = down[i];
        c.Text($"{name}  ·  {status}", c.Width / 2, title * 2 + i * row, row * 4 / 5, status == "partial" ? 0xFFB040u : 0xFFFFFFu, Canvas.Align.Center);
    }
    return true;
});
return 0;

// Every monitor that is offline, or offline in some regions ("partial"), across all pages.
static async Task<List<(string Name, string Status)>> Down()
{
    var down = new List<(string, string)>();
    for (string? url = "https://api.phare.io/uptime/monitors"; url is not null;)
    {
        var page = JsonNode.Parse(await Sample.Http.GetStringAsync(url))!;
        foreach (var m in page["data"]!.AsArray())
            if ((string?)m!["status"] is "offline" or "partial") down.Add(((string)m["name"]!, (string)m["status"]!));
        url = (string?)page["links"]?["next"];
    }
    return down;
}
