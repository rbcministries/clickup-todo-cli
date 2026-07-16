using System.Collections.ObjectModel;
using ClickUpTodo.ClickUp;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// The static `Application` API (Invoke) is deprecated in Terminal.Gui 2.4 but remains the supported
// v2 pattern (see TodoApp.cs); silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

/// <summary>How an <see cref="AssigneeSelectorView"/> applies an add/remove.</summary>
public enum AssigneeSelectorMode
{
    /// <summary>New Task (#213): add/remove mutate an in-memory selection only; nothing is written to
    /// ClickUp until the host creates the task and sends the whole set.</summary>
    CollectSelection = 0,

    /// <summary>Quick Updates (#158): add/remove apply to the server immediately (optimistic update,
    /// reconcile from the server-confirmed set, revert on failure) via the injected apply callback.</summary>
    ImmediateApply = 1,
}

/// <summary>
/// A reusable assignee selector (#212): a search box over a type-ahead list, built <b>once</b> so the
/// New Task screen (#213) and the Quick Updates Assignees pane (#158) share one implementation. Empty
/// search shows the current assignees (prefixed <c>✓</c>) topped up from the most-frequent pool
/// (#155); typing runs a debounced substring match; picking a result adds that person, picking a
/// <c>✓</c> row removes them. A seeded <em>locked</em> default (e.g. the current user on a New Task)
/// can't be removed.
/// <para>
/// This is the CI-untestable Terminal.Gui glue — layout, input, the debounce timer, and off-thread
/// dispatch. All decisions (row assembly, the add/remove/locked toggle, the debounce coalescing gate)
/// live in the pure, unit-tested <see cref="AssigneeSelectorModel"/>, mirroring the
/// <c>DetailPaneView</c> split. It's a single focusable composite (a <see cref="TextField"/> over a
/// <see cref="ListView"/>), so it doesn't reintroduce a second focusable pane on the main list
/// (#3/#38); it's meant to be embedded in a modal screen that owns Tab/Esc.
/// </para>
/// </summary>
public sealed class AssigneeSelectorView : View
{
    /// <summary>Default empty-state row budget: current assignees plus top-frequent top-up (the list
    /// scrolls beyond this).</summary>
    public const int DefaultCapacity = 10;

    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(1);

    private readonly TextField _search;
    private readonly ListView _list;

    private readonly Func<string, ISet<long>, IReadOnlyList<TaskAssignee>> _match;
    private readonly Func<int, ISet<long>, IReadOnlyList<TaskAssignee>> _topFrequent;
    private readonly Func<ToggleKind, TaskAssignee, CancellationToken, Task<IReadOnlyList<TaskAssignee>>>? _applyAsync;
    private readonly AssigneeSelectorMode _mode;
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly int _capacity;

    private readonly List<TaskAssignee> _selected = [];
    private readonly HashSet<long> _selectedIds = [];
    private readonly HashSet<long> _lockedIds = [];
    private readonly CancellationTokenSource _cts = new();

    // The people behind the rows currently shown, parallel to the ListView source, so Enter maps a
    // highlighted row back to a person. Rebuilt on every render.
    private List<TaskAssignee> _rowPeople = [];

    // Monotonic keystroke stamp: bumped on every text change, captured when a debounce timer is armed,
    // and compared when it fires so only the latest keystroke's search runs. Touched on the UI thread.
    private long _searchStamp;
    private ITimer? _debounceTimer;

    // Monotonic immediate-apply generation: bumped per optimistic add/remove that fires a server write,
    // captured by that write, and re-checked on the UI thread before its reconcile/revert runs — so an
    // out-of-order older write can't clobber the state a newer one already established. Touched on the
    // UI thread only. (Deeper serialisation of overlapping writes is the host's job — see #158.)
    private long _applyGeneration;

    /// <param name="match">Substring match over the candidate pool, excluding the given ids — i.e.
    /// <c>AssigneeFrequencyCache.Match</c>.</param>
    /// <param name="topFrequent">Top-N most-frequent candidates excluding the given ids — i.e.
    /// <c>AssigneeFrequencyCache.TopMostFrequent</c>.</param>
    /// <param name="initialSelected">The task's current assignees (empty for a new task).</param>
    /// <param name="lockedDefault">A default assignee that is pre-selected and cannot be removed (e.g.
    /// the current user on a New Task); null for no lock. Added to <paramref name="initialSelected"/>.</param>
    /// <param name="mode">Whether add/remove apply immediately or are collected for a later save.</param>
    /// <param name="applyAsync">Required in <see cref="AssigneeSelectorMode.ImmediateApply"/>: performs
    /// the server add/remove for a person and returns the server-confirmed assignee set. The view owns
    /// the optimistic update and the revert-on-failure around it. Ignored in collect mode.</param>
    /// <param name="timeProvider">Debounce clock (test seam); defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="debounce">Type-ahead debounce interval; defaults to ~1s.</param>
    /// <param name="capacity">Empty-state row budget (see <see cref="DefaultCapacity"/>).</param>
    public AssigneeSelectorView(
        Func<string, ISet<long>, IReadOnlyList<TaskAssignee>> match,
        Func<int, ISet<long>, IReadOnlyList<TaskAssignee>> topFrequent,
        IReadOnlyList<TaskAssignee>? initialSelected = null,
        TaskAssignee? lockedDefault = null,
        AssigneeSelectorMode mode = AssigneeSelectorMode.CollectSelection,
        Func<ToggleKind, TaskAssignee, CancellationToken, Task<IReadOnlyList<TaskAssignee>>>? applyAsync = null,
        TimeProvider? timeProvider = null,
        TimeSpan? debounce = null,
        int capacity = DefaultCapacity)
    {
        _match = match;
        _topFrequent = topFrequent;
        _mode = mode;
        _applyAsync = applyAsync;
        _time = timeProvider ?? TimeProvider.System;
        _debounce = debounce ?? DefaultDebounce;
        _capacity = capacity < 1 ? DefaultCapacity : capacity;

        if (mode == AssigneeSelectorMode.ImmediateApply && applyAsync is null)
            throw new ArgumentNullException(nameof(applyAsync), "ImmediateApply mode requires an apply callback.");

        // Seed selection: the current assignees, plus the locked default (deduped, and marked locked).
        foreach (var person in initialSelected ?? [])
            AddToSelection(person);
        if (lockedDefault is { } locked && locked.Id > 0)
        {
            AddToSelection(locked);
            _lockedIds.Add(locked.Id);
        }

        CanFocus = true;

        _search = new TextField { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
        _list = new ListView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };
        // The composite is a single tab stop: the search box. The list stays focusable (Cursor Down
        // moves into it) but is not in the Tab order, so Tab/Shift+Tab pass straight through to the host
        // screen's pane cycle instead of toggling between the box and the list inside the component.
        _list.TabStop = TabBehavior.NoStop;

        _search.TextChanged += (_, _) => OnSearchChanged();
        _search.KeyDown += OnSearchKey;
        _list.KeyDown += OnListKey;

        Add(_search, _list);
        RenderEmptyState();
    }

    /// <summary>The current selection, in add order (the locked default, then any others).</summary>
    public IReadOnlyList<TaskAssignee> Selection => _selected.ToList();

    /// <summary>Raised whenever the selection changes (add/remove/reconcile), so the host can react.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised with a short user-facing message (a locked-remove no-op, or an immediate-apply
    /// write failure) for the host to surface in its status line.</summary>
    public event EventHandler<string>? Flash;

    // ── Input ───────────────────────────────────────────────────────────────

    private void OnSearchKey(object? sender, Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.CursorDown:
                key.Handled = true;
                _list.SetFocus();
                break;
            case KeyCode.Enter:
                // Add the highlighted result without leaving the box — but only while a search is
                // active. Search results are always unselected, addable rows, so Enter there only adds.
                // In the empty-search state row 0 is the first current assignee (a removable ✓ row) and
                // SelectedItem defaults to 0, so a stray Enter used to silently remove them (#234); it's
                // a no-op now, and removal stays an explicit ✓-row action reached by arrowing Down.
                if (AssigneeSelectorModel.ShouldPickFromSearchBox(_search.Text, _rowPeople.Count))
                {
                    key.Handled = true;
                    Pick(_list.SelectedItem ?? 0);
                }
                break;
        }
        // Tab / Shift+Tab / Esc / F1 fall through to the host screen (pane cycle, exit, help).
    }

    private void OnListKey(object? sender, Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.CursorUp when (_list.SelectedItem ?? 0) <= 0:
                key.Handled = true;
                _search.SetFocus();
                break;
            case KeyCode.Enter:
                key.Handled = true;
                Pick(_list.SelectedItem ?? -1);
                break;
        }
    }

    private void Pick(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rowPeople.Count)
            return;
        var person = _rowPeople[rowIndex];
        var decision = AssigneeSelectorModel.Toggle(_selectedIds, _lockedIds, person.Id);
        switch (decision.Kind)
        {
            case ToggleKind.Added:
                ApplyAdd(person);
                break;
            case ToggleKind.Removed:
                ApplyRemove(person);
                break;
            case ToggleKind.LockedNoOp:
                Flash?.Invoke(this, $"{person.Name} is the default assignee and can't be removed.");
                break;
        }
    }

    // ── Add / remove ──────────────────────────────────────────────────────────

    private void ApplyAdd(TaskAssignee person)
    {
        AddToSelection(person);
        // Clearing a non-empty search box already re-rendered the empty state; only render again when
        // the box was already empty (added from the empty-state top-frequent list).
        if (!ClearSearch())
            RenderEmptyState();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        if (_mode == AssigneeSelectorMode.ImmediateApply)
            RunApply(ToggleKind.Added, person, revert: () => { RemoveFromSelection(person.Id); RenderCurrent(); });
    }

    private void ApplyRemove(TaskAssignee person)
    {
        RemoveFromSelection(person.Id);
        RenderCurrent();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        if (_mode == AssigneeSelectorMode.ImmediateApply)
            RunApply(ToggleKind.Removed, person, revert: () => { AddToSelection(person); RenderCurrent(); });
    }

    // Immediate-apply: perform the server write off the UI thread; on success reconcile the selection
    // from the server-confirmed set, on failure run the caller's revert and flash. The optimistic
    // update has already been applied by ApplyAdd/ApplyRemove.
    private void RunApply(ToggleKind kind, TaskAssignee person, Action revert)
    {
        var token = _cts.Token;
        var generation = ++_applyGeneration;
        _ = Task.Run(async () =>
        {
            try
            {
                var confirmed = await _applyAsync!(kind, person, token).ConfigureAwait(false);
                Application.Invoke(() =>
                {
                    // Ignore a write that a newer add/remove has already superseded, so an out-of-order
                    // response can't reconcile stale server truth over the current optimistic state.
                    if (token.IsCancellationRequested || generation != _applyGeneration)
                        return;
                    Reconcile(confirmed);
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    if (token.IsCancellationRequested || generation != _applyGeneration)
                        return;
                    revert();
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                    Flash?.Invoke(this, $"Couldn't update assignees: {ex.Message}");
                });
            }
        });
    }

    // Replace the selection with the server-confirmed set, keeping any locked default that's still
    // present. Called on the UI thread after a successful immediate-apply write.
    private void Reconcile(IReadOnlyList<TaskAssignee> confirmed)
    {
        _selected.Clear();
        _selectedIds.Clear();
        foreach (var person in confirmed)
            AddToSelection(person);
        // A locked default that the server dropped is no longer meaningfully locked.
        _lockedIds.RemoveWhere(id => !_selectedIds.Contains(id));
        RenderCurrent();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddToSelection(TaskAssignee person)
    {
        if (person.Id <= 0 || string.IsNullOrWhiteSpace(person.Name))
            return;
        if (_selectedIds.Add(person.Id))
            _selected.Add(person);
    }

    private void RemoveFromSelection(long id)
    {
        var index = _selected.FindIndex(a => a.Id == id);
        if (index >= 0)
            _selected.RemoveAt(index);
        _selectedIds.Remove(id);
    }

    // ── Search / debounce ───────────────────────────────────────────────────

    private void OnSearchChanged()
    {
        var query = (_search.Text ?? "").Trim();
        // Bump the stamp so any pending timer becomes stale, then either paint the empty state now
        // (blank box) or arm the debounce for a match.
        _searchStamp++;
        if (query.Length == 0)
        {
            DisposeTimer();
            RenderEmptyState();
            return;
        }
        ArmDebounce(_searchStamp);
    }

    private void ArmDebounce(long stamp)
    {
        DisposeTimer();
        _debounceTimer = _time.CreateTimer(_ => OnDebounceFired(stamp), null, _debounce, System.Threading.Timeout.InfiniteTimeSpan);
    }

    private void OnDebounceFired(long stamp)
    {
        // The timer fires off the UI thread; marshal back and run the search only if this timer still
        // represents the latest keystroke (no newer input coalesced it away).
        Application.Invoke(() =>
        {
            if (_cts.IsCancellationRequested || !AssigneeSelectorModel.ShouldRunSearch(stamp, _searchStamp))
                return;
            RunSearch();
        });
    }

    private void RunSearch()
    {
        var query = (_search.Text ?? "").Trim();
        if (query.Length == 0)
        {
            RenderEmptyState();
            return;
        }
        var exclude = new HashSet<long>(_selectedIds);
        var token = _cts.Token;
        _ = Task.Run(() =>
        {
            IReadOnlyList<TaskAssignee> matches;
            try
            {
                matches = _match(query, exclude);
            }
            catch
            {
                matches = [];
            }
            Application.Invoke(() =>
            {
                if (token.IsCancellationRequested)
                    return;
                SetRows(AssigneeSelectorModel.SearchResultRows(matches, _selectedIds));
            });
        });
    }

    // Clears the search box. Returns true when it actually cleared a non-empty box — which synchronously
    // fires OnSearchChanged → RenderEmptyState — so the caller can skip a redundant render.
    private bool ClearSearch()
    {
        DisposeTimer();
        _searchStamp++;
        if (string.IsNullOrEmpty(_search.Text))
            return false;
        _search.Text = string.Empty;
        return true;
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    // Re-render whichever state matches the current search box (blank → empty state, else results).
    private void RenderCurrent()
    {
        var query = (_search.Text ?? "").Trim();
        if (query.Length == 0)
            RenderEmptyState();
        else
            SetRows(AssigneeSelectorModel.SearchResultRows(SafeMatch(query, _selectedIds), _selectedIds));
    }

    private void RenderEmptyState()
    {
        var top = SafeTopFrequent(_capacity, _selectedIds);
        SetRows(AssigneeSelectorModel.EmptyStateRows(_selected, _lockedIds, top, _capacity));
    }

    private void SetRows(IReadOnlyList<AssigneeRow> rows)
    {
        var previous = _list.SelectedItem ?? 0;
        _rowPeople = rows.Select(r => new TaskAssignee(r.Id, r.Name)).ToList();
        _list.SetSource(new ObservableCollection<string>(rows.Select(AssigneeSelectorModel.Format)));
        // Keep the highlight where it was when the row still exists (e.g. after removing a ✓ row in the
        // empty state), clamping into range rather than snapping back to the top on every re-render.
        if (_rowPeople.Count > 0)
            _list.SelectedItem = Math.Clamp(previous, 0, _rowPeople.Count - 1);
    }

    private IReadOnlyList<TaskAssignee> SafeTopFrequent(int n, ISet<long> exclude)
    {
        try
        {
            return _topFrequent(n, exclude);
        }
        catch
        {
            return [];
        }
    }

    private IReadOnlyList<TaskAssignee> SafeMatch(string query, ISet<long> exclude)
    {
        try
        {
            return _match(query, exclude);
        }
        catch
        {
            return [];
        }
    }

    private void DisposeTimer()
    {
        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            DisposeTimer();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
