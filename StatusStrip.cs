using System.Globalization;
using LcdHub.Client;

namespace LcdHub;

/// <summary>
/// Draws a status strip: the time (optional), then every producer's status items, each a mark in
/// its colour and its text. Items flow left to right and wrap, so the same drawing serves a thin
/// bar and a tall sidebar; what doesn't fit goes on further pages, which the hub turns every
/// rotate_seconds. The time starts every page.
/// </summary>
internal static class StatusStrip
{
    public readonly record struct Line(string Text, uint Color);

    public static uint LevelColor(LcdHubStatusLevel level, LcdHubStyle style) => level switch
    {
        LcdHubStatusLevel.Bad => 0xEF4444,
        LcdHubStatusLevel.Warn => 0xF59E0B,
        _ => style.Accent,
    };

    /// <param name="page">Any number; taken modulo <paramref name="pages"/>.</param>
    /// <param name="pages">How many pages the items take at this size.</param>
    public static uint[]? Paint(int w, int h, IReadOnlyList<Line> items, DateTime? clock, LcdHubStyle style, int page, out int pages)
    {
        var lineHeight = Math.Clamp(Math.Min(w, h), Layout.MinStatusSize, 44);
        int em = lineHeight / 2, pad = lineHeight / 3, mark = Math.Max(2, em / 4), gap = em;
        var lineWidth = w - 2 * pad;
        var linesPerPage = Math.Max(1, h / lineHeight);
        var dots = new List<(int X, int Y, uint Color)>();
        var count = 1;
        var pixels = Gdi.Render(w, h, dc =>
        {
            var background = Today.Mix(style.Background, style.Text, 0.06);
            var brush = NativeMethods.CreateSolidBrush(Bgr(background));
            var all = new RECT { Right = w, Bottom = h };
            NativeMethods.FillRect(dc, ref all, brush);
            NativeMethods.DeleteObject(brush);
            NativeMethods.SetBkMode(dc, Win32Consts.TRANSPARENT);
            NativeMethods.SetTextColor(dc, Bgr(style.Text));
            var regular = Font(em, bold: false);
            var bold = Font(em, bold: true);
            var old = NativeMethods.SelectObject(dc, bold);

            var time = clock?.ToString("HH:mm", CultureInfo.CurrentCulture);
            var indent = time is null ? 0 : Measure(dc, time) + gap;
            NativeMethods.SelectObject(dc, regular);
            // A cell is the mark, a mark's width of space, and the text.
            var widths = items.Select(i => Math.Min(lineWidth, 3 * mark + Measure(dc, i.Text))).ToList();
            var flowed = Flow(widths, lineWidth, linesPerPage, indent, gap);
            count = flowed.Count;
            var lines = flowed[page % count];

            var top = (h - lines.Count * lineHeight) / 2;
            if (time is not null)
            {
                NativeMethods.SelectObject(dc, bold);
                Text(dc, time, pad, top, pad + lineWidth, lineHeight);
                NativeMethods.SelectObject(dc, regular);
            }
            for (int l = 0; l < lines.Count; l++)
            {
                var (x, y) = (pad + (l == 0 ? indent : 0), top + l * lineHeight);
                foreach (var i in lines[l])
                {
                    dots.Add((x + mark, y + lineHeight / 2, items[i].Color));
                    Text(dc, items[i].Text, x + 3 * mark, y, Math.Min(x + widths[i], pad + lineWidth), lineHeight);
                    x += widths[i] + gap;
                }
            }
            NativeMethods.SelectObject(dc, old);
            NativeMethods.DeleteObject(regular);
            NativeMethods.DeleteObject(bold);
        });
        pages = count;
        if (pixels is null) return null;
        for (int i = 0; i < pixels.Length; i++) pixels[i] &= 0x00FFFFFF;
        foreach (var (x, y, color) in dots) Today.Disc(pixels, w, h, x, y, mark, color, 1);
        return pixels;
    }

    /// <summary>
    /// Lays cells of these widths out in lines of <paramref name="lineWidth"/>, and the lines in
    /// pages of <paramref name="linesPerPage"/>. Every page's first line starts at
    /// <paramref name="indent"/> (room for the time); a cell too wide for what is left of that
    /// line goes on the next, unless the page has no next line. Pure, so it can be tested.
    /// </summary>
    /// <returns>Pages of lines of cell indexes; at least one page with one line.</returns>
    public static List<List<List<int>>> Flow(IReadOnlyList<int> widths, int lineWidth, int linesPerPage, int indent, int gap)
    {
        var pages = new List<List<List<int>>>();
        var lines = new List<List<int>>();
        var line = new List<int>();
        var x = indent;
        for (int i = 0; i < widths.Count; i++)
        {
            // Twice at most: off a full page, then past the time on the next page's first line.
            while (x + widths[i] > lineWidth && (line.Count > 0 || (x > 0 && lines.Count + 1 < linesPerPage)))
            {
                lines.Add(line);
                line = [];
                x = 0;
                if (lines.Count == linesPerPage)
                {
                    pages.Add(lines);
                    lines = [];
                    x = indent;
                }
            }
            line.Add(i);
            x += widths[i] + gap;
        }
        lines.Add(line);
        pages.Add(lines);
        return pages;
    }

    private static IntPtr Font(int em, bool bold) =>
        NativeMethods.CreateFontW(-em, 0, 0, 0, bold ? Win32Consts.FW_BOLD : Win32Consts.FW_NORMAL, 0, 0, 0,
            Win32Consts.DEFAULT_CHARSET, Win32Consts.OUT_TT_PRECIS, Win32Consts.CLIP_DEFAULT_PRECIS,
            Win32Consts.CLEARTYPE_QUALITY, Win32Consts.DEFAULT_PITCH | Win32Consts.FF_DONTCARE, "Segoe UI");

    private static int Measure(IntPtr dc, string text)
    {
        var rect = new RECT();
        NativeMethods.DrawTextW(dc, text, text.Length, ref rect, Win32Consts.DT_CALCRECT | Win32Consts.DT_SINGLELINE | Win32Consts.DT_NOPREFIX);
        return rect.Right - rect.Left;
    }

    private static void Text(IntPtr dc, string text, int left, int top, int right, int height)
    {
        var rect = new RECT { Left = left, Top = top, Right = right, Bottom = top + height };
        NativeMethods.DrawTextW(dc, text, text.Length, ref rect,
            Win32Consts.DT_SINGLELINE | Win32Consts.DT_VCENTER | Win32Consts.DT_NOPREFIX | Win32Consts.DT_END_ELLIPSIS);
    }

    private static uint Bgr(uint rgb) => (rgb & 0xFF) << 16 | (rgb & 0xFF00) | (rgb >> 16 & 0xFF);
}
