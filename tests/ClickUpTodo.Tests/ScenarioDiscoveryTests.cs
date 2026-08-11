using System.Net;
using ClickUpTodo.Tui.E2E;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the E2E harness's scenario discovery + selection (#489). The point of the design is that
/// scenarios are found by reflection (never a registry), self-activate from their own env var(s) or an
/// additive <c>E2E_SCENARIO</c> selector, and each one's routes coexist with the default backend's without
/// ambiguity — so those properties are what these pin. They assume the test process has no <c>E2E_*</c>
/// scenario vars set (true in CI), so nothing self-activates and selection is deterministic.
/// </summary>
public class ScenarioDiscoveryTests
{
    [Fact]
    public void Discover_FindsScenarios_ExcludesDefault_UniqueNonEmptyNames()
    {
        var all = ScenarioHost.Discover();

        Assert.NotEmpty(all);
        // DefaultScenario is constructed directly by the backend, never discovered — so it can't be selected
        // or deactivated and never appears in the fail-fast listing.
        Assert.DoesNotContain(all, s => s.Name == "default");
        Assert.All(all, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
        var names = all.Select(s => s.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        // A spread of the extracted scenarios is discovered (overrides, a host, an augment).
        Assert.Contains(all, s => s.Name == "foreign");
        Assert.Contains(all, s => s.Name == "tree");
        Assert.Contains(all, s => s.Name == "nudge");
        Assert.Contains(all, s => s.Name == "single-task");
        Assert.Contains(all, s => s.Name == "checklists");
    }

    [Fact]
    public void IsKnownSelector_TrueForDiscovered_FalseForGarbage()
    {
        var all = ScenarioHost.Discover();

        Assert.True(ScenarioHost.IsKnownSelector(all, "foreign"));
        // The fail-fast guard: a mistyped selector is rejected (Program then prints the names and exits 2).
        Assert.False(ScenarioHost.IsKnownSelector(all, "definitely-not-a-scenario"));
    }

    [Fact]
    public void Active_Selector_ActivatesNamedScenario_EvenWithLegacyVarUnset()
    {
        var all = ScenarioHost.Discover();

        // With no E2E_* env set nothing self-activates; the additive selector still turns its scenario on.
        var active = ScenarioHost.Active(all, "tree");

        Assert.Contains(active, s => s.Name == "tree");
    }

    [Fact]
    public void Active_NoSelector_NoEnv_IsEmpty()
    {
        var all = ScenarioHost.Discover();

        // Nothing self-activates without its env var, and there is no selector — the pure default backend.
        Assert.Empty(ScenarioHost.Active(all, null));
    }

    [Fact]
    public void EachScenario_LayeredOverDefault_BuildsRouteTableWithoutAmbiguity()
    {
        // The one-file property's safety net: each discovered scenario's routes register at tier 1 over the
        // default backend's tier-0 routes without an ambiguity throw — so a scenario that overrides an
        // endpoint cleanly shadows the default, and an added route never collides. A change that breaks this
        // fails here at `dotnet test`, not only at E2E harness boot. No handler is invoked.
        foreach (var scenario in ScenarioHost.Discover())
        {
            var ex = Record.Exception(() => new FakeClickUp(new HarnessContext { TaskCount = 1 }, [scenario]));
            Assert.True(ex is null, $"Scenario '{scenario.Name}' produced an ambiguous route table: {ex?.Message}");
        }
    }

    [Fact]
    public async Task ScenarioTaskGetOverride_HonoursNotFoundSentinels()
    {
        // A scenario that overrides GET task/{id} (here TreeScenario) must still 404 the two quick-open
        // sentinels — tmissing (#303) and hyphenless PROJ123 without custom_task_ids (#353) — that the
        // monolith checked ahead of its scenario branch. Passing the scenario directly registers its routes
        // regardless of its env-gated IsActive, so the override path is what answers here.
        using var client = new HttpClient(
            new FakeClickUp(new HarnessContext { TaskCount = 3 }, new IE2EScenario[] { new TreeScenario() }))
        {
            BaseAddress = new Uri("https://api.clickup.com/api/v2/"),
        };

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("task/tmissing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("task/PROJ123")).StatusCode);
        // custom_task_ids=true lifts the PROJ123 fallback; a real id resolves through the override.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("task/PROJ123?custom_task_ids=true")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("task/t0")).StatusCode);
    }
}
