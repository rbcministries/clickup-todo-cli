namespace ClickUpTodo.Configuration;

/// <summary>
/// A tab in the task detail view (#17/#106), used as the persisted "default tab" preference (#108).
/// Declared in the same order the detail screen lays its tabs out, so <see cref="DetailTabExtensions.ToTabIndex"/>
/// maps each value to its tab index. Lives in <c>Configuration</c> so the persistence layer and the
/// Tui screen share one definition.
/// </summary>
public enum DetailTab
{
    /// <summary>The Description + comments timeline (#106) — the app's out-of-the-box default.</summary>
    Stream,

    /// <summary>The task Description only.</summary>
    Description,

    /// <summary>The comments only.</summary>
    Comments,

    /// <summary>Other attributes (status/priority/custom fields).</summary>
    Other,

    /// <summary>The task's native ClickUp checklists (C, #456).</summary>
    Checklists,
}

/// <summary>Pure helpers over <see cref="DetailTab"/>, kept out of the Terminal.Gui layer so they're
/// unit-testable (the F2 cycle button and the detail screen's initial-tab selection share them).</summary>
public static class DetailTabExtensions
{
    /// <summary>The next tab in the F2 cycle, looping Stream → Description → Comments → Other →
    /// Checklists → Stream.</summary>
    public static DetailTab Next(this DetailTab tab) => tab switch
    {
        DetailTab.Stream => DetailTab.Description,
        DetailTab.Description => DetailTab.Comments,
        DetailTab.Comments => DetailTab.Other,
        DetailTab.Other => DetailTab.Checklists,
        _ => DetailTab.Stream,
    };

    /// <summary>
    /// The 0-based index of this tab in the detail screen's tab array (Stream, Description, Comments,
    /// Other, Checklists). Kept as an explicit map — rather than a raw cast — so a future reordering of
    /// either the enum or the screen's tabs is a deliberate, test-visible change. Checklists (#456) sits
    /// at index 4 in the base tab array, before the conditionally-appended Task Tree tab (#291), so its
    /// index is the same in both hosts.
    /// </summary>
    public static int ToTabIndex(this DetailTab tab) => tab switch
    {
        DetailTab.Stream => 0,
        DetailTab.Description => 1,
        DetailTab.Comments => 2,
        DetailTab.Other => 3,
        DetailTab.Checklists => 4,
        _ => 0,
    };
}
