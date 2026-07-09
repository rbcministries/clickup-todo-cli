namespace ClickUpTodo.Configuration;

/// <summary>
/// Sort direction for the detail view's Stream tab (#106): <see cref="Ascending"/> is oldest-first
/// (Description, then comments by date ascending); <see cref="Descending"/> is newest-first (comments
/// by date descending, then Description last).
/// <para>
/// Lives in <c>Configuration</c> (not the Tui layer that renders it) so it's the single source of
/// truth shared by the formatter/screen and the persisted detail-view default (#108,
/// <see cref="DetailViewSettings.StreamSort"/>) without Configuration depending on Tui.
/// </para>
/// </summary>
public enum StreamSort
{
    /// <summary>Oldest-first: Description block, then comments by date ascending.</summary>
    Ascending,

    /// <summary>Newest-first: comments by date descending, then the Description block last.</summary>
    Descending,
}

/// <summary>Pure helpers over <see cref="StreamSort"/>, kept out of the Terminal.Gui layer so they're
/// unit-testable (the F2 cycle button and the detail screen's Ctrl+PgUp/PgDn share them).</summary>
public static class StreamSortExtensions
{
    /// <summary>The other direction — cycles the two-value setting for the F2 button (#108).</summary>
    public static StreamSort Next(this StreamSort sort) =>
        sort == StreamSort.Ascending ? StreamSort.Descending : StreamSort.Ascending;
}
