using System.Text.Json;
using System.Text.Json.Serialization;

namespace LcdHub;

/// <summary>What each panel is set to show, by <see cref="LcdDevice.Id"/>, so it survives replugs and restarts.</summary>
internal sealed class PanelSettings
{
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
    /// <summary><see cref="HubStyle.System"/> follows Windows' app mode; or dark or light.</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = HubStyle.System;
    /// <summary>"#RRGGBB"; without one, Windows' accent colour.</summary>
    [JsonPropertyName("accent")] public string? Accent { get; set; }
}

/// <summary>A producer the hub starts with itself and keeps running (<see cref="Launcher"/>).</summary>
internal sealed class LaunchSettings
{
    /// <summary>The app's own exe; environment variables like %USERPROFILE% are expanded.</summary>
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("args")] public string? Args { get; set; }
}

internal sealed class HubSettings
{
    public const int DefaultRotateSeconds = 10;

    [JsonPropertyName("rotate_seconds")] public int RotateSeconds { get; set; } = DefaultRotateSeconds;
    [JsonPropertyName("today")] public TodaySettings Today { get; set; } = new();
    [JsonPropertyName("style")] public StyleSettings Style { get; set; } = new();
    [JsonPropertyName("launch")] public List<LaunchSettings> Launch { get; set; } = new();
    [JsonPropertyName("panels")] public Dictionary<string, PanelSettings> Panels { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LcdHub", "settings.json");

    public PanelSettings For(string panelId)
    {
        if (!Panels.TryGetValue(panelId, out var p)) Panels[panelId] = p = new PanelSettings();
        return p;
    }

    public static HubSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.HubSettings) is { } s)
            {
                if (s.RotateSeconds < 1) s.RotateSeconds = DefaultRotateSeconds;
                return s;
            }
        }
        catch (Exception ex) { Diagnose.LogError("settings load", ex); }
        return new HubSettings();
    }

    /// <summary>Best effort; a failed save only costs the choice at the next start.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJsonContext.Default.HubSettings));
        }
        catch (Exception ex) { Diagnose.LogError("settings save", ex); }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HubSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext;
