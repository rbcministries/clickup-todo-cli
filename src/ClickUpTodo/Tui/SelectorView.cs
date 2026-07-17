using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// The static `Application` API (Invoke) is deprecated in Terminal.Gui 2.4 but remains the supported
// v2 pattern (see TodoApp.cs); silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

/// <summary>How a <see cref="SelectorView"/> applies an add/remove.</summary>
public enum SelectorMode
{
    /// <summary>New Task (#213): add/remove mutate an in-memory selection only; nothing is written to
    /// ClickUp until the host sends the whole set.</summary>
    CollectSelection = 0,

    /// <summary>Quick Updates (#158): add/remove apply to the server immediately (optimistic update,
    /// reconcile from the server-confirmed set, revert on failure) via the injected apply callback.</summary>
    ImmediateApply = 1,
}

/// <summary>
/// A reusable multi-select entity picker (#243, extracted from the assignee selector #212): a search
/// box over a type-ahead list, built <b>once</b> so both the assignee selector and the List selector
/// (#239) share one implementation instead of forking a near-duplicate. Empty search shows the current
/// selection (prefixed <c>✓</c>) topped up from a most-frequent pool; typing runs a debounced substring
/// match; picking a result adds that item, picking a <c>✓</c> row removes it. A seeded <em>locked</em>
/// entry (e.g. the current user on a New Task) can't be removed; a seeded <em>distinguished</em> entry
/// (e.g. the primary/home list — #240) renders with an injectable marker.
/// <para>
/// This is the CI-untestable Terminal.Gui glue — layout, input, the debounce timer, and off-thread
/// dispatch. All decisions (row assembly, the add/remove/locked toggle, the debounce coalescing gate)
/// live in the pure, unit-tested <see cref="SelectorModel"/>, mirroring the <c>DetailPaneView</c> split.
/// It's a single focusable composite (a <see cref="TextField"/> over a <see cref="ListView"/>), so it
/// doesn't reintroduce a second focusable pane on the main list (#3/#38); it's meant to be embedded in
/// a modal screen that owns Tab/Esc. Keyed on <b>string</b> ids so it fits ClickUp lists natively and
/// assignees via a thin adapter (see <see cref="AssigneeSelectorView"/>).
/// </para>
/// </summary>
public class SelectorView : View
{
    /// <summary>Default empty-state row budget: current selection plus top-frequent top-up (the list
    /// scrolls beyond this).</summary>
    public const int DefaultCapacity = 10;

    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(1);

    private readonly TextField _search;
    private readonly ListView _list;

    private readonly Func<string, ISet<string>, IReadOnlyList<SelectorItem>> _match;
    private readonly Func<int, ISet<string>, IReadOnlyList<SelectorItem>> _topFrequent;
    private readonly Func<ToggleKind, SelectorItem, CancellationToken, Task<IReadOnlyList<SelectorItem>>>? _applyAsync;
    private readonly SelectorMode _mode;
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly int _capacity;
    private readonly string _distinguishedSuffix;
    private readonly Func<SelectorItem, string> _lockedRemoveMessage;
    private readonly Func<Exception, string> _applyFailureMessage;

    private readonly List<SelectorItem> _selected = [];
    private readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _lockedIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _distinguishedIds = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    // The items behind the rows currently shown, parallel to the ListView source, so Enter maps a
    // highlighted row back to an item. Rebuilt on every render.
    private List<SelectorItem> _rowItems = [];

    // Monotonic keystroke stamp: bumped on every text change, captured when a debounce timer is armed,
    // and compared when it fires so only the latest keystroke's search runs. Touched on the UI thread.
    private long _searchStamp;
    private ITimer? _debounceTimer;

    // Monotonic immediate-apply generation: bumped per optimistic add/remove that fires a server write,
    // captured by that write, and re-checked on the UI thread before its reconcile/revert runs — so an
    // out-of-order older write can't clobber the state a newer one already established. Touched on the
    // UI thread only. (Deeper serialisation of overlapping writes is the host's job — see #158.)
    private long _applyGeneration;

    /// <param name="match">Substring match over the candidate pool, excluding the given ids.</param>
    /// <param name="topFrequent">Top-N most-frequent candidates excluding the given ids.</param>
    /// <param name="initialSelected">The currently-selected items (empty for a fresh selection).</param>
    /// <param name="lockedDefault">An entry that is pre-selected and cannot be removed (e.g. the current
    /// user on a New Task); null for no lock. Added to <paramref name="initialSelected"/>.</param>
    /// <param name="distinguishedDefault">An entry that is pre-selected and marked as the distinguished
    /// (primary/home) row (e.g. the create-target list — #240); null for none. Added to
    /// <paramref name="initialSelected"/>.</param>
    /// <param name="distinguishedSuffix">Marker appended after the distinguished row's name (e.g.
    /// <c>" (home)"</c>); empty for none (the assignee default — no visible marker).</param>
    /// <param name="mode">Whether add/remove apply immediately or are collected for a later save.</param>
    /// <param name="applyAsync">Required in <see cref="SelectorMode.ImmediateApply"/>: performs the
    /// server add/remove for an item and returns the server-confirmed set. The view owns the optimistic
    /// update and the revert-on-failure around it. Ignored in collect mode.</param>
    /// <param name="lockedRemoveMessage">Flash text when a locked entry's remove is refused.</param>
    /// <param name="applyFailureMessage">Flash text when an immediate-apply write fails.</param>
    /// <param name="timeProvider">Debounce clock (test seam); defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="debounce">Type-ahead debounce interval; defaults to ~1s.</param>
    /// <param name="capacity">Empty-state row budget (see <see cref="DefaultCapacity"/>).</param>
    public SelectorView(
        Func<string, ISet<string>, IReadOnlyList<SelectorItem>> match,
        Func<int, ISet<string>, IReadOnlyList<SelectorItem>> topFrequent,
        IReadOnlyList<SelectorItem>? initialSelected = null,
        SelectorItem? lockedDefault = null,
        SelectorItem? distinguishedDefault = null,
        string distinguishedSuffix = "",
        SelectorMode mode = SelectorMode.CollectSelection,
        Func<ToggleKind, SelectorItem, CancellationToken, Task<IReadOnlyList<SelectorItem>>>? applyAsync = null,
        Func<SelectorItem, string>? lockedRemoveMessage = null,
        Func<Exception, string>? applyFailureMessage = null,
        TimeProvider? timeProvider = null,
        TimeSpan? debounce = null,
        int capacity = DefaultCapacity)
    {
        _match = match;
        _topFrequent = topFrequent;
        _mode = mode;
        _applyAsync = applyAsync;
        _distinguishedSuffix = distinguishedSuffix;
        _lockedRemoveMessage = lockedRemoveMessage ?? (item => $"{item.Name} can't be removed.");
        _applyFailureMessage = applyFailureMessage ?? (ex => $"Couldn't apply change: {ex.Message}");
        _time = timeProvider ?? TimeProvider.System;
        _debounce = debounce ?? DefaultDebounce;
        _capacity = capacity < 1 ? DefaultCapacity : capacity;

        if (mode == SelectorMode.ImmediateApply && applyAsync is null)
            throw new ArgumentNullException(nameof(applyAsync), "ImmediateApply mode requires an apply callback.");

        // Seed selection: the current items, plus the locked / distinguished defaults (deduped, marked).
        foreach (var item in initialSelected ?? [])
            AddToSelection(item);
        if (lockedDefault is { } locked && !string.IsNullOrWhiteSpace(locked.Id))
        {
            AddToSelection(locked);
            _lockedIds.Add(locked.Id);
        }
        if (distinguishedDefault is { } distinguished && !string.IsNullOrWhiteSpace(distinguished.Id))
        {
            AddToSelection(distinguished);
            _distinguishedIds.Add(distinguished.Id);
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

    /// <summary>The current selection, in add order (the seeded defaults, then any others).</summary>
    public IReadOnlyList<SelectorItem> SelectedItems => _selected.ToList();

    /// <summary>The currently-selected distinguished entries — exactly the items rendered with the
    /// distinguished suffix (the primary/home marker), in selection order; empty when none is marked.
    /// The distinguished set is pruned on reconcile (a server-dropped entry stops being marked) and an
    /// item re-added by search is never re-marked, so this is the authoritative source for a
    /// specialization's primary/home accessor to stay in lockstep with what's on screen across
    /// add/remove/reconcile/revert (see <see cref="ListSelectorView.Primary"/>).</summary>
    protected IReadOnlyList<SelectorItem> DistinguishedSelection
        => _selected.Where(i => _distinguishedIds.Contains(i.Id)).ToList();

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
                // Enter in the search box is strictly add-only: pick the highlighted row so the user can
                // add without leaving the box, but only when there's an active query AND that row is an
                // addable (not already-selected) candidate. Picking a ✓ selected row would remove it —
                // and that row can still be the highlighted one under a non-blank query during the
                // type-ahead debounce window (rows haven't re-rendered yet), so gating on query text alone
                // would reintroduce #234. Removal stays an explicit ✓-row pick (Cursor Down into the list,
                // then Enter — see OnListKey). Enter is always Handled so it never falls through to a host
                // default action (e.g. New Task's IsDefault Save button).
                key.Handled = true;
                var row = _list.SelectedItem ?? 0;
                if (row >= 0 && row < _rowItems.Count
                    && SelectorModel.ShouldPickFromSearchBox(_search.Text, _rowItems[row].Id, _selectedIds))
                    Pick(row);
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
        if (rowIndex < 0 || rowIndex >= _rowItems.Count)
            return;
        var item = _rowItems[rowIndex];
        var decision = SelectorModel.Toggle(_selectedIds, _lockedIds, item.Id);
        switch (decision.Kind)
        {
            case ToggleKind.Added:
                ApplyAdd(item);
                break;
            case ToggleKind.Removed:
                ApplyRemove(item);
                break;
            case ToggleKind.LockedNoOp:
                Flash?.Invoke(this, _lockedRemoveMessage(item));
                break;
        }
    }

    // ── Add / remove ──────────────────────────────────────────────────────────

    private void ApplyAdd(SelectorItem item)
    {
        AddToSelection(item);
        // Clearing a non-empty search box already re-rendered the empty state; only render again when
        // the box was already empty (added from the empty-state top-frequent list).
        if (!ClearSearch())
            RenderEmptyState();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        if (_mode == SelectorMode.ImmediateApply)
            RunApply(ToggleKind.Added, item, revert: () => { RemoveFromSelection(item.Id); RenderCurrent(); });
    }

    private void ApplyRemove(SelectorItem item)
    {
        RemoveFromSelection(item.Id);
        RenderCurrent();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        if (_mode == SelectorMode.ImmediateApply)
            RunApply(ToggleKind.Removed, item, revert: () => { AddToSelection(item); RenderCurrent(); });
    }

    // Immediate-apply: perform the server write off the UI thread; on success reconcile the selection
    // from the server-confirmed set, on failure run the caller's revert and flash. The optimistic
    // update has already been applied by ApplyAdd/ApplyRemove.
    private void RunApply(ToggleKind kind, SelectorItem item, Action revert)
    {
        var token = _cts.Token;
        var generation = ++_applyGeneration;
        _ = Task.Run(async () =>
        {
            try
            {
                var confirmed = await _applyAsync!(kind, item, token).ConfigureAwait(false);
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
                    Flash?.Invoke(this, _applyFailureMessage(ex));
                });
            }
        });
    }

    // Replace the selection with the server-confirmed set, keeping any locked/distinguished marker
    // that's still present. Called on the UI thread after a successful immediate-apply write.
    private void Reconcile(IReadOnlyList<SelectorItem> confirmed)
    {
        _selected.Clear();
        _selectedIds.Clear();
        foreach (var item in confirmed)
            AddToSelection(item);
        // A locked / distinguished entry the server dropped is no longer meaningfully marked.
        _lockedIds.RemoveWhere(id => !_selectedIds.Contains(id));
        _distinguishedIds.RemoveWhere(id => !_selectedIds.Contains(id));
        RenderCurrent();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Merges already-existing selections into the current set without firing a server write — for a host
    /// that only learns the full selection <em>after</em> construction (the Quick Updates List pane's
    /// background membership enrich, #242). Purely additive: each not-already-selected item is added
    /// (blank id/name dropped) and the view re-rendered; nothing is removed and no
    /// <see cref="SelectorMode.ImmediateApply"/> write is issued (these items are already on the server).
    /// <para>
    /// No-ops once the user has interacted (an add/remove has bumped <see cref="_applyGeneration"/>), so a
    /// slow enrich can't resurrect an item the user just removed; a later user edit reconciles from the
    /// server truth regardless. Marks nothing as locked/distinguished — the seeded home marker set at
    /// construction is left untouched. Must run on the UI thread.
    /// </para>
    /// </summary>
    protected void AddExistingSelections(IReadOnlyList<SelectorItem> items)
    {
        if (_applyGeneration != 0)
            return;
        var added = false;
        foreach (var item in items ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name) || _selectedIds.Contains(item.Id))
                continue;
            AddToSelection(item);
            added = true;
        }
        if (!added)
            return;
        RenderCurrent();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddToSelection(SelectorItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name))
            return;
        if (_selectedIds.Add(item.Id))
            _selected.Add(item);
    }

    private void RemoveFromSelection(string id)
    {
        var index = _selected.FindIndex(a => string.Equals(a.Id, id, StringComparison.Ordinal));
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
            if (_cts.IsCancellationRequested || !SelectorModel.ShouldRunSearch(stamp, _searchStamp))
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
        var exclude = new HashSet<string>(_selectedIds, StringComparer.Ordinal);
        var token = _cts.Token;
        _ = Task.Run(() =>
        {
            IReadOnlyList<SelectorItem> matches;
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
                SetRows(SelectorModel.SearchResultRows(matches, _selectedIds));
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
            SetRows(SelectorModel.SearchResultRows(SafeMatch(query, _selectedIds), _selectedIds));
    }

    private void RenderEmptyState()
    {
        var top = SafeTopFrequent(_capacity, _selectedIds);
        SetRows(SelectorModel.EmptyStateRows(_selected, _lockedIds, _distinguishedIds, top, _capacity));
    }

    private void SetRows(IReadOnlyList<SelectorRow> rows)
    {
        var previous = _list.SelectedItem ?? 0;
        _rowItems = rows.Select(r => new SelectorItem(r.Id, r.Name)).ToList();
        _list.SetSource(new ObservableCollection<string>(rows.Select(r => SelectorModel.Format(r, _distinguishedSuffix))));
        // Keep the highlight where it was when the row still exists (e.g. after removing a ✓ row in the
        // empty state), clamping into range rather than snapping back to the top on every re-render.
        if (_rowItems.Count > 0)
            _list.SelectedItem = Math.Clamp(previous, 0, _rowItems.Count - 1);
    }

    private IReadOnlyList<SelectorItem> SafeTopFrequent(int n, ISet<string> exclude)
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

    private IReadOnlyList<SelectorItem> SafeMatch(string query, ISet<string> exclude)
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
