namespace LcdHub;

/// <summary>
/// Where a region sits on its panel. Producers see a rotating region as a panel of this id and
/// size; a <paramref name="Status"/> strip they don't see: the hub draws it.
/// </summary>
internal readonly record struct RegionRect(string Id, int X, int Y, int Width, int Height, bool Status = false);

/// <summary>
/// Cuts a panel into regions. Each layout entry takes a strip off one edge of what is left, in
/// order, and the rest keeps the panel's own id, so a panel's settings and producers' frames
/// carry on when a sidebar is added. Pure, so it can be tested.
/// </summary>
internal static class Layout
{
    /// <summary>The narrowest a region, or what is left of the panel, may be.</summary>
    public const int MinSize = 64;
    /// <summary>The narrowest a status strip may be: one line of text.</summary>
    public const int MinStatusSize = 16;
    public static readonly string[] Edges = ["left", "right", "top", "bottom"];
    public const string Rotation = "rotation", Status = "status";
    public static readonly string[] Kinds = [Rotation, Status];

    /// <param name="layout">Checked entries (<see cref="HubSettings"/> drops bad ones).</param>
    /// <param name="skipped">Entries left out because what remained would be under <see cref="MinSize"/>.</param>
    /// <returns>What is left first (the panel's id), then the strips in layout order.</returns>
    public static List<RegionRect> Carve(string panelId, int width, int height, IReadOnlyList<RegionSettings> layout,
        out List<RegionSettings> skipped)
    {
        skipped = [];
        int x = 0, y = 0, w = width, h = height;
        var strips = new List<RegionRect>();
        foreach (var e in layout)
        {
            var size = e.Size;
            if (size > (e.Edge is "left" or "right" ? w : h) - MinSize)
            {
                skipped.Add(e);
                continue;
            }
            var id = $"{panelId}/{e.Region}";
            var status = e.Kind == Status;
            switch (e.Edge)
            {
                case "left": strips.Add(new(id, x, y, size, h, status)); x += size; w -= size; break;
                case "right": strips.Add(new(id, x + w - size, y, size, h, status)); w -= size; break;
                case "top": strips.Add(new(id, x, y, w, size, status)); y += size; h -= size; break;
                default: strips.Add(new(id, x, y + h - size, w, size, status)); h -= size; break;
            }
        }
        return [new(panelId, x, y, w, h), .. strips];
    }
}
