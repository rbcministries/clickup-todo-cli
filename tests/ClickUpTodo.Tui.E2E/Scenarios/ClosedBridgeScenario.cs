namespace ClickUpTodo.Tui.E2E;

using Handler = FakeClickUp.RouteHandler;

/// <summary>
/// #333 closed-task bridge-paint scenario. Two knobs the closed-bridge check pairs (and which therefore must
/// be one scenario — two team-tasks overrides would tie at tier 1):
/// <list type="bullet">
/// <item><c>E2E_WARM_CLOSED=1</c> — warm the closed-task cache before boot (a real
/// <c>PrefetchClosedTasksAsync</c>) so the F12→All bridge has a set to splice, and serve the completed task
/// with a <em>recent</em> <c>date_updated</c> so it survives ClosedTaskCache's 30-day age window.</item>
/// <item><c>E2E_STALL_CLOSED_MS=&lt;ms&gt;</c> — delay the <em>authoritative</em> <c>include_closed=true</c>
/// team-task refresh once armed, so the F12→All pre-refresh bridge frame is deterministically observable
/// before the superset lands.</item>
/// </list>
/// The stall is armed only after any warm prefetch has run (in <see cref="BeforeBootAsync"/>), so it hits the
/// authoritative refresh but never the pre-boot warm prefetch that seeds the bridge. The control leg (stall
/// only, no warm) still arms the stall, so it observes the closed row appearing only after the stall.
/// </summary>
internal sealed class ClosedBridgeScenario : IE2EScenario
{
    private volatile bool _armed;

    private static bool Warm => Environment.GetEnvironmentVariable("E2E_WARM_CLOSED") == "1";
    private static int StallMs =>
        int.TryParse(Environment.GetEnvironmentVariable("E2E_STALL_CLOSED_MS"), out var ms) ? ms : 0;

    public string Name => "closed-bridge";
    public bool IsActive => Warm || StallMs > 0;

    public async Task BeforeBootAsync(HarnessServices services)
    {
        if (Warm)
            await services.Tasks.PrefetchClosedTasksAsync();
        _armed = true;
    }

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend) =>
    [
        new(HttpMethod.Get, "team/{id}/task", async (_, _, query, ct) =>
        {
            if (StallMs > 0 && _armed && FakeClickUp.IncludeClosed(query))
                await Task.Delay(StallMs, ct);
            var closedDate = Warm
                ? DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds().ToString()
                : FakeClickUp.ClosedTaskDefaultDate;
            return FakeClickUp.Ok(backend.TasksJson(
                FakeClickUp.PageOf(query), backend.TaskCount, FakeClickUp.IncludeClosed(query), closedDate));
        }, 1),
    ];
}
