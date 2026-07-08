using System.Globalization;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure input-handling logic for the settings screen, factored out of the Terminal.Gui glue so it
/// can be unit-tested: parsing/clamping the refresh interval and parsing/formatting the
/// agent-dispatch extra-args field.
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

    // ── base working directory (#92) ────────────────────────────────────────────

    /// <summary>The folder name appended to home when no base working directory is set (⇒ <c>~/ClickUp-Tasks</c>).</summary>
    public const string DefaultWorkingDirectoryFolderName = "ClickUp-Tasks";

    /// <summary>
    /// Expands a user-entered path (#92): trims it, expands a leading <c>~</c> (alone) or
    /// <c>~/…</c> / <c>~\…</c> to <paramref name="homeDirectory"/>, and returns blank for
    /// blank input. Any other value is returned trimmed as-is (an already-absolute path passes
    /// through unchanged). Pure so it can be unit-tested; the wizard and F2 dialog call it at
    /// write time so an absolute path is stored.
    /// </summary>
    public static string ExpandHomePath(string? text, string homeDirectory)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
            return "";
        if (trimmed == "~")
            return homeDirectory;
        if (trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(homeDirectory, trimmed[2..]);
        return trimmed;
    }

    /// <summary>
    /// Resolves the stored base working directory to an absolute path at read time (#92): expands a
    /// leading <c>~</c> (so a hand-edited <c>config.json</c> value works), and falls back to
    /// <c>{home}/<see cref="DefaultWorkingDirectoryFolderName"/></c> when blank/absent. This is the
    /// single source of truth future consumers (#95 browser root, #98 task-derived parent) call.
    /// </summary>
    public static string ResolveDefaultWorkingDirectory(string? stored, string homeDirectory)
    {
        var expanded = ExpandHomePath(stored, homeDirectory);
        return expanded.Length == 0
            ? Path.Combine(homeDirectory, DefaultWorkingDirectoryFolderName)
            : expanded;
    }
}
