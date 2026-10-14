namespace LcdHub;

/// <summary>
/// Decides what one panel shows. Pure, so the rules live in one place and can be tested:
/// <list type="number">
/// <item>Monitors off → <see cref="Blank"/>; session locked → <see cref="Today"/>. Nothing
/// a producer sends shows to whoever walks by a locked machine.</item>
/// <item>Any producer alerting → rotate among the alerting ones only, over a pin or feed too.</item>
/// <item>Any toast up → rotate among the producers showing one, over a pin or feed too.</item>
/// <item>A pinned producer with a frame → it, for as long as it stays pinned.</item>
/// <item>A video or mirrored window → <see cref="Feed"/>.</item>
/// <item>Otherwise rotate among the producers offering a frame, plus Today when it's
/// in the rotation or there's nothing else.</item>
/// </list>
/// Whatever is showing stays for its whole turn (its own interval, or the shared one) unless
/// it drops out of the pool.
/// </summary>
internal static class Rotation
{
    // Producers name themselves, so these start with a character no sane name does.
    public const string Today = "\0today";
    public const string Feed = "\0feed";
    public const string Blank = "\0blank";

    /// <param name="Toast">It has a toast up on this panel.</param>
    public readonly record struct Offer(string Name, bool Alert, bool Toast = false);

    /// <param name="offers">Producers with a frame for this panel, in rotation order.</param>
    /// <param name="pinned">The producer kept on this panel, or null. Without a frame it doesn't count.</param>
    /// <param name="current">What the panel shows now, or null.</param>
    /// <param name="shownFor">How long <paramref name="current"/> has been up; <see cref="TimeSpan.MaxValue"/> for "its turn is over".</param>
    /// <param name="ownIntervals">Producers, by name, whose turn isn't <paramref name="interval"/>.</param>
    public static string Pick(IReadOnlyList<Offer> offers, string? pinned, bool todayInRotation, bool hasFeed, bool locked, bool displayOff,
        string? current, TimeSpan shownFor, TimeSpan interval, IReadOnlyDictionary<string, TimeSpan>? ownIntervals = null)
    {
        if (displayOff) return Blank;
        if (locked) return Today;

        var pool = new List<string>();
        foreach (var o in offers) if (o.Alert) pool.Add(o.Name);
        if (pool.Count == 0) foreach (var o in offers) if (o.Toast) pool.Add(o.Name);
        if (pool.Count == 0)
        {
            foreach (var o in offers) if (o.Name == pinned) return pinned;
            if (hasFeed) return Feed;
            foreach (var o in offers) pool.Add(o.Name);
            if (todayInRotation || pool.Count == 0) pool.Add(Today);
        }

        var at = current is null ? -1 : pool.IndexOf(current);
        if (at < 0) return pool[0];
        var turn = ownIntervals?.GetValueOrDefault(current!, interval) ?? interval;
        return shownFor < turn ? current! : pool[(at + 1) % pool.Count];
    }
}
