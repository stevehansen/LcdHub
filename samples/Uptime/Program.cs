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
    int offline = down.Count(d => d.Status == "offline"), partial = down.Count - offline;
    string Monitors(int n) => n == 1 ? "1 monitor" : $"{n} monitors";
    var headline = offline > 0 && partial > 0 ? $"{offline} down  ·  {partial} partly down"
        : offline > 0 ? $"{Monitors(offline)} down" : $"{Monitors(partial)} partly down";
    uint Color(string status) => status == "offline" ? 0xFF5050u : 0xFFB040u;

    // Headline and rows as one block, centred; rows shrink when there are many.
    int title = c.Size(0.17), gap = title / 2, margin = c.Size(0.08);
    int row = Math.Min(c.Size(0.13), (c.Height - 2 * margin - title - gap) / down.Count);
    int y = (c.Height - (title + gap + row * down.Count)) / 2;
    c.Box(0, 0, c.Width, c.Height, offline > 0 ? 0x300808u : 0x2A1C04u);
    c.Text(headline, c.Width / 2, y, title, Color(offline > 0 ? "offline" : "partial"), Canvas.Align.Center, bold: true);
    y += title + gap;
    foreach (var (name, status) in down)
    {
        // The headline already says it when every monitor is in the same state.
        var line = offline > 0 && partial > 0 ? $"{name}  ·  {(status == "offline" ? "down" : "partly down")}" : name;
        c.Text(line,c.Width / 2, y, row * 4 / 5, Color(status), Canvas.Align.Center);
        y += row;
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
