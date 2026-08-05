namespace ClickUpTodo.Configuration;

/// <summary>
/// Persisted preferences for the task detail view (#108, S3 of the #102 epic): which tab opens first,
/// the Stream tab's default sort direction (#106), and where it auto-scrolls on open (#107). Edited in
/// the F2 Settings dialog.
/// <para>
/// Deliberately a separate group on <see cref="AppConfig"/> rather than part of
/// <see cref="ViewSettings"/> — like <see cref="AppConfig.BadgeDisplay"/>, these are detail-view
/// display preferences independent of the F3 filter/sort/group view (and its
/// <see cref="ViewSettings.IsDefault"/>). An absent <c>detailView</c> key in an older
/// <c>config.json</c> deserializes to this all-defaults instance, so it is backward-compatible with no
/// migration.
/// </para>
/// </summary>
public sealed class DetailViewSettings
{
    /// <summary>Which tab the detail view opens on. Default: <see cref="DetailTab.Stream"/> (#106).</summary>
    public DetailTab DefaultTab { get; set; } = DetailTab.Stream;

    /// <summary>The Stream tab's initial sort direction (#106). The on-screen Ctrl+PgUp/PgDn toggle
    /// overrides this for the current view only; it is not written back here. Default:
    /// <see cref="StreamSort.Ascending"/> (oldest/Description first).</summary>
    public StreamSort StreamSort { get; set; } = StreamSort.Ascending;

    /// <summary>Where the Stream tab is scrolled to on open (#107). Default:
    /// <see cref="StreamAutoScroll.Newest"/>.</summary>
    public StreamAutoScroll AutoScroll { get; set; } = StreamAutoScroll.Newest;

    /// <summary>Where a <c>Ctrl</c>+click on a <b>task</b> link in a detail pane goes (#320);
    /// <c>Ctrl+Shift</c>+click performs the other one. Default:
    /// <see cref="TaskLinkCtrlClickDestination.Browser"/> — byte-identical to the fixed behaviour
    /// #318 shipped.</summary>
    public TaskLinkCtrlClickDestination TaskLinkCtrlClick { get; set; } = TaskLinkCtrlClickDestination.Browser;
}
