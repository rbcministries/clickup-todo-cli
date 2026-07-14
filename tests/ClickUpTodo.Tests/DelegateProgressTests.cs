using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pins the small <see cref="DelegateProgress{T}"/> glue behind the streaming run screen (#187): it
/// invokes its handler inline on the reporting thread (so the caller can do its own UI-thread marshal),
/// forwards each reported value in order, and rejects a null handler.
/// </summary>
public sealed class DelegateProgressTests
{
    [Fact]
    public void Report_InvokesHandler_InlineOnTheCallingThread()
    {
        int? handlerThread = null;
        var progress = new DelegateProgress<string>(_ => handlerThread = Environment.CurrentManagedThreadId);

        progress.Report("x");

        Assert.Equal(Environment.CurrentManagedThreadId, handlerThread);
    }

    [Fact]
    public void Report_ForwardsEachValue_InOrder()
    {
        var seen = new List<string>();
        var progress = new DelegateProgress<string>(seen.Add);

        progress.Report("a");
        progress.Report("b");
        progress.Report("c");

        Assert.Equal(["a", "b", "c"], seen);
    }

    [Fact]
    public void Ctor_NullHandler_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new DelegateProgress<string>(null!));
}
