using System.Collections.ObjectModel;
using ClickUpTodo.ClickUp;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// AssigneeSelectorView / SelectorMode / ToggleKind live in the parent ClickUpTodo.Tui
// namespace and are visible here without an extra using (this is the nested Screens namespace).

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// A full-window screen (#153/#156) that lets the user change a task's <b>Status</b>, <b>Priority</b>,
/// <b>Assignees</b> and <b>Lists</b> without leaving their place. It hosts four vertically-stacked,
/// focusable controls; <c>Tab</c>/<c>Shift+Tab</c> cycle focus Status → Priority → Assignees → Lists
/// (wrapping) and <c>Esc</c> exits from any pane.
/// <para>
/// Status and Priority are <b>deferred-commit</b> (#157): moving the highlight does nothing; pressing
/// <c>Enter</c> applies the highlighted value. Each pane marks its current effective value with a
/// leading <c>✓</c>. On Enter the screen moves the <c>✓</c> optimistically and raises
/// <see cref="StatusCommitted"/> / <see cref="PriorityCommitted"/>; the host performs the optimistic
/// row update + off-thread write + revert-on-failure and then calls <see cref="SetEffectiveStatus"/> /
/// <see cref="SetEffectivePriority"/> so the <c>✓</c> always reflects the server-confirmed value.
/// </para>
/// <para>
/// The Assignees pane (#158) is an embedded <see cref="AssigneeSelectorView"/> in
/// <see cref="SelectorMode.ImmediateApply"/> mode: a search box over a type-ahead list, drawing
/// its candidate pool from the assignee-frequency cache (#155). Add/remove apply to ClickUp immediately
/// (optimistic + revert-on-failure) via the injected apply callback; unlike Status/Priority there is no
/// <c>Enter</c> commit gate. The selector owns its own display + optimistic state; the host owns the
/// server write and reconciling the task's row.
/// </para>
/// <para>
/// The List pane (#242) is the same pattern for list membership: an embedded <see cref="ListSelectorView"/>
/// in <see cref="SelectorMode.ImmediateApply"/> mode over the list-frequency cache (#238), wired to the
/// task↔list membership writes (#237). It seeds with the task's current memberships — the home list
/// (marked <c>" (home)"</c>, removable) plus any additional "Tasks in Multiple Lists" locations —
/// and add/remove apply immediately. When the host only learns the additional locations after open (a
/// list-origin launch, where the snapshot task carries only the home list), it enriches them via
/// <see cref="SeedListMemberships"/>.
/// </para>
/// </summary>
public sealed class QuickUpdatesScreen : Screen
{
    private readonly ListView _statusList;
    private readonly ListView _priorityList;
    private readonly AssigneeSelectorView _assignees;
    private readonly ListSelectorView _lists;
    private readonly IReadOnlyList<StatusOption> _statuses;

    // The panes in focus (Tab) order — index maps to QuickUpdatesPane. The Assignees pane is a single
    // focusable composite (search box over a list), so it is one entry here, not two.
    private readonly View[] _panes;

    // The task's current effective values, mirrored so the ✓ marker (and the "unchanged" guard) track
    // the latest confirmed value. Seeded from the task; updated optimistically on Enter and reconciled
    // by the host from the server's returned value.
    private string? _effectiveStatus;
    private int? _effectivePriorityLevel;

    /// <summary>Raised when the user commits a (changed) status with Enter; the host applies it.</summary>
    public event Action<string>? StatusCommitted;

    /// <summary>Raised when the user commits a (changed) priority with Enter (<c>null</c> = clear);
    /// the host applies it.</summary>
    public event Action<int?>? PriorityCommitted;

    /// <param name="assigneeMatch">Case-insensitive substring match over the candidate pool excluding
    /// the given ids — i.e. <c>AssigneeFrequencyCache.Match</c> (#155).</param>
    /// <param name="assigneeTopFrequent">Top-N most-frequent candidates excluding the given ids — i.e.
    /// <c>AssigneeFrequencyCache.TopMostFrequent</c> (#155).</param>
    /// <param name="applyAssignee">Performs the immediate server add/remove for a person and returns the
    /// server-confirmed assignee set. Runs off the UI thread (the selector owns the optimistic update +
    /// revert around it).</param>
    /// <param name="homeList">The task's home list — pre-selected as the List pane's primary, marked
    /// <c>" (home)"</c> (removable); null when the task has no list (Quick Updates won't open in that
    /// case). See #242.</param>
    /// <param name="additionalLists">The task's additional "Tasks in Multiple Lists" locations known at
    /// construction (a detail-origin launch); empty for a list-origin launch, which enriches them later
    /// via <see cref="SeedListMemberships"/>.</param>
    /// <param name="listMatch">Case-insensitive substring match over the list candidate pool excluding
    /// the given ids — i.e. <c>ListFrequencyCache.Match</c> (#238).</param>
    /// <param name="listTopFrequent">Top-N most-frequent lists excluding the given ids — i.e.
    /// <c>ListFrequencyCache.TopMostFrequent</c> (#238).</param>
    /// <param name="applyList">Performs the immediate server add/remove of a list membership and returns
    /// the server-confirmed membership set. Runs off the UI thread (the selector owns the optimistic
    /// update + revert).</param>
    /// <param name="timeProvider">Debounce clock for the type-ahead search (test seam); defaults to
    /// <see cref="TimeProvider.System"/>.</param>
    /// <param name="assigneeDebounce">Type-ahead debounce interval; defaults to the selector's ~1s.</param>
    /// <param name="listDebounce">List type-ahead debounce interval; defaults to the selector's ~1s.</param>
    public QuickUpdatesScreen(
        string taskName,
        IReadOnlyList<StatusOption> statuses,
        string? currentStatus,
        int? currentPriorityLevel,
        IReadOnlyList<TaskAssignee> currentAssignees,
        Func<string, ISet<long>, IReadOnlyList<TaskAssignee>> assigneeMatch,
        Func<int, ISet<long>, IReadOnlyList<TaskAssignee>> assigneeTopFrequent,
        Func<ToggleKind, TaskAssignee, CancellationToken, Task<IReadOnlyList<TaskAssignee>>> applyAssignee,
        NamedEntity? homeList,
        IReadOnlyList<NamedEntity> additionalLists,
        Func<string, ISet<string>, IReadOnlyList<NamedEntity>> listMatch,
        Func<int, ISet<string>, IReadOnlyList<NamedEntity>> listTopFrequent,
        Func<ToggleKind, NamedEntity, CancellationToken, Task<IReadOnlyList<NamedEntity>>> applyList,
        TimeProvider? timeProvider = null,
        TimeSpan? assigneeDebounce = null,
        TimeSpan? listDebounce = null)
    {
        _statuses = statuses;
        _effectiveStatus = currentStatus;
        _effectivePriorityLevel = currentPriorityLevel;

        var title = taskName.Length > 40 ? taskName[..39] + "…" : taskName;
        Title = $"Quick Updates — {title}";

        // Four bordered sections, top-to-bottom in focus order. Priority is a fixed 5-row list; the
        // Assignees and Lists panes (a search box over a scrolling list each) get the taller bottom
        // frames; Status takes the remaining top space. The shared footer (#103) carries the shortcuts,
        // so no per-pane hint labels are needed. (Frame geometry is set further below.)
        _statusList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _statusList.SetSource(new ObservableCollection<string>(QuickUpdatesModel.StatusRows(statuses, _effectiveStatus)));
        var preselectedStatus = StatusPickerModel.PreselectedIndex(statuses, currentStatus);
        if (preselectedStatus >= 0)
            _statusList.SelectedItem = preselectedStatus;

        _priorityList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _priorityList.SetSource(new ObservableCollection<string>(QuickUpdatesModel.PriorityRows(_effectivePriorityLevel)));
        _priorityList.SelectedItem = QuickUpdatesModel.PriorityRowForLevel(currentPriorityLevel);

        _assignees = new AssigneeSelectorView(
            assigneeMatch,
            assigneeTopFrequent,
            initialSelected: currentAssignees,
            lockedDefault: null, // Quick Updates has no self-lock (that's the New Task rule, #213)
            mode: SelectorMode.ImmediateApply,
            applyAsync: applyAssignee,
            timeProvider: timeProvider,
            debounce: assigneeDebounce)
        { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        // Surface the selector's locked-no-op / write-failure messages on the shared status line.
        _assignees.Flash += (_, message) => RequestFlash(message);

        _lists = new ListSelectorView(
            listMatch,
            listTopFrequent,
            initialSelected: additionalLists,
            primary: homeList,
            mode: SelectorMode.ImmediateApply,
            applyAsync: applyList,
            timeProvider: timeProvider,
            debounce: listDebounce)
        { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _lists.Flash += (_, message) => RequestFlash(message);

        // Four bordered sections, top-to-bottom in focus order. Priority is a fixed 5-row list; the two
        // search-box panes (Assignees, Lists) get taller frames; Status takes the remaining top space.
        // The bottom stack reserves 24 rows (7 + 8 + 9); on the wide validation/typical terminal Status
        // still gets ample height above it. The shared footer (#103) carries the shortcuts.
        var statusFrame = new FrameView { Title = "Status", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(24) };
        statusFrame.Add(_statusList);

        var priorityFrame = new FrameView { Title = "Priority", X = 0, Y = Pos.AnchorEnd(24), Width = Dim.Fill(), Height = 7 };
        priorityFrame.Add(_priorityList);

        var assigneesFrame = new FrameView { Title = "Assignees", X = 0, Y = Pos.AnchorEnd(17), Width = Dim.Fill(), Height = 8 };
        assigneesFrame.Add(_assignees);

        var listsFrame = new FrameView { Title = "Lists", X = 0, Y = Pos.AnchorEnd(9), Width = Dim.Fill(), Height = 9 };
        listsFrame.Add(_lists);

        _panes = [_statusList, _priorityList, _assignees, _lists];
        foreach (var pane in _panes)
            pane.KeyDown += OnPaneKey;

        // Mouse click-to-apply (#288): a left-click on a Status/Priority row selects and commits it in
        // one gesture. The Assignees and Lists panes own their own click (SelectorView.OnListMouse).
        _statusList.MouseEvent += (_, e) => OnListClick(e, _statusList, _statuses.Count, CommitStatus);
        _priorityList.MouseEvent += (_, e) => OnListClick(e, _priorityList, QuickUpdatesModel.PriorityLabels.Count, CommitPriority);

        Add([statusFrame, priorityFrame, assigneesFrame, listsFrame]);
    }

    /// <summary>
    /// Merges the task's additional "Tasks in Multiple Lists" locations into the List pane after open —
    /// for a list-origin launch, where the snapshot task carries only the home list, so the host fetches
    /// the full membership in the background and enriches it here (#242). Additive and idempotent; a
    /// no-op once the user has started editing the pane. Must run on the UI thread.
    /// </summary>
    public void SeedListMemberships(IReadOnlyList<NamedEntity> additionalLists)
        => _lists.SeedExistingMemberships(additionalLists);

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.QuickUpdates;

    public override void OnShown() => _statusList.SetFocus();

    /// <summary>Reconciles the Status pane's <c>✓</c> to the host's confirmed (or reverted) value.</summary>
    public void SetEffectiveStatus(string? status)
    {
        _effectiveStatus = status;
        ReplaceRows(_statusList, QuickUpdatesModel.StatusRows(_statuses, _effectiveStatus));
    }

    /// <summary>Reconciles the Priority pane's <c>✓</c> to the host's confirmed (or reverted) value.</summary>
    public void SetEffectivePriority(int? level)
    {
        _effectivePriorityLevel = level;
        ReplaceRows(_priorityList, QuickUpdatesModel.PriorityRows(_effectivePriorityLevel));
    }

    /// <summary>
    /// Screen-wide keys shared by all four panes. Tab/Shift+Tab cycle focus (wrapping); Esc exits;
    /// F1 opens Help; Enter commits the highlighted Status/Priority value. ↑/↓ fall through so each
    /// Status/Priority ListView moves its own selection. The Assignees and Lists panes are single
    /// focusable <see cref="SelectorView"/> composites: each handles Enter (add/remove) and ↑/↓ (search
    /// box ↔ list) internally and marks them handled, so only their bubbled Tab/Shift+Tab/Esc/F1 reach
    /// here — the Enter-commit branch below stays keyed to the Status/Priority lists by identity.
    /// </summary>
    private void OnPaneKey(object? sender, Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.Tab:
                key.Handled = true;
                CyclePane(forward: !key.IsShift);
                break;
            case KeyCode.Enter:
                if (ReferenceEquals(sender, _statusList))
                {
                    CommitStatus();
                    key.Handled = true;
                }
                else if (ReferenceEquals(sender, _priorityList))
                {
                    CommitPriority();
                    key.Handled = true;
                }
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

    /// <summary>
    /// A left-click on a Status/Priority row is select-and-apply in one gesture (#288): resolve the
    /// clicked row (via the pure <see cref="QuickUpdatesModel.RowIndexAt"/>, using the list's scroll
    /// offset), move the highlight and focus there, then run the same commit path Enter uses — which
    /// keeps the "unchanged → flash, no-op" guard and the host's optimistic-apply + revert + ✓ reconcile.
    /// Only the single left-click is handled; a click on empty space below a short list resolves to
    /// <c>-1</c> and is left unhandled (native select behaviour intact), so it can never apply the
    /// nearest row.
    /// </summary>
    private static void OnListClick(Mouse e, ListView list, int rowCount, Action commit)
    {
        if (!e.Flags.HasFlag(MouseFlags.LeftButtonClicked) || e.Position is not { } pos)
            return;
        var row = QuickUpdatesModel.RowIndexAt(pos.Y, list.Viewport.Y, rowCount);
        if (row < 0)
            return;
        e.Handled = true;
        list.SetFocus();
        list.SelectedItem = row;
        commit();
    }

    private void CommitStatus()
    {
        if (_statusList.SelectedItem is not int i || i < 0 || i >= _statuses.Count)
            return;
        var name = _statuses[i].Name;
        if (string.Equals(name, _effectiveStatus, StringComparison.OrdinalIgnoreCase))
        {
            RequestFlash("Status unchanged.");
            return;
        }
        // Hand the write to the host; it moves the ✓ (via SetEffectiveStatus) only once it commits to
        // attempting the write, and reconciles us again from the server-confirmed value.
        StatusCommitted?.Invoke(name);
    }

    private void CommitPriority()
    {
        if (_priorityList.SelectedItem is not int i)
            return;
        var level = QuickUpdatesModel.PriorityLevelForRow(i);
        if (level == _effectivePriorityLevel)
        {
            RequestFlash("Priority unchanged.");
            return;
        }
        PriorityCommitted?.Invoke(level);
    }

    // Replaces a pane's rows in place, keeping the highlighted index so moving the ✓ doesn't jump the
    // cursor (SetSource resets SelectedItem to 0).
    private static void ReplaceRows(ListView list, IReadOnlyList<string> rows)
    {
        var selected = list.SelectedItem;
        list.SetSource(new ObservableCollection<string>(rows));
        if (selected is int i && i >= 0 && i < rows.Count)
            list.SelectedItem = i;
    }

    private void CyclePane(bool forward)
    {
        var current = Array.FindIndex(_panes, static p => p.HasFocus);
        if (current < 0)
            current = 0;
        var next = QuickUpdatesModel.Cycle((QuickUpdatesPane)current, forward);
        _panes[(int)next].SetFocus();
    }
}
