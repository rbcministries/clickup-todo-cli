using System.Globalization;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure input-handling logic for the settings screen, factored out of the Terminal.Gui glue so it
/// can be unit-tested: parsing/clamping the refresh interval and deciding whether an excluded-status
/// entry can be added (non-blank and not a case-insensitive duplicate).
/// </summary>
public static class SettingsForm
{
    /// <summary>The allowed refresh-interval range, in seconds.</summary>
    public const int MinRefreshSeconds = 10;
    public const int MaxRefreshSeconds = 3600;

    /// <summary>
    /// Parses the refresh-interval field, clamping to [<see cref="MinRefreshSeconds"/>,
    /// <see cref="MaxRefreshSeconds"/>]. Falls back to <paramref name="fallback"/> when the text
    /// isn't a valid integer.
    /// </summary>
    public static int ParseRefreshSeconds(string? text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
            ? Math.Clamp(s, MinRefreshSeconds, MaxRefreshSeconds)
            : fallback;

    /// <summary>
    /// Whether <paramref name="candidate"/> can be added to the excluded-status list: it must be
    /// non-blank and not already present (case-insensitive). Compares against the trimmed candidate.
    /// </summary>
    public static bool CanAdd(IReadOnlyList<string> existing, string? candidate)
    {
        var trimmed = candidate?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;
        return !existing.Any(s => string.Equals(s, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses the agent-dispatch "extra args" field (#27) into a list of arguments, splitting on
    /// whitespace and dropping blanks. This keeps the settings UI simple; args that themselves
    /// contain spaces aren't expressible here (a rare need for the dispatch model flag / etc.).
    /// </summary>
    public static List<string> ParseExtraArgs(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Renders an extra-args list back to the space-joined text shown in the field.</summary>
    public static string FormatExtraArgs(IEnumerable<string> args)
        => string.Join(" ", args.Where(a => !string.IsNullOrWhiteSpace(a)));
}
