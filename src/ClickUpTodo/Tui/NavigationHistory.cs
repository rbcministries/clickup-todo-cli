namespace ClickUpTodo.Tui;

/// <summary>
/// A pure, terminal-free back/forward navigation history — the mechanism behind browser-style
/// navigation in the TUI (issue #298, multi-tab epic #292 sub-issue 6). Kept free of Terminal.Gui so
/// the push/back/forward/root rules are unit-testable, like the repo's other pure glue
/// (<c>DetailTabNav</c>, <c>DetailScrollModel</c>, <c>DispatchPaneModel</c>, <c>HelpLine</c>). Both
/// hosts (<see cref="TodoApp"/> and <see cref="SingleTaskApp"/>) and the future detail→detail
/// navigation (#291) drive this one model, so there is a single back-stack, not several.
/// <para>
/// The entry at index 0 is the <b>root</b>: it is immutable, never evicted, and cannot be navigated
/// above (<see cref="TryBack"/> returns <c>false</c> at the root so the host can hand off to the
/// exit-confirmation seam, #299). Seeding the root with the dashboard's list vs. single-task mode's
/// launch task is all "per-launch-mode root" requires — the model needs no mode flag.
/// </para>
/// <para>
/// Semantics resolve #298's open questions: a fresh <see cref="Push"/> <b>truncates</b> the forward
/// entries (browser semantics), and depth is <b>bounded</b> by <see cref="MaxDepth"/> — when a push
/// would exceed it, the oldest <em>non-root</em> entry is evicted so the root stays pinned.
/// </para>
/// </summary>
public sealed class NavigationHistory<T>
{
    /// <summary>Default cap on history depth (#298's "bound it; record the cap"): far beyond any
    /// realistic hand-navigation depth while still bounding a pathological link-following loop.</summary>
    public const int DefaultMaxDepth = 50;

    private readonly List<T> _entries;
    private int _index;

    /// <summary>
    /// Creates a history rooted at <paramref name="root"/> (index 0, never evicted). The optional
    /// <paramref name="maxDepth"/> caps total depth (default <see cref="DefaultMaxDepth"/>); it must be
    /// at least 1 (room for the root alone).
    /// </summary>
    public NavigationHistory(T root, int maxDepth = DefaultMaxDepth)
    {
        if (maxDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, "maxDepth must be at least 1.");

        MaxDepth = maxDepth;
        _entries = [root];
        _index = 0;
    }

    /// <summary>The cap on history depth, including the root.</summary>
    public int MaxDepth { get; }

    /// <summary>The entry currently shown.</summary>
    public T Current => _entries[_index];

    /// <summary>True when the current entry is the root — back can't go higher (hands off to exit, #299).</summary>
    public bool AtRoot => _index == 0;

    /// <summary>True when there is an entry to go back to (i.e. not at the root).</summary>
    public bool CanGoBack => _index > 0;

    /// <summary>True when a prior <see cref="TryBack"/> left forward entries not yet truncated by a push.</summary>
    public bool CanGoForward => _index < _entries.Count - 1;

    /// <summary>The number of entries currently retained (root included).</summary>
    public int Count => _entries.Count;

    /// <summary>The current position in the history (0 = root).</summary>
    public int Index => _index;

    /// <summary>The retained entries, oldest (root) first — for host reconciliation and tests.</summary>
    public IReadOnlyList<T> Entries => _entries;

    /// <summary>
    /// Navigates to <paramref name="entry"/>: truncates any forward entries (browser semantics — a fresh
    /// navigation discards the forward stack), appends, and advances <see cref="Current"/> to it. Enforces
    /// <see cref="MaxDepth"/> by evicting the oldest non-root entry, so the root is never dropped.
    /// </summary>
    public void Push(T entry)
    {
        // Drop the forward stack: everything after the current position is no longer reachable.
        if (_index < _entries.Count - 1)
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);

        _entries.Add(entry);
        _index = _entries.Count - 1;

        // Enforce the cap by evicting the oldest non-root entry (index 1). The root (index 0) is pinned,
        // so "history never escapes above the root" holds even after eviction.
        while (_entries.Count > MaxDepth)
        {
            _entries.RemoveAt(1);
            _index--;
        }
    }

    /// <summary>
    /// Moves back one entry and reports the entry now current. Returns <c>false</c> at the root (the
    /// caller hands off to the exit seam, #299); <see cref="Current"/> is unchanged in that case.
    /// </summary>
    public bool TryBack(out T entry)
    {
        if (!CanGoBack)
        {
            entry = Current;
            return false;
        }

        _index--;
        entry = _entries[_index];
        return true;
    }

    /// <summary>
    /// Moves forward one entry (into entries left by a prior <see cref="TryBack"/>) and reports the entry
    /// now current. Returns <c>false</c> when there is nothing ahead; <see cref="Current"/> is unchanged.
    /// </summary>
    public bool TryForward(out T entry)
    {
        if (!CanGoForward)
        {
            entry = Current;
            return false;
        }

        _index++;
        entry = _entries[_index];
        return true;
    }
}
