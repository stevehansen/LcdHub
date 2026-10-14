using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LcdHub;

/// <summary>The weather for one place right now, plus today's range and sun times.</summary>
internal sealed record Weather(string Place, double Latitude, double Temperature, int Code, double Wind,
    double Max, double Min, DateTime? Sunrise, DateTime? Sunset);

/// <summary>
/// Keeps <see cref="Current"/> up to date for the configured place, from Open-Meteo (free, no
/// key), every 15 minutes in the background. A failed fetch keeps the last weather; without a
/// place there is none and Today shows only the time.
/// </summary>
internal static class WeatherSource
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static Weather? _current;
    // UI thread only: the place being fetched, and how to stop fetching it.
    private static string? _place;
    private static CancellationTokenSource? _stop;

    static WeatherSource() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("LcdHub/1.0");

    public static Weather? Current => Volatile.Read(ref _current);

    /// <summary>
    /// Fetches for <paramref name="place"/> from now on. A new place drops the old reading at once,
    /// so Today never shows one place's weather under another's name; the same place changes nothing.
    /// </summary>
    /// <param name="changed">Called on a background thread after each new reading.</param>
    public static void Start(string? place, Action changed)
    {
        place = string.IsNullOrWhiteSpace(place) ? null : place.Trim();
        if (string.Equals(place, _place, StringComparison.OrdinalIgnoreCase)) return;
        _stop?.Cancel();
        _stop = null;
        _place = place;
        Volatile.Write(ref _current, null);
        if (place is null) return;
        var stop = _stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            (string Name, double Lat, double Lon)? where = null;
            while (!stop.IsCancellationRequested)
            {
                // A failed fetch (Open-Meteo answers 503 now and then) is retried soon, not in 15 minutes.
                var next = RetryAfter;
                try
                {
                    where ??= await Geocode(place);
                    if (where is { } w)
                    {
                        var reading = await Fetch(w.Name, w.Lat, w.Lon);
                        if (stop.IsCancellationRequested) return;
                        Volatile.Write(ref _current, reading);
                        changed();
                        next = Every;
                    }
                    else next = Every; // an unknown place won't become known by asking again soon
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                               or InvalidOperationException or NullReferenceException or FormatException)
                {
                    Diagnose.LogError("weather", ex);
                }
                try { await Task.Delay(next, stop.Token); }
                catch (OperationCanceledException) { return; }
            }
        });
    }

    private static async Task<(string, double, double)?> Geocode(string place)
    {
        var hit = JsonNode.Parse(await Http.GetStringAsync(
            $"https://geocoding-api.open-meteo.com/v1/search?count=1&name={Uri.EscapeDataString(place)}"))?["results"]?[0];
        if (hit is null)
        {
            Diagnose.Log($"weather: Open-Meteo doesn't know \"{place}\"");
            return null;
        }
        return ((string)hit["name"]!, (double)hit["latitude"]!, (double)hit["longitude"]!);
    }

    private static async Task<Weather> Fetch(string name, double lat, double lon)
    {
        var json = JsonNode.Parse(await Http.GetStringAsync(
            $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
            "&current=temperature_2m,weather_code,wind_speed_10m&daily=temperature_2m_max,temperature_2m_min,sunrise,sunset" +
            "&forecast_days=1&timezone=auto"))!;
        var now = json["current"]!;
        var day = json["daily"]!;
        return new Weather(name, lat, (double)now["temperature_2m"]!, (int)now["weather_code"]!, (double)now["wind_speed_10m"]!,
            (double)day["temperature_2m_max"]![0]!, (double)day["temperature_2m_min"]![0]!,
            Time(day["sunrise"]?[0]), Time(day["sunset"]?[0]));
    }

    // Local time at the place; the hub assumes it sits in the same zone.
    private static DateTime? Time(JsonNode? n) => n is null ? null : DateTime.Parse((string)n!, CultureInfo.InvariantCulture);
}
