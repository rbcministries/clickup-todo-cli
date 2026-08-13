using ClickUpTodo.Configuration;
using Terminal.Gui.Input;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// The resolved, validated override tokens for the two app-wide launch gestures (#506, split-pane epic
/// #502) — <see cref="KeyAction.OpenInNewTab"/> (<c>Ctrl+Enter</c>) and
/// <see cref="KeyAction.OpenInSplitPane"/> (<c>Ctrl+Alt+Enter</c>). An immutable value threaded to the two
/// consumers that must agree — the <see cref="ClickUpTodo.Tui.KeybindingDispatcher"/> (via the
/// <see cref="Keybindings.Token(ScreenContext, KeyAction, LaunchChordOverrides)"/> overload) and the
/// contextual footer (via <see cref="HelpItemSets.WithConfiguredLaunchChords"/>) — so both resolve a
/// gesture the same way and can't drift. The central <see cref="Keybindings"/> table itself stays a pure
/// default lookup; the override sits <em>over</em> it, consulted ahead of the defaults.
/// <para>
/// Built from <see cref="LaunchChordSettings"/> by <see cref="FromConfig"/>, which parses each token and
/// <b>drops any that <see cref="Key.TryParse"/> can't parse</b> — the #506 load-time defense, so an
/// invalid persisted override degrades to the shipped default rather than faulting the app. Only these two
/// actions are overridable; <see cref="For"/> returns <c>null</c> for anything else, and the
/// <see cref="Keybindings"/> overload then falls back to the default token.
/// </para>
/// </summary>
public sealed class LaunchChordOverrides
{
    /// <summary>No overrides — every gesture resolves to its shipped default. The zero-config value the
    /// zero-arg dispatcher construction and every non-launch screen use.</summary>
    public static readonly LaunchChordOverrides None = new(null, null);

    private readonly string? _newTab;
    private readonly string? _splitPane;

    private LaunchChordOverrides(string? newTab, string? splitPane)
    {
        _newTab = newTab;
        _splitPane = splitPane;
    }

    /// <summary>
    /// Reads the persisted <paramref name="settings"/> into a resolved value, dropping any token that
    /// <see cref="Key.TryParse"/> rejects (load-time defense, #506) so a corrupt/hand-edited/older-or-newer
    /// override falls back to the default instead of crashing. A <c>null</c> settings object (or a null/blank
    /// field) yields <see cref="None"/>-equivalent behaviour for that gesture. Tokens are trimmed so a
    /// stray-whitespace config value still resolves.
    /// </summary>
    public static LaunchChordOverrides FromConfig(LaunchChordSettings? settings)
    {
        if (settings is null || settings.IsDefault)
            return None;
        return new LaunchChordOverrides(Sanitize(settings.NewTab), Sanitize(settings.SplitPane));
    }

    private static string? Sanitize(string? token)
    {
        var trimmed = token?.Trim();
        return !string.IsNullOrEmpty(trimmed) && Key.TryParse(trimmed, out _) ? trimmed : null;
    }

    /// <summary>The override token for <paramref name="action"/> when it is an overridable launch action
    /// with a valid override set, otherwise <c>null</c> (⇒ the caller uses the default). This is the single
    /// resolution rule both the dispatcher overload and the footer transform funnel through, so a gesture
    /// resolves identically on both sides.</summary>
    public string? For(KeyAction action) => action switch
    {
        KeyAction.OpenInNewTab => _newTab,
        KeyAction.OpenInSplitPane => _splitPane,
        _ => null,
    };

    /// <summary>True when at least one gesture is overridden — lets the footer transform skip its allocation
    /// on the (overwhelmingly common) no-override path.</summary>
    public bool HasAny => _newTab is not null || _splitPane is not null;
}
