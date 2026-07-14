using System.Collections.ObjectModel;
using ClickUpTodo.ClickUp;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// The Mentions &amp; Comments feed screen (#109). Renders the live feed (#114): recent comments and
/// @-mentions across the user's assigned tasks, newest first, as rows in a single focusable
/// <see cref="ListView"/> backed by a <see cref="StatusBadgeListSource"/> — mentions carry a coloured
/// ` @ ` chip so they stand out. <c>F3</c> toggles the mentions-only filter (#113) in place; the feed
/// is loaded once (all entries stamped) so the toggle filters locally with no re-fetch. Built on the
/// shared screen-navigation seam (#38), not a nested <c>Dialog</c>/<c>Application.Run</c> loop, and
/// hosts exactly one focusable pane so the #3/#38 latency invariants hold.
/// <para>
/// Loading and error states live on the host's status line (the feed is fetched off the UI thread
/// before the screen is constructed, like <c>OpenDetail()</c>); this screen owns the <b>empty</b>
/// state — a placeholder when the (possibly filtered) feed has no rows.
/// </para>
/// </summary>
public sealed class NotificationsFeedScreen : Screen
{
    /// <summary>
    /// The mention-coverage prerequisite note (#126) shown in every empty state. Because ClickUp has no
    /// inbox API, the feed is built from comments on the user's <b>assigned</b> tasks, so a mention on a
    /// task they aren't assigned to only appears when a per-Space automation turns mentions into
    /// assignments — which the app can't create or verify. Baked into the placeholders below (rather than
    /// appended at render time) so the pure <see cref="EmptyMessage"/> surface keeps returning a single
    /// constant that already carries the guidance. Ends with the doc path.
    /// </summary>
    public const string MentionCoverageNote =
        "Note: @-mentions only appear here if your ClickUp Space runs the\n"
        + "mention→assignee automation. It's per-Space and not retroactive, and the app\n"
        + "can't set it up for you. Setup & caveats: docs/mention-assignee-automation.md";

    /// <summary>
    /// The empty-state copy shown when the feed has no comments at all. Kept as a constant so the copy
    /// is unit-testable without instantiating the Terminal.Gui view (the test suite never calls
    /// <c>Application.Init</c>), mirroring the repo's pure-surface testing pattern.
    /// </summary>
    public const string EmptyStatePlaceholder =
        "No mentions or comments to show.\n"
        + "\n"
        + "This feed lists recent comments and @-mentions across the tasks assigned to you,\n"
        + "newest first.\n"
        + "\n"
        + MentionCoverageNote + "\n"
        + "\n"
        + "Press Esc to return to your tasks.";

    /// <summary>The empty-state copy shown when the mentions-only filter is on but nothing mentions
    /// the current user (there are comments — they're just not mentions).</summary>
    public const string NoMentionsPlaceholder =
        "No @-mentions of you to show.\n"
        + "\n"
        + "You're seeing mentions only. Press F3 to show all recent comments,\n"
        + "or Esc to return to your tasks.\n"
        + "\n"
        + MentionCoverageNote;

    private IReadOnlyList<CommentItem> _feed;
    private readonly ListView _list;
    private readonly Label _emptyLabel;
    private bool _mentionsOnly;

    /// <summary>Whether the feed currently includes activity from completed (closed-type) tasks — the
    /// F12 toggle. Display-only here: the persisted flag and the re-fetch it needs are owned by the
    /// host (see <see cref="ToggleCompletedRequested"/>); this drives the title indicator.</summary>
    private bool _showCompleted;

    /// <summary>The rows currently displayed (the feed after the F3 filter), kept so Enter can map the
    /// selected <see cref="ListView"/> index back to its <see cref="CommentItem"/> exactly as shown.</summary>
    private IReadOnlyList<CommentItem> _rows = [];

    /// <summary>Raised when the user presses Enter on a feed row that is attributed to a task (#115).
    /// The payload is the row's <see cref="CommentItem.TaskId"/>; the host opens that task's detail
    /// stacked over the feed and Esc returns here with the selection intact.</summary>
    public event EventHandler<string>? OpenTaskRequested;

    // Auto-refresh cadence (seconds) — the feed's own, longer interval (FeedRefreshSeconds, #123),
    // independent of the dashboard list's RefreshSeconds because assembling the feed is far heavier.
    // Floored like RefreshService. The repeating timeout token is removed on dispose; null until
    // OnShown arms it.
    private readonly int _autoRefreshSeconds;
    private object? _autoRefreshToken;

    /// <summary>
    /// Raised when the feed wants fresh data — on F5 / Ctrl+R, or on the auto-refresh tick. The host
    /// re-fetches the feed off the UI thread and feeds it back via <see cref="UpdateFeed"/>; the
    /// mentions-only filter and (where possible) the selected row are preserved.
    /// </summary>
    public event EventHandler? RefreshRequested;

    /// <summary>
    /// Raised when the user presses F12 to toggle whether completed (closed-type) tasks' activity is
    /// included (mirrors the main list's F12). Unlike the F3 mentions filter — a local re-filter of the
    /// loaded feed — this changes <b>what is fetched</b>, so the host owns it: it flips and persists
    /// <see cref="AppConfig.FeedShowCompleted"/>, reflects the new state back via
    /// <see cref="SetShowCompleted"/>, and re-fetches the feed.
    /// </summary>
    public event EventHandler? ToggleCompletedRequested;

    /// <param name="feed">The already-fetched, mention-stamped feed (newest first).</param>
    /// <param name="autoRefreshSeconds">Background auto-refresh cadence — the feed's own
    /// <see cref="Configuration.AppConfig.FeedRefreshSeconds"/> (#123), independent of the task list.</param>
    /// <param name="mentionsOnly">Whether the mentions-only filter starts on.</param>
    /// <param name="showCompleted">Whether the feed starts including completed-task activity (F12).</param>
    public NotificationsFeedScreen(
        IReadOnlyList<CommentItem> feed, int autoRefreshSeconds, bool mentionsOnly = false, bool showCompleted = false)
    {
        _feed = feed;
        _autoRefreshSeconds = Math.Max(5, autoRefreshSeconds);
        _mentionsOnly = mentionsOnly;
        _showCompleted = showCompleted;

        // One focusable ListView fills the screen area (the shared footer #103 carries the shortcuts).
        // A single pane keeps the #3 latency model — no second focusable view.
        _list = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };

        // Overlays the (empty) list to show the placeholder when there are no rows; hidden otherwise.
        _emptyLabel = new Label { X = 1, Y = 0, Width = Dim.Fill(1), Height = Dim.Fill(1) };

        _list.KeyDown += OnKey;

        Add(_list);
        Add(_emptyLabel);

        RenderFeed();
    }

    private void OnKey(object? sender, Key key)
    {
        // Ctrl+E toggles back to the task list — the same key that opened the feed (List ↔ Feed nav).
        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.E)
        {
            key.Handled = true;
            Close();
            return;
        }

        // Ctrl+R is the (undisplayed) alias for the F5 refresh key. The bare F5 case is in the switch.
        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.R)
        {
            key.Handled = true;
            RequestRefresh();
            return;
        }

        switch (key.KeyCode)
        {
            case KeyCode.Enter:
                key.Handled = true;
                OpenSelectedTask();
                break;
            case KeyCode.F5:
                key.Handled = true;
                RequestRefresh();
                break;
            case KeyCode.F3:
                key.Handled = true;
                _mentionsOnly = !_mentionsOnly;
                RenderFeed();
                RequestFlash(_mentionsOnly ? "Mentions only" : "All comments");
                break;
            case KeyCode.F12:
                key.Handled = true;
                // Unlike F3 (a local re-filter), completed activity is never in the loaded feed when the
                // toggle is off — the closed tasks were never fetched — so the host must re-fetch. Ask it
                // to; it flips/persists the flag, calls back into SetShowCompleted, and refreshes.
                ToggleCompletedRequested?.Invoke(this, EventArgs.Empty);
                break;
            case KeyCode.F1:
                key.Handled = true;
                RequestHelp();
                break;
            case KeyCode.Esc:
                key.Handled = true;
                Close();
                break;
        }
    }

    /// <summary>F5 / Ctrl+R — flashes and asks the host to re-fetch the feed.</summary>
    private void RequestRefresh()
    {
        RequestFlash("Refreshing feed…");
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Swaps in a freshly-fetched feed (F5 / Ctrl+R or the auto-refresh tick) and re-renders. Must run
    /// on the UI thread. The mentions-only filter is preserved (it filters locally), and the selection
    /// follows the same <em>comment</em> across the swap — so a refresh that prepends newer comments
    /// (or the warm-cache instant-paint→live swap, #123) doesn't slide the cursor onto a different row.
    /// Falls back to the clamped prior index when the selected comment is gone.
    /// </summary>
    public void UpdateFeed(IReadOnlyList<CommentItem> feed)
    {
        var prevIndex = _list.SelectedItem;
        var selectedId = prevIndex is int p && p >= 0 && p < _rows.Count ? _rows[p].Id : null;
        _feed = feed;
        RenderFeed(); // updates _rows to the new (filtered) rows
        var target = ResolveSelection(_rows, selectedId, prevIndex);
        if (target >= 0)
            _list.SelectedItem = target;
    }

    /// <summary>
    /// The row index a swapped-in feed should select: the row whose <see cref="CommentItem.Id"/> matches
    /// <paramref name="selectedId"/> (so the selection follows the same comment across a refresh), else
    /// the clamped <paramref name="previousIndex"/> (keep the cursor position when the comment is gone),
    /// or <c>-1</c> for an empty feed. An empty/absent id never matches (empty-id comments are kept
    /// distinct by <see cref="FeedService.Aggregate"/>, so they must not collapse onto each other). Pure
    /// and unit-testable.
    /// </summary>
    internal static int ResolveSelection(IReadOnlyList<CommentItem> rows, string? selectedId, int? previousIndex)
    {
        if (rows.Count == 0)
            return -1;
        if (!string.IsNullOrEmpty(selectedId))
        {
            for (var i = 0; i < rows.Count; i++)
                if (string.Equals(rows[i].Id, selectedId, StringComparison.Ordinal))
                    return i;
        }
        return Math.Clamp(previousIndex ?? 0, 0, rows.Count - 1);
    }

    /// <summary>
    /// Reflects the new completed-inclusion state (F12) in the title after the host has flipped and
    /// persisted the flag. The row content follows from the host's subsequent re-fetch (which feeds
    /// back through <see cref="UpdateFeed"/>), so this only updates the indicator — no source rebuild,
    /// so the cursor isn't disturbed. Must run on the UI thread.
    /// </summary>
    public void SetShowCompleted(bool showCompleted)
    {
        _showCompleted = showCompleted;
        Title = TitleFor(_mentionsOnly, _showCompleted);
    }

    /// <summary>Rebuilds the list rows from the (filtered) feed and toggles the empty-state placeholder.
    /// Reassigns <c>_list.Source</c> (which disposes the previous source) — cheap; the feed is small
    /// and bounded (<see cref="FeedService.DefaultMaxEntries"/>).</summary>
    private void RenderFeed()
    {
        var rows = Filter(_feed, _mentionsOnly);
        _rows = rows;
        Title = TitleFor(_mentionsOnly, _showCompleted);

        var (text, badges, keys) = BuildRows(rows);
        _list.Source = new StatusBadgeListSource(text, badges, headerAttrs: null, searchKeys: keys);

        var empty = rows.Count == 0;
        _emptyLabel.Visible = empty;
        _emptyLabel.Text = empty ? "\n" + EmptyMessage(_mentionsOnly, _feed.Count > 0) : "";
    }

    /// <summary>Enter on a feed row (#115): opens the selected comment's task, or — when the selected
    /// comment carries no task id — flashes a note. A no-selection / empty feed is a no-op. The task is
    /// opened by raising <see cref="OpenTaskRequested"/>; the host stacks its detail over this screen.</summary>
    private void OpenSelectedTask()
    {
        var index = _list.SelectedItem ?? -1;
        if (index < 0 || index >= _rows.Count)
            return; // empty feed or no selection — nothing to open

        var taskId = SelectedTaskId(_rows, index);
        if (taskId is null)
            RequestFlash("This comment isn't linked to a task.");
        else
            OpenTaskRequested?.Invoke(this, taskId);
    }

    /// <summary>The task id of the row at <paramref name="index"/> in <paramref name="rows"/>, or null
    /// when the index is out of range or the row's <see cref="CommentItem.TaskId"/> is missing. Pure and
    /// unit-testable — the mapping Enter uses to decide which task (if any) to open.</summary>
    internal static string? SelectedTaskId(IReadOnlyList<CommentItem> rows, int index)
        => index >= 0 && index < rows.Count && !string.IsNullOrEmpty(rows[index].TaskId)
            ? rows[index].TaskId
            : null;

    /// <summary>The feed rows to show: all of it, or only the entries that mention the current user
    /// (#113). Pure and unit-testable.</summary>
    internal static IReadOnlyList<CommentItem> Filter(IReadOnlyList<CommentItem> feed, bool mentionsOnly)
        => mentionsOnly ? feed.Where(c => c.MentionsMe).ToList() : feed;

    /// <summary>The frame title for the given filter state: the mentions-only vs all-comments base,
    /// suffixed with <c>(+completed)</c> when completed-task activity is included (F12). Pure and
    /// unit-testable.</summary>
    internal static string TitleFor(bool mentionsOnly, bool showCompleted)
    {
        var baseTitle = mentionsOnly ? "Feed — mentions only" : "Feed — mentions & comments";
        return showCompleted ? baseTitle + " (+completed)" : baseTitle;
    }

    /// <summary>Which empty-state copy to show: the mentions-only placeholder when that filter is on
    /// and the feed does have (non-mention) comments, otherwise the no-comments-at-all placeholder.
    /// Pure and unit-testable.</summary>
    internal static string EmptyMessage(bool mentionsOnly, bool feedHasAnyComments)
        => mentionsOnly && feedHasAnyComments ? NoMentionsPlaceholder : EmptyStatePlaceholder;

    /// <summary>Builds the parallel arrays a <see cref="StatusBadgeListSource"/> consumes: the display
    /// text, the per-row badge spans (a mention row carries the amber ` @ ` chip; a plain row carries
    /// none), and the type-ahead search keys (author only). The mention colour is fixed (not a ClickUp
    /// field colour), built here in the view layer via <see cref="StatusBadgeListSource.TryCreate"/> —
    /// mirroring how <c>TodoApp.BuildRow</c> colours task badges from a hex string.</summary>
    internal static (ObservableCollection<string> Text,
                     IReadOnlyList<IReadOnlyList<StatusBadgeListSource.Badge>> Badges,
                     IReadOnlyList<string> Keys)
        BuildRows(IReadOnlyList<CommentItem> comments)
    {
        var text = new ObservableCollection<string>();
        var badges = new List<IReadOnlyList<StatusBadgeListSource.Badge>>(comments.Count);
        var keys = new List<string>(comments.Count);

        foreach (var comment in comments)
        {
            var row = FeedRowFormatter.Format(comment);
            text.Add(row.Text);

            var rowBadges = new List<StatusBadgeListSource.Badge>(1);
            if (StatusBadgeListSource.TryCreate(row.MentionStart, row.MentionLength, FeedRowFormatter.MentionBadgeColor) is { } chip)
                rowBadges.Add(chip);
            badges.Add(rowBadges);

            keys.Add(row.SearchKey);
        }

        return (text, badges, keys);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.NotificationsFeed;

    public override void OnShown()
    {
        _list.SetFocus();
        // Auto-refresh on the feed's own longer cadence (#123). The callback fires on the UI thread;
        // returning true keeps it repeating. Armed once here, torn down in Dispose.
        _autoRefreshToken ??= Application.AddTimeout(TimeSpan.FromSeconds(_autoRefreshSeconds), () =>
        {
            RefreshRequested?.Invoke(this, EventArgs.Empty);
            return true;
        });
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        // Stop the auto-refresh tick so it can't fire against a torn-down screen (#114 follow-up).
        if (disposing && _autoRefreshToken is { } token)
        {
            Application.RemoveTimeout(token);
            _autoRefreshToken = null;
        }
        base.Dispose(disposing);
    }
}
