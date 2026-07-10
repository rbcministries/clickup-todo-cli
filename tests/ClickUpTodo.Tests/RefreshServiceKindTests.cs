using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// The refresh loop must tell the fetch <em>why</em> it's running (#194): the session's first fetch is
/// <see cref="RefreshKind.Initial"/> and a user-triggered one is <see cref="RefreshKind.Manual"/> —
/// the two full-fetch guarantees. (The timeout→<see cref="RefreshKind.Poll"/> branch shares the same
/// code path but needs the ≥5s interval floor to elapse, so it isn't unit-tested here.)
/// </summary>
public sealed class RefreshServiceKindTests
{
    [Fact]
    public async Task FirstFetchIsInitial_ManualRefreshIsManual()
    {
        var kinds = new List<RefreshKind>();
        var fetches = 0;
        var twoFetches = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var service = new RefreshService(
            fetch: (kind, _) =>
            {
                lock (kinds)
                {
                    kinds.Add(kind);
                    if (++fetches == 2)
                        twoFetches.TrySetResult();
                }
                return Task.FromResult<IReadOnlyList<TaskItem>>([]);
            },
            intervalSeconds: 600, // far above the test's lifetime, so only the manual trigger fires
            onUpdate: _ => { },
            onError: _ => { });

        service.Start();
        service.RequestRefresh();
        await twoFetches.Task.WaitAsync(TimeSpan.FromSeconds(10));

        lock (kinds)
            Assert.Equal([RefreshKind.Initial, RefreshKind.Manual], kinds.Take(2));
    }
}
