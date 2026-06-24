using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// Runs a background polling loop that fetches tasks every <c>intervalSeconds</c> and pushes the
/// result to <paramref name="onUpdate"/>. A manual refresh can be requested at any time, which
/// short-circuits the wait. Callbacks fire on a background thread — marshal UI work to the UI thread.
/// </summary>
public sealed class RefreshService(
    Func<CancellationToken, Task<IReadOnlyList<TaskItem>>> fetch,
    int intervalSeconds,
    Action<IReadOnlyList<TaskItem>> onUpdate,
    Action<Exception> onError) : IDisposable
{
    private readonly SemaphoreSlim _trigger = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public int IntervalSeconds { get; set; } = intervalSeconds;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Wake the loop immediately for a one-off refresh (e.g. the user pressed 'r').</summary>
    public void RequestRefresh()
    {
        try { _trigger.Release(); }
        catch (SemaphoreFullException) { /* a refresh is already queued */ }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tasks = await fetch(ct);
                onUpdate(tasks);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                onError(ex);
            }

            try
            {
                // Wait for the interval OR an explicit refresh request, whichever comes first.
                await _trigger.WaitAsync(TimeSpan.FromSeconds(Math.Max(5, IntervalSeconds)), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        _cts.Dispose();
        _trigger.Dispose();
    }
}
