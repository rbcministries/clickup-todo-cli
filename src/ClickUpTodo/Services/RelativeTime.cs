namespace ClickUpTodo.Services;

/// <summary>
/// Formats an elapsed <see cref="TimeSpan"/> as a short, human "how long ago" label for the
/// cache-staleness markers (#124) — e.g. <c>"just now"</c>, <c>"3m ago"</c>, <c>"2h ago"</c>,
/// <c>"5d ago"</c>. Pure and allocation-light so it can be unit-tested and called on the render path.
/// </summary>
public static class RelativeTime
{
    /// <summary>
    /// A coarse "N units ago" label for <paramref name="age"/>. Sub-minute ages (including negatives
    /// from minor clock skew) render as <c>"just now"</c>; otherwise the largest whole unit wins —
    /// minutes under an hour, hours under a day, days beyond that. Deliberately coarse: the marker only
    /// needs to convey freshness at a glance, not a precise duration.
    /// </summary>
    public static string Format(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1))
            return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}
