using System.Diagnostics;

namespace ClickUpTodo.Tui;

/// <summary>
/// Shared Terminal.Gui teardown helper (#346). Terminal.Gui 2.4.10 can throw
/// <see cref="ArgumentOutOfRangeException"/>/<see cref="IndexOutOfRangeException"/> from
/// <c>View</c>/<c>Tabs</c> <c>Dispose</c> while tearing down a tabbed view's subviews (disposing a
/// child mutates the parent's subview list mid-iteration). That must never crash the app while it is
/// quitting or closing a screen, so both hosts and the screen-stack seam route their disposes through
/// here rather than each hand-rolling the same guard.
/// </summary>
internal static class TuiTeardown
{
    /// <summary>
    /// Disposes <paramref name="disposable"/>, swallowing only the known Terminal.Gui teardown bug
    /// (<see cref="ArgumentOutOfRangeException"/>/<see cref="IndexOutOfRangeException"/>) and logging it
    /// under <paramref name="label"/>. A null disposable is a no-op; any other exception propagates.
    /// </summary>
    public static void DisposeSwallowingTeardownBug(IDisposable? disposable, string label)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            Debug.WriteLine($"{label} dispose threw (Terminal.Gui teardown bug), ignoring: {ex}");
        }
    }
}
