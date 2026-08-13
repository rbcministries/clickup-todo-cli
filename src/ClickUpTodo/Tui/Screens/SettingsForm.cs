using System.Globalization;
using ClickUpTodo.Configuration;
using Terminal.Gui.Input;

namespace ClickUpTodo.Tui.Screens;

/// <summary>The outcome of validating a proposed launch-chord override (#506): either
/// <see cref="Ok"/>, or invalid with a user-facing <see cref="Error"/> naming the problem (an
/// unparseable token, or the binding it would collide with).</summary>
public readonly record struct LaunchChordValidation(bool IsValid, string? Error)
{
    /// <summary>The accepted result — a parseable, collision-free chord (or a blank field, which clears the
    /// override back to the default).</summary>
    public static readonly LaunchChordValidation Ok = new(true, null);

    /// <summary>An <paramref name="error"/> the settings dialog surfaces inline while keeping the default.</summary>
    public static LaunchChordValidation Invalid(string error) => new(false, error);
}

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

    /// <summary>The allowed feed look-back window (#244), in days. <c>0</c> = disabled (fetch the full
    /// assigned set); the upper bound caps a fat-fingered entry rather than reflecting any API limit.</summary>
    public const int MinLookbackDays = 0;
    public const int MaxLookbackDays = 3650;

    /// <summary>
    /// Parses the feed activity look-back field (#244), clamping to [<see cref="MinLookbackDays"/>,
    /// <see cref="MaxLookbackDays"/>] — <c>0</c> disables the window. Falls back to
    /// <paramref name="fallback"/> when the text isn't a valid integer.
    /// </summary>
    public static int ParseLookbackDays(string? text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
            ? Math.Clamp(d, MinLookbackDays, MaxLookbackDays)
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

    /// <summary>
    /// A one-line read-only summary of the configured dispatch providers (#547) for the F10 Dispatch
    /// section, beside the "Edit dispatch providers…" button: the provider count and the resolved default
    /// provider's name (matched on <paramref name="defaultName"/> Ordinal, else the first). An empty list
    /// reads as the single built-in <c>Claude</c> default the editor seeds on demand. Pure so it can be
    /// unit-tested.
    /// </summary>
    public static string DescribeProviders(IReadOnlyList<DispatchProvider> providers, string? defaultName)
    {
        if (providers.Count == 0)
            return $"1 provider · {AgentDispatchSettings.DefaultProviderDisplayName}";

        var def = providers.FirstOrDefault(p => string.Equals(p.Name, defaultName, StringComparison.Ordinal)) ?? providers[0];
        return providers.Count == 1
            ? $"1 provider · {def.Name}"
            : $"{providers.Count} providers · default {def.Name}";
    }

    // ── configurable launch chords (#506) ───────────────────────────────────────

    /// <summary>
    /// Validates a proposed launch-chord override (#506) at save time: a blank field is <b>Ok</b> (it clears
    /// the override, reverting the gesture to its shipped default); a non-blank token must be parseable by
    /// <see cref="Key.TryParse"/> and must not <b>collide</b> — its parsed <c>KeyCode</c> may not equal any
    /// <em>other</em> live binding's in any context that binds <paramref name="action"/> (a launch action is
    /// pinned to one key app-wide, so it is checked across every such context). The sibling launch gesture is
    /// compared at its <em>effective</em> token via <paramref name="current"/>, so rebinding one launch chord
    /// onto the other's configured chord is caught. On collision the message names the conflicting action and
    /// context; the caller keeps the default. Pure so it is unit-tested (mirrors the other <see cref="SettingsForm"/>
    /// parsers); the F10 dialog calls it before persisting.
    /// </summary>
    public static LaunchChordValidation ValidateLaunchChord(
        KeyAction action, string? proposedToken, LaunchChordOverrides current)
    {
        var token = proposedToken?.Trim() ?? "";
        if (token.Length == 0)
            return LaunchChordValidation.Ok;

        if (!Key.TryParse(token, out var proposed))
            return LaunchChordValidation.Invalid($"'{token}' isn't a recognised key combination.");

        foreach (var context in Keybindings.ContextsBinding(action))
            foreach (var (otherAction, otherToken) in Keybindings.EffectiveBindingsFor(context, current))
            {
                if (otherAction == action)
                    continue;
                if (Key.TryParse(otherToken, out var other) && other.KeyCode == proposed.KeyCode)
                    return LaunchChordValidation.Invalid(
                        $"{token} is already bound to {DescribeAction(otherAction)} on the {DescribeContext(context)}.");
            }

        return LaunchChordValidation.Ok;
    }

    /// <summary>A short human label for a <see cref="KeyAction"/> in a launch-chord collision message.</summary>
    private static string DescribeAction(KeyAction action) => action switch
    {
        KeyAction.OpenInNewTab => "open in new tab",
        KeyAction.OpenInSplitPane => "open in split pane",
        KeyAction.Open => "open",
        KeyAction.QuickOpen => "quick-open",
        KeyAction.QuickUpdate => "quick update",
        KeyAction.NewTask => "new task",
        KeyAction.RenameTask => "rename",
        KeyAction.Help => "help",
        KeyAction.Back => "back",
        _ => action.ToString(),
    };

    /// <summary>A short human label for a <see cref="ScreenContext"/> in a launch-chord collision message.</summary>
    private static string DescribeContext(ScreenContext context) => context switch
    {
        ScreenContext.MainList => "task list",
        ScreenContext.QuickOpen => "quick-open surface",
        _ => context.ToString(),
    };

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
