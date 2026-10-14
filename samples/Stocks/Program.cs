// Today's price and chart for a few tickers, from Yahoo Finance's public chart endpoint (no key,
// unofficial: fine for a desk display, not for trading). With --alert N, a move of N% or more
// either way takes over the panel.
//   dotnet run --project samples/Stocks -- MSFT NVDA BTC-USD --alert 5
using System.Text.Json.Nodes;
using LcdHub.Samples;

var alertAt = double.PositiveInfinity;
var symbols = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--alert" && i + 1 < args.Length) alertAt = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else symbols.Add(args[i].ToUpperInvariant());
}
if (symbols.Count == 0) symbols = ["MSFT", "AAPL", "NVDA"];

await Sample.Run("Stocks", TimeSpan.FromMinutes(1),
    () => Task.WhenAll(symbols.Select(Quote)),
    (c, quotes) =>
    {
        int rowH = c.Height / quotes.Length, pad = rowH / 8, text = Math.Min(rowH / 3, c.Size(0.12));
        var alert = false;
        for (int i = 0; i < quotes.Length; i++)
        {
            var q = quotes[i];
            int top = i * rowH;
            var change = (q.Price / q.PreviousClose - 1) * 100;
            alert |= Math.Abs(change) >= alertAt;
            var color = change >= 0 ? 0x40D080u : 0xFF5050u;
            if (Math.Abs(change) >= alertAt) c.Box(0, top, c.Width, rowH, 0x401010);
            c.Text(q.Symbol, pad, top + pad, text, c.Style.Text, bold: true);
            c.Text($"{q.Price:N2} {q.Currency}", pad, top + rowH - pad - text, text, c.Style.Text);
            c.Text($"{change:+0.00;-0.00}%", c.Width / 2 - pad, top + rowH / 2 - text / 2, text, color, Canvas.Align.Right, bold: true);
            Chart(c, q, c.Width / 2, top + pad, c.Width / 2 - pad, rowH - 2 * pad, color);
        }
        return alert;
    });

static async Task<Quote> Quote(string symbol)
{
    var result = JsonNode.Parse(await Sample.Http.GetStringAsync(
        $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range=1d&interval=5m"))!["chart"]!["result"]?[0]
        ?? throw new InvalidOperationException($"no quote for {symbol}");
    var meta = result["meta"]!;
    var closes = result["indicators"]!["quote"]![0]!["close"]!.AsArray()
        .Where(v => v is not null).Select(v => (double)v!).ToArray();
    return new(symbol, (double)meta["regularMarketPrice"]!, (double)meta["chartPreviousClose"]!, (string?)meta["currency"] ?? "", closes);
}

// The day's prices against yesterday's close, which is the dotted baseline.
static void Chart(Canvas c, Quote q, int x, int y, int w, int h, uint color)
{
    var all = q.Closes.Append(q.PreviousClose);
    double lo = all.Min(), hi = all.Max(), span = Math.Max(hi - lo, 1e-9);
    int Y(double v) => y + (int)((hi - v) / span * h);
    for (int dx = 0; dx < w; dx += 8) c.Box(x + dx, Y(q.PreviousClose), 3, 1, 0x606870);
    var points = q.Closes.Select((v, i) => (x + i * w / Math.Max(1, q.Closes.Length - 1), Y(v))).ToArray();
    c.Line(points, color, Math.Max(2, h / 40));
}

record Quote(string Symbol, double Price, double PreviousClose, string Currency, double[] Closes);
