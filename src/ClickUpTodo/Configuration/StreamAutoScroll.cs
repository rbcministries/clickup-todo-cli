namespace ClickUpTodo.Configuration;

/// <summary>
/// Where the task detail view's Stream tab (#106) is scrolled to when it opens (#107). Expressed
/// relative to <em>content meaning</em> — the newest or the oldest entry — so it stays correct
/// regardless of the Stream's sort direction (Ctrl+PgUp/PgDn). The mapping to a viewport edge lives
/// in <see cref="ClickUpTodo.Tui.Screens.DetailScrollModel"/>, which also depends on the sort.
/// <para>
/// This is a user preference that #108 (S3) persists in <see cref="ViewSettings"/> and exposes in the
/// F2 dialog; it lives here (not in the Tui layer) so the persistence layer can reference it without
/// Configuration taking a dependency on Tui. Until #108 lands, the detail screen defaults it.
/// </para>
/// </summary>
public enum StreamAutoScroll
{
    /// <summary>Open scrolled to the newest entry (the most recent comment) — inbox-style.</summary>
    Newest,

    /// <summary>Open scrolled to the oldest entry (the Description / first comment).</summary>
    Oldest,
}
