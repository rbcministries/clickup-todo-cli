using ClickUpTodo.Agent;
using ClickUpTodo.Configuration;
using ClickUpTodo.Focus;
using ClickUpTodo.Services;
using ClickUpTodo.Setup;

namespace ClickUpTodo.Tui.E2E;

/// <summary>
/// One E2E harness scenario, discovered by reflection (never a registry) and self-contained in a single
/// file — the crux of the E2E harness epic (#484 / #489). A scenario self-activates from its own legacy
/// env var(s) via <see cref="IsActive"/>, so the 39 checks' existing invocations (<c>E2E_TREE=1</c>, …)
/// keep working unchanged; <c>E2E_SCENARIO=&lt;Name&gt;</c> is an additive selector on top. Everything a
/// scenario needs lives here — config tweaks, seeded backend state, routes, pre-boot work, launcher/marker
/// inputs, even taking over which app boots — so adding one is a new file nobody else's diff touches. All
/// members but <see cref="Name"/> and <see cref="IsActive"/> default to no-ops, so a scenario implements
/// only the hooks it uses.
/// </summary>
internal interface IE2EScenario
{
    /// <summary>Stable handle; matches <c>E2E_SCENARIO</c> and names the scenario in fail-fast output.</summary>
    string Name { get; }

    /// <summary>Whether this scenario is on for the current run — reads its own legacy env var(s). The
    /// selector (<c>E2E_SCENARIO=Name</c>) activates it too; both paths are OR-ed by the orchestrator.</summary>
    bool IsActive { get; }

    /// <summary>Tweak the <see cref="AppConfig"/> the harness boots with (grouping, pinned tasks, detail
    /// prefs, subdomain, …). Runs for every active scenario before the backend and services are built.</summary>
    void Configure(AppConfig config) { }

    /// <summary>Seed shared, mutable backend state (the round-tripped assignee set, additional list
    /// memberships, the description) before any request is served — the state the default routes read.</summary>
    void SeedBackend(FakeClickUp backend) { }

    /// <summary>The routes this scenario contributes, registered at tier 1 so they <b>override</b> the
    /// always-on <see cref="DefaultScenario"/>'s tier-0 route for the same pattern (e.g. a scenario-specific
    /// <c>GET task/{id}</c>). Handlers may reuse <paramref name="backend"/>'s default builders and patch
    /// the result, so there is no duplicated response bytes to drift.</summary>
    IEnumerable<Route<FakeClickUp.RouteHandler>> Routes(FakeClickUp backend) => [];

    /// <summary>Pre-boot work that needs the constructed services (e.g. a real <c>PrefetchClosedTasksAsync</c>
    /// to warm the closed-task cache), run after services are built and before the app boots.</summary>
    Task BeforeBootAsync(HarnessServices services) => Task.CompletedTask;

    /// <summary>A browser launcher this scenario supplies (e.g. a recorder writing launched URLs to a file);
    /// the first active scenario's non-null launcher wins, else a no-op launcher is used.</summary>
    IBrowserLauncher? BrowserLauncher => null;

    /// <summary>A new-terminal-tab launcher this scenario supplies (e.g. a recorder); first non-null wins,
    /// else the app's real launcher is used.</summary>
    ITerminalLauncher? TabLauncher => null;

    /// <summary>A cross-process change-marker channel this scenario supplies (the two-instance nudge store);
    /// first non-null wins, else the facade's Null store is used. Its <see cref="HarnessMarkers.Disposable"/>
    /// is disposed on exit.</summary>
    HarnessMarkers? Markers => null;

    /// <summary>An alternate app host this scenario boots instead of the dashboard (single-task / feed);
    /// the first active scenario's non-null host wins, else the default <see cref="TodoApp"/> dashboard.</summary>
    IAppHost? Host => null;
}

/// <summary>The constructed services the app hosts and pre-boot hooks run against, assembled once by the
/// orchestrator so a scenario's <see cref="IE2EScenario.BeforeBootAsync"/> and <see cref="IAppHost"/> get
/// everything without a widening parameter list.</summary>
internal sealed class HarnessServices
{
    public required FakeClickUp Backend { get; init; }
    public required TaskService Tasks { get; init; }
    public required FeedService Feed { get; init; }
    public required AppConfig Config { get; init; }
    public required ConfigStore ConfigStore { get; init; }
    public required LocalFocusStore Focus { get; init; }
    public required TaskCache TaskCache { get; init; }
    public required FeedCache FeedCache { get; init; }
    public required AssigneeFrequencyCache Assignees { get; init; }
    public required ListFrequencyCache Lists { get; init; }
    public required IBrowserLauncher Browser { get; init; }
    public IChangeMarkerStore? ChangeMarkers { get; init; }
    public ITerminalLauncher? TabLauncher { get; init; }
}

/// <summary>An alternate top-level app a scenario boots instead of the dashboard — the harness equivalent
/// of <c>clickup-todo --task &lt;id&gt;</c> (single-task) or <c>--feed</c>. Runs the real app under the PTY
/// exactly as the dashboard host does.</summary>
internal interface IAppHost
{
    Task RunAsync(HarnessServices services);
}

/// <summary>A change-marker channel plus the disposable backing it (the two-instance nudge scenario's
/// LiteDB store), so the orchestrator can wire the store into both the client and the app and dispose it
/// on exit without the scenario leaking that lifetime.</summary>
internal sealed class HarnessMarkers
{
    public required IChangeMarkerStore Store { get; init; }
    public IDisposable? Disposable { get; init; }
}
