using System.Text.Json;
using System.Text.Json.Serialization;

namespace LcdHub;

/// <summary>What each panel is set to show, by <see cref="LcdDevice.Id"/>, so it survives replugs and restarts.</summary>
internal sealed class PanelSettings
{
    /// <summary>Keys this version doesn't know (typos, mostly): reported, and kept on save.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>Today takes its turn in the rotation (it shows anyway when nothing else does).</summary>
    [JsonPropertyName("today")] public bool Today { get; set; }

    /// <summary>Producers, by name, left out of this panel's rotation. Unknown names are shown by default.</summary>
    [JsonPropertyName("hidden")] public List<string> Hidden { get; set; } = new();

    /// <summary>A producer, by name, kept on this panel instead of rotating; alerts still interrupt it.</summary>
    [JsonPropertyName("pinned")] public string? Pinned { get; set; }

    /// <summary>A video playing instead of the rotation. A mirrored window isn't saved: it can't be found again.</summary>
    [JsonPropertyName("video_path")] public string? VideoPath { get; set; }
}

/// <summary>What Today shows, the same on every panel.</summary>
internal sealed class TodaySettings
{
    /// <summary>Keys this version doesn't know (typos, mostly): reported, and kept on save.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>A place name for Open-Meteo to look up, e.g. "Antwerp". Without one there is no weather.</summary>
    [JsonPropertyName("weather_location")] public string? WeatherLocation { get; set; }
    [JsonPropertyName("weather")] public bool Weather { get; set; } = true;
    [JsonPropertyName("date")] public bool Date { get; set; } = true;
    /// <summary>A holiday's name under the date, on the day.</summary>
    [JsonPropertyName("holidays")] public bool Holidays { get; set; } = true;
    /// <summary>The sky colours for the time of day; off is plain black.</summary>
    [JsonPropertyName("sky")] public bool Sky { get; set; } = true;
    /// <summary>Stars, the sun arc and the season's or holiday's particles.</summary>
    [JsonPropertyName("effects")] public bool Effects { get; set; } = true;
}

/// <summary>The look producers are asked to share (<see cref="HubStyle"/>).</summary>
internal sealed class StyleSettings
{
    /// <summary>Keys this version doesn't know (typos, mostly): reported, and kept on save.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary><see cref="HubStyle.System"/> follows Windows' app mode; or dark or light.</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = HubStyle.System;
    /// <summary>"#RRGGBB"; without one, Windows' accent colour.</summary>
    [JsonPropertyName("accent")] public string? Accent { get; set; }
}

/// <summary>A producer the hub starts with itself and keeps running (<see cref="Launcher"/>).</summary>
internal sealed class LaunchSettings
{
    /// <summary>Keys this version doesn't know (typos, mostly): reported, and kept on save.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>The app's own exe; environment variables like %USERPROFILE% are expanded.</summary>
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("args")] public string? Args { get; set; }
}

internal sealed class HubSettings
{
    public const int DefaultRotateSeconds = 10;
    public const int DefaultNightBrightness = 50;
    public const string SchemaUrl = "https://raw.githubusercontent.com/stevehansen/LcdHub/main/settings.schema.json";
    public const string HelpUrl = "https://github.com/stevehansen/LcdHub/blob/main/docs/settings.md";

    /// <summary>Lets an editor validate the file and explain each key; rewritten on every save.</summary>
    [JsonPropertyName("$schema")] public string? Schema { get; set; }

    [JsonPropertyName("rotate_seconds")] public int RotateSeconds { get; set; } = DefaultRotateSeconds;
    /// <summary>Apps, by name, that stay on a panel for their own number of seconds instead of rotate_seconds.</summary>
    [JsonPropertyName("app_seconds")] public Dictionary<string, int> AppSeconds { get; set; } = new();
    /// <summary>Percent the panels dim to between dusk and dawn; 100 leaves them alone.</summary>
    [JsonPropertyName("night_brightness")] public int NightBrightness { get; set; } = DefaultNightBrightness;
    [JsonPropertyName("today")] public TodaySettings Today { get; set; } = new();
    [JsonPropertyName("style")] public StyleSettings Style { get; set; } = new();
    [JsonPropertyName("launch")] public List<LaunchSettings> Launch { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>What was wrong with the file at load, for the tray to show. Empty when all is well.</summary>
    [JsonIgnore] public List<string> Problems { get; private set; } = new();

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LcdHub", "settings.json");
    [JsonPropertyName("panels")] public Dictionary<string, PanelSettings> Panels { get; set; } = new();

    public PanelSettings For(string panelId)
    {
        if (!Panels.TryGetValue(panelId, out var p)) Panels[panelId] = p = new PanelSettings();
        return p;
    }

    /// <summary>
    /// Never fails: an unreadable file is copied to settings.json.bad before anything can save
    /// defaults over it, and every problem found is in <see cref="Problems"/>.
    /// </summary>
    /// <param name="unreadable">The file couldn't be read or parsed, so this is all defaults.</param>
    public static HubSettings Load(out bool unreadable)
    {
        string? json = null, readError = null;
        try
        {
            if (File.Exists(FilePath)) json = File.ReadAllText(FilePath);
        }
        catch (IOException ex) { readError = ex.Message; }
        var s = Parse(json, out unreadable);
        unreadable |= readError is not null;
        if (unreadable)
        {
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); }
            catch (IOException) { }
        }
        if (readError is not null) s.Problems.Insert(0, $"{Strings.SettingsUnreadable} ({readError})");
        foreach (var problem in s.Problems) Diagnose.Log($"settings: {problem}");
        return s;
    }

    /// <summary>The settings in <paramref name="json"/> (defaults for null), checked.</summary>
    /// <param name="unreadable">The JSON couldn't be parsed at all, so this is all defaults.</param>
    internal static HubSettings Parse(string? json, out bool unreadable)
    {
        HubSettings? s = null;
        string? error = null;
        try
        {
            if (json is not null) s = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.HubSettings);
        }
        catch (JsonException ex) { error = $"{Strings.SettingsUnreadable} ({ex.Message})"; }
        s ??= new HubSettings();
        unreadable = error is not null;
        if (error is not null) s.Problems.Add(error);
        s.Validate();
        return s;
    }

    /// <summary>Repairs what it can (falling back to the default) and reports everything it finds.</summary>
    private void Validate()
    {
        void Report(string where, Dictionary<string, JsonElement>? keys)
        {
            foreach (var key in keys?.Keys ?? Enumerable.Empty<string>()) Problems.Add($"{Strings.SettingsUnknownKey}: {where}{key}");
        }

        Report("", Unknown);
        Report("today.", Today.Unknown);
        Report("style.", Style.Unknown);
        foreach (var (id, panel) in Panels) Report($"panels.{id}.", panel.Unknown);
        if (RotateSeconds < 1)
        {
            Problems.Add($"{Strings.SettingsBadValue}: rotate_seconds = {RotateSeconds}");
            RotateSeconds = DefaultRotateSeconds;
        }
        foreach (var (app, seconds) in AppSeconds.Where(a => a.Value < 1).ToList())
        {
            Problems.Add($"{Strings.SettingsBadValue}: app_seconds.{app} = {seconds}");
            AppSeconds.Remove(app);
        }
        if (NightBrightness is < 0 or > 100)
        {
            Problems.Add($"{Strings.SettingsBadValue}: night_brightness = {NightBrightness}");
            NightBrightness = DefaultNightBrightness;
        }
        if (Style.Mode is not (HubStyle.System or HubStyle.Dark or HubStyle.Light))
            Problems.Add($"{Strings.SettingsBadValue}: style.mode = \"{Style.Mode}\"");
        if (Style.Accent is not null && HubStyle.ParseColor(Style.Accent) is null)
            Problems.Add($"{Strings.SettingsBadValue}: style.accent = \"{Style.Accent}\"");
        for (int i = 0; i < Launch.Count; i++)
        {
            Report($"launch[{i}].", Launch[i].Unknown);
            if (string.IsNullOrWhiteSpace(Launch[i].Path)) Problems.Add($"{Strings.SettingsBadValue}: launch[{i}].path");
            else if (!File.Exists(Environment.ExpandEnvironmentVariables(Launch[i].Path!)))
                Problems.Add($"{Strings.SettingsMissingFile}: {Launch[i].Path}");
        }
    }

    /// <summary>Best effort; a failed save only costs the choice at the next start.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            Schema = SchemaUrl;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJsonContext.Default.HubSettings));
        }
        catch (Exception ex) { Diagnose.LogError("settings save", ex); }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HubSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext;
