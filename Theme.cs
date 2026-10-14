namespace LcdHub;

internal enum DayPhase { Dawn, Day, Dusk, Night }
internal enum Season { Spring, Summer, Autumn, Winter }
internal enum Particles { None, Blossom, Fireflies, Leaves, Snow, Confetti, Embers }

/// <summary>
/// The look of a moment on Today: the sky for the time of day, the season (flipped south of the
/// equator) for the accent and particles, and a holiday's name when there is one. Colors are 0xRRGGBB.
/// </summary>
internal sealed record Theme(DayPhase Phase, double SunProgress, uint SkyTop, uint SkyBottom, uint Accent, string? Holiday, Particles Particles)
{
    private static readonly TimeSpan Twilight = TimeSpan.FromMinutes(40);
    private static readonly uint[] SeasonAccent = [0x7CD67C, 0xFFC145, 0xE8833A, 0x7FB8FF];

    /// <param name="sunrise">Today's, when the weather knows it; otherwise a plain 7-to-7 day.</param>
    public static Theme For(DateTime now, double latitude, DateTime? sunrise, DateTime? sunset)
    {
        var rise = sunrise ?? now.Date.AddHours(7);
        var set = sunset ?? now.Date.AddHours(19);
        var phase = (now - rise).Duration() < Twilight ? DayPhase.Dawn
            : (now - set).Duration() < Twilight ? DayPhase.Dusk
            : now > rise && now < set ? DayPhase.Day
            : DayPhase.Night;
        // How far the sun (or, at night, the moon) is along its arc, 0 to 1.
        var progress = now >= rise && now <= set
            ? (now - rise) / (set - rise)
            : (now < rise ? now - set.AddDays(-1) : now - set) / (rise.AddDays(1) - set);
        var (top, bottom) = phase switch
        {
            DayPhase.Dawn => (0x22305Au, 0xB47A64u),
            DayPhase.Day => (0x1E4676u, 0x46698Cu),
            DayPhase.Dusk => (0x221838u, 0xA85C40u),
            _ => (0x070B1Au, 0x141C34u),
        };

        var season = SeasonOf(now, southern: latitude < 0);
        var accent = SeasonAccent[(int)season];
        var particles = season switch
        {
            Season.Spring => Particles.Blossom,
            Season.Summer => phase == DayPhase.Night ? Particles.Fireflies : Particles.None,
            Season.Autumn => Particles.Leaves,
            _ => Particles.Snow,
        };
        string? holiday = null;
        if (Holidays.On(now.Date) is { } h) (holiday, accent, particles) = (h.Name, h.Color, h.Particles ?? particles);
        return new Theme(phase, Math.Clamp(progress, 0, 1), top, bottom, accent, holiday, particles);
    }

    /// <summary>
    /// 1 by day, 0 at night, ramping over the twilight after sunset and before sunrise, so
    /// night dimming never jumps. Same 7-to-7 fallback as <see cref="For"/>.
    /// </summary>
    public static double Daylight(DateTime now, DateTime? sunrise, DateTime? sunset)
    {
        var rise = sunrise ?? now.Date.AddHours(7);
        var set = sunset ?? now.Date.AddHours(19);
        if (now >= rise && now <= set) return 1;
        var fromDark = now > set ? Twilight - (now - set) : Twilight - (rise - now);
        return Math.Clamp(fromDark / Twilight, 0, 1);
    }

    // Meteorological seasons: whole months, March–May is spring up north.
    private static Season SeasonOf(DateTime d, bool southern) =>
        ((southern ? (d.Month + 5) % 12 + 1 : d.Month) switch
        {
            3 or 4 or 5 => Season.Spring,
            6 or 7 or 8 => Season.Summer,
            9 or 10 or 11 => Season.Autumn,
            _ => Season.Winter,
        });
}

/// <summary>Days marked in much of the world.</summary>
internal static class Holidays
{
    public static (string Name, uint Color, Particles? Particles)? On(DateTime date)
    {
        var easter = EasterSunday(date.Year);
        foreach (var (day, name, color, particles) in new (DateTime, string, uint, Particles?)[]
        {
            (new(date.Year, 1, 1), Strings.NewYearsDay, 0xFFD54A, Particles.Confetti),
            (new(date.Year, 2, 14), Strings.ValentinesDay, 0xFF6F91, null),
            (easter, Strings.Easter, 0xF3A6D0, Particles.Blossom),
            (easter.AddDays(1), Strings.EasterMonday, 0xF3A6D0, Particles.Blossom),
            (new(date.Year, 5, 1), Strings.LabourDay, 0xE05050, null),
            (easter.AddDays(39), Strings.AscensionDay, 0xA8D8FF, null),
            (easter.AddDays(49), Strings.Pentecost, 0xFF9A4A, null),
            (easter.AddDays(50), Strings.WhitMonday, 0xFF9A4A, null),
            (new(date.Year, 10, 31), Strings.Halloween, 0xFF8C1A, Particles.Embers),
            (new(date.Year, 12, 24), Strings.ChristmasEve, 0x4FC46A, Particles.Snow),
            (new(date.Year, 12, 25), Strings.ChristmasDay, 0xE04848, Particles.Snow),
            (new(date.Year, 12, 26), Strings.BoxingDay, 0x4FC46A, Particles.Snow),
            (new(date.Year, 12, 31), Strings.NewYearsEve, 0xFFD54A, Particles.Confetti),
        })
            if (day == date.Date) return (name, color, particles);
        return null;
    }

    /// <summary>Western (Gregorian) Easter, by the anonymous Gregorian algorithm.</summary>
    public static DateTime EasterSunday(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4;
        int f = (b + 8) / 25, g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(year, month, day);
    }
}
