namespace ClickUpTodo.Tui.E2E;

/// <summary>Reflection discovery and selection of <see cref="IE2EScenario"/>s — the "discovery, not
/// registration" core of #489, factored out of <c>Program.cs</c> so the selection rules (legacy-var +
/// selector activation, unknown-name detection) are unit-testable without booting the app under a PTY.</summary>
internal static class ScenarioHost
{
    /// <summary>Every concrete <see cref="IE2EScenario"/> in the harness assembly except the always-on
    /// <see cref="DefaultScenario"/> (which the backend constructs directly). No central list exists — the set
    /// is whatever files declare an <see cref="IE2EScenario"/>, so adding one is a new file and nothing else.</summary>
    public static List<IE2EScenario> Discover() =>
        typeof(ScenarioHost).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface
                        && typeof(IE2EScenario).IsAssignableFrom(t) && t != typeof(DefaultScenario))
            .Select(t => (IE2EScenario)Activator.CreateInstance(t)!)
            .ToList();

    /// <summary>Whether <paramref name="selector"/> names a discovered scenario — the guard behind the
    /// fail-fast for a mistyped <c>E2E_SCENARIO</c>.</summary>
    public static bool IsKnownSelector(IReadOnlyList<IE2EScenario> all, string selector)
        => all.Any(s => s.Name == selector);

    /// <summary>The active set for a run: a scenario is on when its own legacy env var(s) say so
    /// (<see cref="IE2EScenario.IsActive"/>) or the additive <paramref name="selector"/> names it.</summary>
    public static List<IE2EScenario> Active(IReadOnlyList<IE2EScenario> all, string? selector)
        => all.Where(s => s.IsActive || s.Name == selector).ToList();
}
