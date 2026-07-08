namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// One shortcut on the contextual help footer (#103): a <paramref name="Key"/> (a key or key-combo,
/// e.g. <c>Ctrl+B</c>, <c>↑/↓</c>) and the <paramref name="Label"/> for the action it triggers.
/// </summary>
public readonly record struct HelpItem(string Key, string Label);

/// <summary>
/// Pure model + formatter for the single contextual help footer (#103, part of #102). The window owns
/// one bottom-row help line; each screen (and the main list) declares an ordered set of
/// <see cref="HelpItem"/>s, and the host renders the active context's set on that shared line —
/// replacing, never stacking. Kept free of Terminal.Gui so the shortcut sets and the
/// "which items for which context" selection are unit-testable (mirrors <c>SettingsForm</c> /
/// <c>StatusPickerModel</c>). Responsive truncation of an over-long line is the follow-up #H2 (#104).
/// </summary>
public static class HelpLine
{
    /// <summary>Renders items as <c>"key label · key label · …"</c> (empty for an empty set).</summary>
    public static string Format(IReadOnlyList<HelpItem> items)
        => string.Join(" · ", items.Select(i => $"{i.Key} {i.Label}"));

    /// <summary>
    /// The selection rule: the active screen's items when a screen is open (and it declares any),
    /// otherwise the list's items. A screen that declares an empty set falls back to the list's set
    /// rather than showing a blank footer.
    /// </summary>
    public static IReadOnlyList<HelpItem> ForActiveScreen(
        IReadOnlyList<HelpItem>? screenItems, IReadOnlyList<HelpItem> fallback)
        => screenItems is { Count: > 0 } ? screenItems : fallback;
}

/// <summary>
/// The canonical, ordered shortcut sets for each context. Centralised (rather than hand-rolled per
/// screen) so the footer is consistent and testable; each screen exposes its set via
/// <c>Screen.HelpItems</c> and the host renders the list's set (<see cref="MainList"/>) when no screen
/// is open. Every screen set ends with an Esc/close item and (except Help itself) offers F1 → Help.
/// </summary>
public static class HelpItemSets
{
    /// <summary>The main task list. <see cref="HelpLine.Format"/> of this reproduces the pre-#103
    /// help line byte-for-byte, so the default (list-active) footer is unchanged.</summary>
    public static readonly IReadOnlyList<HelpItem> MainList =
    [
        new("↑/↓", "move"),
        new("→|", "next section"),
        new("␣", "status"),
        new("↩", "detail"),
        new("Ctrl+B", "🌐"),
        new("Ctrl+P", "📌"),
        new("Ctrl+R", "↻"),
        new("F1", "help"),
        new("F2", "⚙"),
        new("F3", "filter/sort/group"),
        new("F4", "subtasks"),
        new("F5", "feed"),
        new("→/←", "expand/collapse"),
        new("Ctrl+→/←", "all"),
        new("Ctrl+Q", "quit"),
        new("type", "to search"),
    ];

    /// <summary>The task detail view (adds F1, which the detail screen previously did not handle).</summary>
    public static readonly IReadOnlyList<HelpItem> Detail =
    [
        new("Tab", "switch tab"),
        new("↑/↓ PgUp/PgDn", "scroll"),
        new("A", "dispatch to Claude"),
        new("Ctrl+B", "browser"),
        new("F1", "help"),
        new("Esc", "back"),
    ];

    /// <summary>The settings screen (F2).</summary>
    public static readonly IReadOnlyList<HelpItem> Settings =
    [
        new("Tab", "moves"),
        new("Space", "cycles buttons"),
        new("F1", "help"),
        new("Esc", "cancels"),
    ];

    /// <summary>The filter · sort · group screen (F3).</summary>
    public static readonly IReadOnlyList<HelpItem> FilterSortGroup =
    [
        new("Tab", "moves"),
        new("Enter", "in Value adds"),
        new("Del", "removes selected filter"),
        new("F1", "help"),
        new("Esc", "cancels"),
    ];

    /// <summary>The status picker (Space).</summary>
    public static readonly IReadOnlyList<HelpItem> StatusPicker =
    [
        new("↑/↓", "move"),
        new("Enter", "select"),
        new("F1", "help"),
        new("Esc", "cancel"),
    ];

    /// <summary>The mentions &amp; comments feed screen (F5, #110). Scaffold set — scroll / open-task
    /// items arrive with the data-bearing follow-ups (#114/#115).</summary>
    public static readonly IReadOnlyList<HelpItem> NotificationsFeed =
    [
        new("F1", "help"),
        new("Esc", "back"),
    ];

    /// <summary>The help screen itself (no F1 — it is the help).</summary>
    public static readonly IReadOnlyList<HelpItem> Help =
    [
        new("Esc/Enter", "close"),
    ];
}
