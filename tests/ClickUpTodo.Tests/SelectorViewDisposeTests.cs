using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Idempotent-dispose regression for <see cref="SelectorView"/> (#483). Unlike the rest of the suite
/// — which never instantiates a Terminal.Gui view and never calls <c>Application.Init</c> because
/// rendering/input/driver behaviour is not CI-testable (see CLAUDE.md and <c>ExitConfirmTests</c>) —
/// this test deliberately constructs a real <see cref="SelectorView"/>. That is safe and in-bounds
/// here because it exercises only the <b>managed-resource teardown path</b> (the debounce
/// <see cref="System.Threading.CancellationTokenSource"/> and timer): construction and
/// <see cref="System.IDisposable.Dispose"/> need no driver, paint nothing, and process no keys.
/// Before the fix the second <c>Dispose()</c> called <c>_cts.Cancel()</c> on an already-disposed CTS
/// and threw <see cref="System.ObjectDisposedException"/>; the <c>_disposed</c> guard makes the
/// cleanup run exactly once so a double-dispose is a no-op.
/// </summary>
public sealed class SelectorViewDisposeTests
{
    private static SelectorView NewView() => new(
        match: (_, _) => System.Array.Empty<SelectorItem>(),
        topFrequent: (_, _) => System.Array.Empty<SelectorItem>());

    [Fact]
    public void Dispose_Twice_DoesNotThrow()
    {
        var view = NewView();

        view.Dispose();
        // The second pass models a still-attached parent re-disposing the child (the #472 call site).
        var second = Record.Exception(() => view.Dispose());

        Assert.Null(second);
    }

    [Fact]
    public void Dispose_Once_DoesNotThrow()
    {
        var view = NewView();

        var first = Record.Exception(() => view.Dispose());

        Assert.Null(first);
    }
}
