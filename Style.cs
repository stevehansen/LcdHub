using System.Globalization;
using LcdHub.Client;
using Microsoft.Win32;

namespace LcdHub;

/// <summary>Turns the style settings into the <see cref="LcdHubStyle"/> every producer is sent.</summary>
internal static class HubStyle
{
    public const string System = "system", Dark = "dark", Light = "light";

    private static readonly LcdHubStyle DarkBase = LcdHubStyle.Default;
    private static readonly LcdHubStyle LightBase = new(true, 0xF3F3F3, 0x1A1A1A, LcdHubStyle.Default.Accent);

    /// <summary>Cheap enough for every tick, which is how a change in Windows' theme gets noticed.</summary>
    public static LcdHubStyle Current(StyleSettings settings)
    {
        var light = settings.Mode switch
        {
            Light => true,
            Dark => false,
            _ => ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme") == 1,
        };
        return (light ? LightBase : DarkBase) with { Accent = ParseColor(settings.Accent) ?? WindowsAccent() ?? DarkBase.Accent };
    }

    // DWORD 0xAABBGGRR.
    private static uint? WindowsAccent() =>
        ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor") is { } abgr
            ? ((abgr & 0xFF) << 16) | (abgr & 0xFF00) | ((abgr >> 16) & 0xFF)
            : null;

    // "#RRGGBB"; anything else is ignored.
    private static uint? ParseColor(string? hex) =>
        hex is ['#', .. var digits] && digits.Length == 6
        && uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb) ? rgb : null;

    private static uint? ReadDword(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(name) is int v ? (uint)v : null;
    }
}
