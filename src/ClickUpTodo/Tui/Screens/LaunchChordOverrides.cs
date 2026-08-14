using System.Text;
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
    /// Reads the persisted <paramref name="settings"/> into a resolved value, <b>dropping any token that
    /// would not survive save-time validation</b> (load-time defense, #506) — one that <see cref="Key.TryParse"/>
    /// rejects, one reserved for the list type-ahead (<see cref="IsTypeAheadReserved"/>, #12), or one that
    /// <b>collides</b> with another live binding (<see cref="FindCollision"/>). Because <c>config.json</c> is a
    /// hand-editable interface, a parseable-but-colliding override (e.g. <c>"newTab": "Ctrl+U"</c>) must fall
    /// back to the default here rather than reach the dispatcher, which throws on a duplicate key at
    /// construction and would otherwise crash the app on startup. A <c>null</c> settings object (or a
    /// null/blank field) yields <see cref="None"/>-equivalent behaviour for that gesture. Tokens are trimmed so
    /// a stray-whitespace config value still resolves.
    /// <para>
    /// Collision resolution is two-pass and order-deterministic: the parse/type-ahead filter runs first, then
    /// the new-tab candidate is checked against the split candidate and the surviving new-tab feeds the split
    /// check — so a pair that collides only with <em>each other</em> degrades the new-tab chord to its default
    /// rather than dropping both or crashing.
    /// </para>
    /// </summary>
    public static LaunchChordOverrides FromConfig(LaunchChordSettings? settings)
    {
        if (settings is null || settings.IsDefault)
            return None;

        var newTab = Parseable(settings.NewTab);
        var splitPane = Parseable(settings.SplitPane);

        // Pass 2 — drop a candidate that collides with a live binding (the sibling's candidate included).
        if (newTab is not null && Collides(KeyAction.OpenInNewTab, newTab, new LaunchChordOverrides(newTab, splitPane)))
            newTab = null;
        if (splitPane is not null && Collides(KeyAction.OpenInSplitPane, splitPane, new LaunchChordOverrides(newTab, splitPane)))
            splitPane = null;

        return new LaunchChordOverrides(newTab, splitPane);
    }

    /// <summary>A token trimmed and kept only when it parses and isn't a bare type-ahead key (#12); the
    /// per-field half of the load-time defense (the cross-field collision half runs in <see cref="FromConfig"/>).</summary>
    private static string? Parseable(string? token)
    {
        var trimmed = token?.Trim();
        return !string.IsNullOrEmpty(trimmed) && Key.TryParse(trimmed, out var key) && !IsTypeAheadReserved(key)
            ? trimmed
            : null;
    }

    private static bool Collides(KeyAction action, string token, LaunchChordOverrides context)
        => Key.TryParse(token, out var proposed) && FindCollision(action, proposed, context) is not null;

    /// <summary>
    /// The first live binding a <paramref name="proposed"/> key collides with — its parsed <c>KeyCode</c>
    /// equalling another action's in any context that binds <paramref name="action"/> (a launch action is
    /// pinned to one key app-wide, so every such context is checked), with <paramref name="siblingContext"/>'s
    /// overrides applied so the other launch gesture is compared at its effective chord — or <c>null</c> when
    /// the key is free. The shared collision primitive both the load path (<see cref="FromConfig"/>) and the
    /// save path (<see cref="SettingsForm.ValidateLaunchChord"/>) resolve through, kept here (where
    /// Terminal.Gui is already referenced) so <see cref="Keybindings"/> stays a pure, parse-free lookup.
    /// </summary>
    public static (ScreenContext Context, KeyAction Action)? FindCollision(
        KeyAction action, Key proposed, LaunchChordOverrides siblingContext)
    {
        foreach (var context in Keybindings.ContextsBinding(action))
            foreach (var (otherAction, otherToken) in Keybindings.EffectiveBindingsFor(context, siblingContext))
            {
                if (otherAction == action)
                    continue;
                if (Key.TryParse(otherToken, out var other) && other.KeyCode == proposed.KeyCode)
                    return (context, otherAction);
            }

        return null;
    }

    /// <summary>Whether <paramref name="key"/> is a bare letter/digit the main list's <c>ListView</c>
    /// type-ahead consumes (#12): no <c>Ctrl</c>/<c>Alt</c> modifier and a letter-or-digit rune. Binding a
    /// launch gesture to one would hijack the type-ahead (the dispatch runs ahead of it), so both the load and
    /// save paths reject it — a launch chord must be a modified chord or a named/function key.</summary>
    public static bool IsTypeAheadReserved(Key key)
        => !key.IsCtrl && !key.IsAlt && Rune.IsLetterOrDigit(key.AsRune);

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
