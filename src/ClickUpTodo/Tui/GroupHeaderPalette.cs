using System.Globalization;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tui;

/// <summary>
/// Chooses a background color (as a <c>#rrggbb</c> hex, so it flows through the same
/// <see cref="StatusBadgeColor"/> parse/contrast path as status badges) for each group header, by
/// field:
/// <list type="bullet">
/// <item><b>Status</b> — the group's ClickUp status color (same value as its tasks' badges).</item>
/// <item><b>List</b> — the list's ClickUp color chip; a stable generated hue when the list has none.</item>
/// <item><b>Priority</b> — the task's ClickUp priority color, or the canonical palette by level.</item>
/// <item><b>Dates</b> — no natural color, so the day-groups are spread across a rainbow gradient in
///   chronological order; the trailing "no date" bucket stays uncolored (neutral).</item>
/// </list>
/// Returns null for a group with no resolvable color (the caller renders a neutral bar). Pure and
/// Terminal.Gui-free so the color math is unit-testable without a driver.
/// </summary>
public static class GroupHeaderPalette
{
    /// <summary>Canonical ClickUp priority colors by level (1=Urgent…4=Low), the fallback when a task
    /// carries no explicit priority color.</summary>
    private static readonly IReadOnlyDictionary<int, string> CanonicalPriorityColors = new Dictionary<int, string>
    {
        [1] = "#f50000", // Urgent — red
        [2] = "#ffcc00", // High — yellow
        [3] = "#6fddff", // Normal — light blue
        [4] = "#d8d8d8", // Low — gray
    };

    // Rainbow sweep for date gradients: earliest group at 0° (red), latest near 280° (violet). A
    // partial sweep (not the full 360°) avoids red reappearing at both ends. Vivid but not neon, so
    // the WCAG contrast picker still lands a readable text color.
    private const double DateHueSpan = 280.0;
    private const double DateSaturation = 0.62;
    private const double DateLightness = 0.55;

    /// <summary>
    /// A header background hex per group in <paramref name="groups"/> order, or null where no color
    /// applies. <paramref name="listColors"/> maps a list id to its fetched ClickUp color (a null
    /// value = fetched-but-unset), consulted only when grouping by <see cref="TaskField.List"/>.
    /// </summary>
    public static IReadOnlyList<string?> Resolve(
        TaskField? field,
        IReadOnlyList<TaskGroup> groups,
        IReadOnlyDictionary<string, string?>? listColors = null)
    {
        if (field is not { } f)
            return groups.Select(_ => (string?)null).ToList();

        return f switch
        {
            TaskField.Status => groups.Select(g => First(g)?.StatusColor).ToList(),
            TaskField.List => groups.Select(g => ListColor(g, listColors)).ToList(),
            TaskField.Priority => groups.Select(PriorityColor).ToList(),
            TaskField.Created or TaskField.LastActivity or TaskField.Due => DateGradient(groups),
            _ => groups.Select(_ => (string?)null).ToList(),
        };
    }

    private static TaskItem? First(TaskGroup group) => group.Tasks.Count > 0 ? group.Tasks[0] : null;

    private static string? ListColor(TaskGroup group, IReadOnlyDictionary<string, string?>? listColors)
    {
        var listId = First(group)?.ListId;
        if (!string.IsNullOrWhiteSpace(listId) && listColors is not null
            && listColors.TryGetValue(listId, out var color) && !string.IsNullOrWhiteSpace(color))
            return color;
        // No fetched color (unset in ClickUp, or not resolved yet): derive a stable hue from the list
        // name so every list still reads as visually distinct.
        return string.IsNullOrWhiteSpace(group.Label) ? null : HueHex(HashHue(group.Label), 0.55, 0.55);
    }

    private static string? PriorityColor(TaskGroup group)
    {
        var task = First(group);
        if (task is null)
            return null;
        if (!string.IsNullOrWhiteSpace(task.PriorityColor))
            return task.PriorityColor;
        return task.PriorityLevel is { } level && CanonicalPriorityColors.TryGetValue(level, out var hex)
            ? hex
            : null;
    }

    /// <summary>
    /// Spreads the day-groups across the rainbow in chronological order. The groups arrive already
    /// ordered (earliest first, the "no date" bucket last); that trailing bucket is detected as a
    /// non-date label and left uncolored so it doesn't steal a slot on the gradient.
    /// </summary>
    private static IReadOnlyList<string?> DateGradient(IReadOnlyList<TaskGroup> groups)
    {
        var dated = groups
            .Select((g, i) => (i, isDate: DateOnly.TryParseExact(g.Label, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            .Where(x => x.isDate)
            .Select(x => x.i)
            .ToList();

        var result = new string?[groups.Count];
        for (var pos = 0; pos < dated.Count; pos++)
        {
            // Single dated group → mid-spectrum; otherwise evenly spaced across the sweep.
            var hue = dated.Count == 1 ? DateHueSpan / 2 : DateHueSpan * pos / (dated.Count - 1);
            result[dated[pos]] = HueHex(hue, DateSaturation, DateLightness);
        }
        return result;
    }

    /// <summary>A stable hue in [0,360) from a string, using FNV-1a so it doesn't vary per process
    /// (unlike <see cref="string.GetHashCode()"/>).</summary>
    private static double HashHue(string text)
    {
        uint hash = 2166136261;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return hash % 360;
    }

    /// <summary>Formats an HSL color (hue in degrees, s/l in [0,1]) as a <c>#rrggbb</c> hex string.</summary>
    private static string HueHex(double hue, double saturation, double lightness)
    {
        var (r, g, b) = HslToRgb(hue, saturation, lightness);
        return $"#{r:x2}{g:x2}{b:x2}";
    }

    /// <summary>Converts HSL (hue in degrees, s/l in [0,1]) to 8-bit sRGB channels.</summary>
    internal static (int R, int G, int B) HslToRgb(double hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var hp = ((hue % 360) + 360) % 360 / 60.0;
        var x = c * (1 - Math.Abs(hp % 2 - 1));
        var (r1, g1, b1) = hp switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        var m = lightness - c / 2;
        return (Channel(r1 + m), Channel(g1 + m), Channel(b1 + m));

        static int Channel(double v) => Math.Clamp((int)Math.Round(v * 255), 0, 255);
    }
}
