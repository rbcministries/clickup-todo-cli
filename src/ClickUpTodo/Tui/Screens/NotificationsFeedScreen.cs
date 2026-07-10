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
        + "Press Esc to return to your tasks.";

    /// <summary>The empty-state copy shown when the mentions-only filter is on but nothing mentions
    /// the current user (there are comments — they're just not mentions).</summary>
    public const string NoMentionsPlaceholder =
        "No @-mentions of you to show.\n"
        + "\n"
        + "You're seeing mentions only. Press F3 to show all recent comments,\n"
        + "or Esc to return to your tasks.";

    private IReadOnlyList<CommentItem> _feed;
    private readonly ListView _list;
    private readonly Label _emptyLabel;
    private bool _mentionsOnly;

    // Auto-refresh cadence (seconds), tied to the list's RefreshSeconds so the feed and dashboard poll
    // in step (#114 follow-up). Floored like RefreshService. The repeating timeout token is removed on
    // dispose; null until OnShown arms it.
    private readonly int _autoRefreshSeconds;
    private object? _autoRefreshToken;

    /// <summary>
    /// Raised when the feed wants fresh data — on F5 / Ctrl+R, or on the auto-refresh tick. The host
    /// re-fetches the feed off the UI thread and feeds it back via <see cref="UpdateFeed"/>; the
    /// mentions-only filter and (where possible) the selected row are preserved.
    /// </summary>
    public event EventHandler? RefreshRequested;

    /// <param name="feed">The already-fetched, mention-stamped feed (newest first).</param>
    /// <param name="autoRefreshSeconds">Background auto-refresh cadence, tied to the list's RefreshSeconds.</param>
    /// <param name="mentionsOnly">Whether the mentions-only filter starts on.</param>
    public NotificationsFeedScreen(IReadOnlyList<CommentItem> feed, int autoRefreshSeconds, bool mentionsOnly = false)
    {
        _feed = feed;
        _autoRefreshSeconds = Math.Max(5, autoRefreshSeconds);
        _mentionsOnly = mentionsOnly;

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
    /// on the UI thread. The mentions-only filter is preserved (it filters locally), and the selected
    /// row index is kept where it still fits so a background refresh doesn't jump the cursor.
    /// </summary>
    public void UpdateFeed(IReadOnlyList<CommentItem> feed)
    {
        var selected = _list.SelectedItem;
        _feed = feed;
        RenderFeed();
        var rowCount = Filter(_feed, _mentionsOnly).Count;
        if (rowCount > 0 && selected is int i)
            _list.SelectedItem = Math.Clamp(i, 0, rowCount - 1);
    }

    /// <summary>Rebuilds the list rows from the (filtered) feed and toggles the empty-state placeholder.
    /// Reassigns <c>_list.Source</c> (which disposes the previous source) — cheap; the feed is small
    /// and bounded (<see cref="FeedService.DefaultMaxEntries"/>).</summary>
    private void RenderFeed()
    {
        var rows = Filter(_feed, _mentionsOnly);
        Title = _mentionsOnly ? "Feed — mentions only" : "Feed — mentions & comments";

        var (text, badges, keys) = BuildRows(rows);
        _list.Source = new StatusBadgeListSource(text, badges, headerAttrs: null, searchKeys: keys);

        var empty = rows.Count == 0;
        _emptyLabel.Visible = empty;
        _emptyLabel.Text = empty ? "\n" + EmptyMessage(_mentionsOnly, _feed.Count > 0) : "";
    }

    /// <summary>The feed rows to show: all of it, or only the entries that mention the current user
    /// (#113). Pure and unit-testable.</summary>
    internal static IReadOnlyList<CommentItem> Filter(IReadOnlyList<CommentItem> feed, bool mentionsOnly)
        => mentionsOnly ? feed.Where(c => c.MentionsMe).ToList() : feed;

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
        // Auto-refresh on the list's cadence (#114 follow-up). The callback fires on the UI thread;
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
