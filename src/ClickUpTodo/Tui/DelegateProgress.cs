namespace ClickUpTodo.Tui;

/// <summary>
/// A minimal <see cref="IProgress{T}"/> that invokes a delegate <b>inline</b> on the thread that calls
/// <see cref="Report"/> — unlike <see cref="System.Progress{T}"/>, which posts to a captured
/// <see cref="System.Threading.SynchronizationContext"/>. The background dispatch runner reports on its
/// own thread and the caller marshals onto the Terminal.Gui UI thread itself (via
/// <c>Application.Invoke</c>), so the extra context hop of <c>Progress&lt;T&gt;</c> is unwanted here.
/// </summary>
internal sealed class DelegateProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly Action<T> _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public void Report(T value) => _handler(value);
}
