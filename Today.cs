using System.Globalization;

namespace LcdHub;

/// <summary>
/// The hub's own screen, for when nothing else is on and while the session is locked: the time,
/// the date and the weather under a sky that follows the day (<see cref="Theme"/>), with the
/// season's or a holiday's particles. Each part can be switched off (<see cref="TodaySettings"/>).
/// It changes at most once a minute, plus when the weather does.
/// </summary>
internal static unsafe class Today
{
    private const uint Text = 0xFFFFFF, SubText = 0xE6EAF0, Muted = 0xCDD4DC;

    public static uint[]? Paint(int w, int h, DateTime now, Weather? weather, TodaySettings opt)
    {
        var dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        var dib = Gdi.CreateTopDownDib(dc, w, h, out var bits);
        if (dib == IntPtr.Zero)
        {
            NativeMethods.DeleteDC(dc);
            return null;
        }
        var old = NativeMethods.SelectObject(dc, dib);
        try
        {
            var px = new Span<uint>((void*)bits, w * h);
            if (!opt.Weather) weather = null;
            var theme = Theme.For(now, weather?.Latitude ?? 0, weather?.Sunrise, weather?.Sunset);
            var s = h / 240.0;

            if (opt.Sky) Gradient(px, w, h, theme.SkyTop, theme.SkyBottom);
            else px.Clear();
            if (opt.Effects)
            {
                var seed = now.Year * 1000 + now.DayOfYear;
                if (opt.Sky && theme.Phase == DayPhase.Night) Stars(px, w, h, s, seed);
                Scatter(px, w, h, s, theme.Particles, seed);
            }

            // Side by side on a wide panel, stacked on a square or tall one; the clock alone
            // takes the whole panel.
            var wide = weather is not null && w >= h * 3 / 2;
            int bw = wide ? w / 2 : w, bh = weather is not null && !wide ? h / 2 : h;
            int big = Math.Min(weather is null ? bh / 2 : bh * 2 / 5, bw / 4), small = Math.Max(12, bh / 10);

            NativeMethods.SetBkMode(dc, Win32Consts.TRANSPARENT);
            var holiday = opt.Holidays ? theme.Holiday : null;
            var lines = big + (opt.Date ? small * 3 / 2 : 0) + (holiday is not null ? small * 3 / 2 : 0);
            var y = bh / 2 - lines / 2;
            var culture = CultureInfo.CurrentCulture;
            DrawText(dc, now.ToString("HH:mm", culture),
                bw / 2, y - big / 6, big, Text, bold: true);
            y += big;
            if (opt.Date)
            {
                DrawText(dc, $"{now.ToString("dddd", culture)} {now.ToString(culture.DateTimeFormat.MonthDayPattern, culture)}",
                    bw / 2, y, small, SubText);
                y += small * 3 / 2;
            }
            if (holiday is not null) DrawText(dc, holiday, bw / 2, y, small, theme.Accent, bold: true);

            if (weather is not null)
            {
                int wx = wide ? bw + bw / 2 : bw / 2, wy = wide ? 0 : bh;
                if (wide) Box(px, w, h, bw, h / 8, Math.Max(1, w / 640), h * 3 / 4, 0xFFFFFF, 0.15);
                if (opt.Effects) SunArc(px, w, h, s, wx, wy + bh - small * 2, Math.Min(bw / 2 - small, bh - small * 3), theme);
                DrawText(dc, weather.Place, wx, wy + bh / 12, small, Muted);
                DrawText(dc, $"{weather.Temperature.ToString("0", culture)}°", wx, wy + bh / 2 - big * 3 / 5, big, Text, bold: true);
                DrawText(dc, Describe(weather.Code), wx, wy + bh / 2 + big / 2, small, weather.Code >= 95 ? 0xFF6060u : SubText);
                DrawText(dc, $"↑ {weather.Max.ToString("0", culture)}°   ↓ {weather.Min.ToString("0", culture)}°   " +
                             $"{weather.Wind.ToString("0", culture)} {Strings.KilometresPerHour}",
                    wx, wy + bh - bh / 12 - small, small, Muted);
            }

            NativeMethods.GdiFlush();
            var pixels = new uint[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = px[i] & 0x00FFFFFF;
            return pixels;
        }
        finally
        {
            NativeMethods.SelectObject(dc, old);
            NativeMethods.DeleteObject(dib);
            NativeMethods.DeleteDC(dc);
        }
    }

    // WMO weather interpretation codes, as Open-Meteo documents them.
    private static string Describe(int code) => code switch
    {
        0 => Strings.WeatherClear,
        1 or 2 => Strings.WeatherPartlyCloudy,
        3 => Strings.WeatherOvercast,
        45 or 48 => Strings.WeatherFog,
        >= 51 and <= 57 => Strings.WeatherDrizzle,
        >= 61 and <= 67 or >= 80 and <= 82 => Strings.WeatherRain,
        >= 71 and <= 77 or 85 or 86 => Strings.WeatherSnow,
        >= 95 => Strings.WeatherThunderstorm,
        _ => "",
    };

    // ---------------- drawing ----------------

    /// <summary>One line of text whose em is <paramref name="size"/> px, centred on <paramref name="x"/>, its cell's top at <paramref name="y"/>.</summary>
    private static void DrawText(IntPtr dc, string text, int x, int y, int size, uint rgb, bool bold = false)
    {
        var font = NativeMethods.CreateFontW(-size, 0, 0, 0, bold ? Win32Consts.FW_BOLD : Win32Consts.FW_NORMAL, 0, 0, 0,
            Win32Consts.DEFAULT_CHARSET, Win32Consts.OUT_TT_PRECIS, Win32Consts.CLIP_DEFAULT_PRECIS,
            Win32Consts.CLEARTYPE_QUALITY, Win32Consts.DEFAULT_PITCH | Win32Consts.FF_DONTCARE, "Segoe UI");
        var old = NativeMethods.SelectObject(dc, font);
        NativeMethods.SetTextColor(dc, (rgb & 0xFF) << 16 | (rgb & 0xFF00) | (rgb >> 16 & 0xFF));
        var rect = new RECT { Left = x - 4000, Top = y, Right = x + 4000, Bottom = y + size * 2 };
        NativeMethods.DrawTextW(dc, text, text.Length, ref rect,
            Win32Consts.DT_CENTER | Win32Consts.DT_SINGLELINE | Win32Consts.DT_NOPREFIX);
        NativeMethods.SelectObject(dc, old);
        NativeMethods.DeleteObject(font);
    }

    private static void Gradient(Span<uint> px, int w, int h, uint top, uint bottom)
    {
        for (int row = 0; row < h; row++) px.Slice(row * w, w).Fill(Mix(top, bottom, (double)row / h));
    }

    private static void Box(Span<uint> px, int w, int h, int x, int y, int bw, int bh, uint color, double alpha)
    {
        for (int row = Math.Max(0, y); row < Math.Min(h, y + bh); row++)
            for (int col = Math.Max(0, x); col < Math.Min(w, x + bw); col++)
                px[row * w + col] = Mix(px[row * w + col], color, alpha);
    }

    /// <summary>An antialiased disc.</summary>
    private static void Disc(Span<uint> px, int w, int h, double cx, double cy, double r, uint color, double alpha)
    {
        for (int row = Math.Max(0, (int)(cy - r - 1)); row <= Math.Min(h - 1, (int)(cy + r + 1)); row++)
            for (int col = Math.Max(0, (int)(cx - r - 1)); col <= Math.Min(w - 1, (int)(cx + r + 1)); col++)
            {
                var cover = Math.Clamp(r + 0.5 - Math.Sqrt((col + 0.5 - cx) * (col + 0.5 - cx) + (row + 0.5 - cy) * (row + 0.5 - cy)), 0, 1);
                if (cover > 0) px[row * w + col] = Mix(px[row * w + col], color, alpha * cover);
            }
    }

    private static void Stars(Span<uint> px, int w, int h, double s, int seed)
    {
        var rnd = new Random(seed);
        for (int i = 0; i < w * h / 4000; i++)
            Disc(px, w, h, rnd.NextDouble() * w, rnd.NextDouble() * h * 0.7, 0.6 * s, 0xFFFFFF, 0.3 + rnd.NextDouble() * 0.6);
    }

    /// <summary>The season's or holiday's particles, sparse, the same all day.</summary>
    private static void Scatter(Span<uint> px, int w, int h, double s, Particles kind, int seed)
    {
        if (kind == Particles.None) return;
        var rnd = new Random(seed + 1);
        uint[] leaves = [0xE8833A, 0xC8562A, 0xE0B040], confetti = [0xFFD54A, 0xFF6F91, 0x7FB8FF, 0x7CD67C];
        for (int i = 0; i < w * h / 14000; i++)
        {
            double x = rnd.NextDouble() * w, y = rnd.NextDouble() * h, size = (1 + rnd.NextDouble()) * s;
            switch (kind)
            {
                case Particles.Snow: Disc(px, w, h, x, y, size, 0xFFFFFF, 0.7); break;
                case Particles.Blossom: Disc(px, w, h, x, y, 1.4 * size, 0xF6C6DA, 0.75); break;
                case Particles.Leaves:
                    var leaf = leaves[i % leaves.Length];
                    Disc(px, w, h, x, y, 1.5 * size, leaf, 0.6);
                    break;
                case Particles.Fireflies:
                    y = h / 2 + y / 2;
                    Disc(px, w, h, x, y, 3 * size, 0xFFE678, 0.12);
                    Disc(px, w, h, x, y, 0.9 * size, 0xFFE678, 0.9);
                    break;
                case Particles.Embers:
                    Disc(px, w, h, x, y, 2.5 * size, 0xFF8C1A, 0.12);
                    Disc(px, w, h, x, y, 0.8 * size, 0xFF8C1A, 0.85);
                    break;
                case Particles.Confetti:
                    Box(px, w, h, (int)x, (int)y, (int)(2 * size), (int)(3.5 * size), confetti[i % confetti.Length], 0.8);
                    break;
            }
        }
    }

    /// <summary>A dotted half circle with the sun (or at night the moon) where it is along it.</summary>
    private static void SunArc(Span<uint> px, int w, int h, double s, int cx, int cy, int r, Theme theme)
    {
        for (var a = 0.0; a <= Math.PI + 1e-9; a += Math.PI / 40)
            Disc(px, w, h, cx - r * Math.Cos(a), cy - r * Math.Sin(a), 0.8 * s, 0xFFFFFF, 0.22);
        var at = Math.PI * theme.SunProgress;
        double sx = cx - r * Math.Cos(at), sy = cy - r * Math.Sin(at);
        var night = theme.Phase == DayPhase.Night;
        var color = night ? 0xE8ECF5u : 0xFFD27Au;
        Disc(px, w, h, sx, sy, 10 * s, color, 0.15);
        Disc(px, w, h, sx, sy, 5 * s, color, 1);
    }

    private static uint Mix(uint a, uint b, double t)
    {
        uint C(int shift) => (uint)Math.Round((a >> shift & 0xFF) * (1 - t) + (b >> shift & 0xFF) * t) << shift;
        return C(16) | C(8) | C(0);
    }
}
