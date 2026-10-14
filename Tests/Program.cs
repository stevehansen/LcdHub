using LcdHub;
using LcdHub.Client;
using static LcdHub.Rotation;

static void Check(bool result, string description)
{
    if (!result) throw new Exception(description);
    Console.WriteLine("PASS: " + description);
}

var interval = TimeSpan.FromSeconds(10);
var early = TimeSpan.FromSeconds(3);
var late = TimeSpan.FromSeconds(10);
Offer A(string name) => new(name, false);
Offer Alert(string name) => new(name, true);
string P(Offer[] offers, string? current, TimeSpan shown, bool today = false, bool feed = false, bool locked = false, bool off = false,
    string? pin = null) =>
    Pick(offers, pin, today, feed, locked, off, current, shown, interval);

// ---- rotation ----
Check(P([], null, early) == Rotation.Today, "nothing offered → Today");
Check(P([A("a")], null, early) == "a", "first pick is the first producer");
Check(P([A("a"), A("b")], "a", early) == "a", "stays for the interval");
Check(P([A("a"), A("b")], "a", late) == "b", "moves on after the interval");
Check(P([A("a"), A("b")], "b", late) == "a", "wraps around");
Check(P([A("a")], "a", late) == "a", "a lone producer stays");
Check(P([A("a")], "a", late, today: true) == Rotation.Today, "Today takes a turn when enabled");
Check(P([A("a")], Rotation.Today, late, today: true) == "a", "and hands back");
Check(P([A("b")], "a", early) == "b", "a producer that left is replaced at once");
Check(P([A("a"), Alert("b")], "a", early) == "b", "an alert preempts mid-interval");
Check(P([Alert("a"), Alert("b")], "a", late) == "b", "alerts rotate among themselves");
Check(P([A("a"), Alert("b")], "b", late) == "b", "a lone alert stays");
Check(P([A("a")], "b", early) == "a", "after the alert clears, back to rotation");
Check(P([A("a")], null, early, feed: true) == Feed, "a feed replaces the rotation");
Check(P([Alert("a")], Feed, early, feed: true) == "a", "an alert overrides a feed");
Check(P([A("a"), A("b")], "b", late, pin: "b") == "b", "a pinned producer stays past the interval");
Check(P([A("a"), A("b")], "a", early, pin: "b") == "b", "pinning takes over mid-interval");
Check(P([A("a")], "a", late, pin: "b") == "a", "a pinned producer without a frame doesn't count");
Check(P([A("b"), Alert("a")], "b", early, pin: "b") == "a", "an alert overrides a pin");
Check(P([A("b")], null, early, locked: true, pin: "b") == Rotation.Today, "locked beats a pin");
Check(P([Alert("a")], null, early, locked: true) == Rotation.Today, "locked shows only Today");
Check(P([Alert("a")], null, early, locked: true, off: true) == Blank, "display off blanks");

// ---- wire ----
var hello = LcdHubWire.Read(new MemoryStream(LcdHubWire.Hello("Example App")))!.Value;
Check(hello.Type == LcdHubWire.MsgHello && LcdHubWire.ParseHello(hello.Body) == (LcdHubWire.Version, "Example App"),
    "hello round-trips");

LcdHubPanel[] panels = [new("0416:5302", 1280, 480), new("0416:5408:ABC", 1920, 462)];
var p = LcdHubWire.Read(new MemoryStream(LcdHubWire.Panels(panels)))!.Value;
Check(p.Type == LcdHubWire.MsgPanels && LcdHubWire.ParsePanels(p.Body).SequenceEqual(panels), "panels round-trip");

var pixels = Enumerable.Range(0, 1280 * 480).Select(i => (uint)i * 2654435761u).ToArray();
var stream = new MemoryStream();
LcdHubWire.Write(stream, LcdHubWire.Frame("0416:5302", true, pixels));
LcdHubWire.Write(stream, LcdHubWire.Frame("x", false, [1, 2]));
stream.Position = 0;
var (id, alert, back) = LcdHubWire.ParseFrame(LcdHubWire.Read(stream)!.Value.Body);
Check(id == "0416:5302" && alert && back.SequenceEqual(pixels), "a full frame round-trips");
var (id2, alert2, back2) = LcdHubWire.ParseFrame(LcdHubWire.Read(stream)!.Value.Body);
Check(id2 == "x" && !alert2 && back2.SequenceEqual([1u, 2u]), "messages stay framed back to back");
Check(LcdHubWire.Read(stream) is null, "clean end of stream reads null");
var (_, _, empty) = LcdHubWire.ParseFrame(LcdHubWire.Read(new MemoryStream(LcdHubWire.Frame("x", false, [])))!.Value.Body);
Check(empty.Length == 0, "an empty frame (withdraw) round-trips");

var style = new LcdHubStyle(true, 0xF3F3F3, 0x1A1A1A, 0x0078D4);
Check(LcdHubWire.ParseStyle(LcdHubWire.Read(new MemoryStream(LcdHubWire.Style(style)))!.Value.Body) == style, "style round-trips");
Check(LcdHubWire.ParseVisibility(LcdHubWire.Read(new MemoryStream(LcdHubWire.Visibility("x", LcdHubVisibility.Next)))!.Value.Body)
      == ("x", LcdHubVisibility.Next), "visibility round-trips");
var longer = LcdHubWire.Visibility("x", LcdHubVisibility.Shown).Concat(new byte[] { 9, 9 }).ToArray();
longer[0] += 2; // the length now covers two fields a newer hub added
Check(LcdHubWire.ParseVisibility(LcdHubWire.Read(new MemoryStream(longer))!.Value.Body) == ("x", LcdHubVisibility.Shown),
    "fields a newer peer appends are ignored");

var truncated = LcdHubWire.Hello("abc")[..^1];
var threw = false;
try { LcdHubWire.Read(new MemoryStream(truncated)); } catch (IOException) { threw = true; }
Check(threw, "a truncated message throws");

var huge = new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, 3 };
threw = false;
try { LcdHubWire.Read(new MemoryStream(huge)); } catch (IOException) { threw = true; }
Check(threw, "an absurd length is refused");

// ---- settings ----
var good = HubSettings.Parse("""{ "rotate_seconds": 15, "style": { "mode": "light", "accent": "#112233" } }""", out var unreadable);
Check(!unreadable && good.Problems.Count == 0 && good.RotateSeconds == 15, "valid settings load without problems");
var typo = HubSettings.Parse("""{ "today": { "wether_location": "Antwerp" }, "colour": 1 }""", out _);
Check(typo.Problems.Count == 2 && typo.Problems.Exists(p => p.Contains("today.wether_location")), "unknown keys are reported with their path");
Check(System.Text.Json.JsonSerializer.Serialize(typo, SettingsJsonContext.Default.HubSettings).Contains("wether_location"),
    "unknown keys survive a save");
var bad = HubSettings.Parse("""{ "rotate_seconds": 0, "style": { "mode": "dim", "accent": "blue" }, "launch": [ {} ] }""", out _);
Check(bad.Problems.Count == 4 && bad.RotateSeconds == HubSettings.DefaultRotateSeconds, "bad values are reported, and repaired where needed");
HubSettings.Parse("{ nope", out unreadable);
Check(unreadable, "unparsable JSON is flagged, so Load keeps a copy");

// Every settings property is in the schema an editor validates against, and nothing else.
var schemaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "settings.schema.json"));
var schema = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(schemaPath))!;
static string[] Keys(Type t) => t.GetProperties()
    .Select(p => p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false)
        .Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>().FirstOrDefault()?.Name)
    .OfType<string>().Order().ToArray();
static string[] SchemaKeys(System.Text.Json.Nodes.JsonNode node) => node["properties"]!.AsObject().Select(p => p.Key).Order().ToArray();
var props = schema["properties"]!;
foreach (var (node, type) in new[] { (schema, typeof(HubSettings)), (props["today"]!, typeof(TodaySettings)), (props["style"]!, typeof(StyleSettings)),
             (props["launch"]!["items"]!, typeof(LaunchSettings)), (props["panels"]!["additionalProperties"]!, typeof(PanelSettings)) })
    Check(SchemaKeys(node).SequenceEqual(Keys(type)), $"settings.schema.json matches {type.Name}");

Console.WriteLine("All checks passed.");
