using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure back/forward navigation-history model (issue #298): push/back/forward,
/// forward-truncation on a fresh push, the pinned root ("history never escapes above the root"), and
/// the bounded-depth cap. The Terminal.Gui wiring in <c>TodoApp</c>/<c>SingleTaskApp</c> (Alt+←/→,
/// Esc-as-back, the exit seam) is verified by build + reasoning per the repo's TUI rule; this locks
/// the decisions it delegates. "Both modes" (dashboard list root vs. single-task launch-task root) is
/// covered by parameterizing the root entry — the model treats the root as opaque data, not a mode.
/// </summary>
public sealed class NavigationHistoryTests
{
    // The two roots the acceptance criteria call out: the dashboard's list, and single-task mode's
    // launch task. The model behaves identically for either — that is the point of a root parameter.
    public static TheoryData<string> Roots => new() { "list", "task:abc123" };

    [Theory]
    [MemberData(nameof(Roots))]
    public void NewHistory_StartsAtRoot_WithNoBackOrForward(string root)
    {
        var history = new NavigationHistory<string>(root);

        Assert.Equal(root, history.Current);
        Assert.True(history.AtRoot);
        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Equal(1, history.Count);
        Assert.Equal(0, history.Index);
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void Push_AdvancesCurrent_AndEnablesBack(string root)
    {
        var history = new NavigationHistory<string>(root);

        history.Push("a");

        Assert.Equal("a", history.Current);
        Assert.False(history.AtRoot);
        Assert.True(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Equal(2, history.Count);
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void BackThenForward_RoundTripsThroughVisitedEntries(string root)
    {
        var history = new NavigationHistory<string>(root);
        history.Push("a");
        history.Push("b");

        Assert.True(history.TryBack(out var back1));
        Assert.Equal("a", back1);
        Assert.True(history.CanGoForward);

        Assert.True(history.TryBack(out var back2));
        Assert.Equal(root, back2);
        Assert.True(history.AtRoot);

        Assert.True(history.TryForward(out var fwd1));
        Assert.Equal("a", fwd1);
        Assert.True(history.TryForward(out var fwd2));
        Assert.Equal("b", fwd2);
        Assert.False(history.CanGoForward);
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void TryBack_AtRoot_ReturnsFalse_AndLeavesCurrentUnchanged(string root)
    {
        var history = new NavigationHistory<string>(root);

        Assert.False(history.TryBack(out var entry));
        Assert.Equal(root, entry);
        Assert.Equal(root, history.Current);
        Assert.True(history.AtRoot);
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void TryForward_WithNothingAhead_ReturnsFalse_AndLeavesCurrentUnchanged(string root)
    {
        var history = new NavigationHistory<string>(root);
        history.Push("a");

        Assert.False(history.TryForward(out var entry));
        Assert.Equal("a", entry);
        Assert.Equal("a", history.Current);
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void Push_AfterBack_TruncatesForwardStack(string root)
    {
        var history = new NavigationHistory<string>(root);
        history.Push("a");
        history.Push("b");
        history.TryBack(out _); // now at "a", with "b" ahead

        history.Push("c"); // a fresh navigation discards the forward entry "b"

        Assert.Equal("c", history.Current);
        Assert.False(history.CanGoForward);
        Assert.Equal(new[] { root, "a", "c" }, history.Entries);
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void Push_EvictsOldestNonRoot_WhenExceedingCap_KeepingRootPinned(string root)
    {
        var history = new NavigationHistory<string>(root, maxDepth: 3);

        history.Push("a");
        history.Push("b"); // full: [root, a, b]
        history.Push("c"); // over cap: evict oldest non-root "a" → [root, b, c]

        Assert.Equal(3, history.Count);
        Assert.Equal(new[] { root, "b", "c" }, history.Entries);
        Assert.Equal("c", history.Current);

        // The root is still reachable and remains the root — it is never the evicted entry.
        Assert.True(history.TryBack(out _)); // → b
        Assert.True(history.TryBack(out var atRoot)); // → root
        Assert.Equal(root, atRoot);
        Assert.True(history.AtRoot);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void Push_KeepsCurrentPointingAtNewEntry_AfterCapEviction()
    {
        var history = new NavigationHistory<int>(0, maxDepth: 2);

        history.Push(1); // [0, 1]
        history.Push(2); // evict → [0, 2]
        history.Push(3); // evict → [0, 3]

        Assert.Equal(3, history.Current);
        Assert.Equal(2, history.Count);
        Assert.Equal(1, history.Index);
        Assert.Equal(new[] { 0, 3 }, history.Entries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsMaxDepthBelowOne(int maxDepth)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new NavigationHistory<string>("root", maxDepth));

    [Fact]
    public void MaxDepthOfOne_PinsTheRoot_SoPushesCannotEscapeIt()
    {
        var history = new NavigationHistory<string>("root", maxDepth: 1);

        history.Push("a");

        // A cap of 1 leaves room for the pinned root alone: a push is retained momentarily, then the
        // over-cap eviction (oldest non-root) removes it again, so navigation can never leave the root.
        Assert.Equal(1, history.Count);
        Assert.Equal("root", history.Current);
        Assert.True(history.AtRoot);
        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
    }
}
