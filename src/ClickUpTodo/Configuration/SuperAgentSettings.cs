namespace ClickUpTodo.Configuration;

/// <summary>
/// User-facing configuration for the Super Agents feature (#490), persisted in <c>config.json</c> under
/// <c>superAgents</c>. Today it carries only the manual agent <see cref="Agents">seed</see> — the pinned
/// <c>name → negative id</c> fallback that populates <see cref="Services.AgentDirectoryCache"/> when live
/// discovery is unavailable (the discovery source itself is #493's deferred work). Everything is optional
/// with a sensible default, so the feature is zero-config: an absent <c>superAgents</c> key loads an empty
/// seed. Kept as its own nested settings object (like <see cref="AgentDispatchSettings"/>) so the growing
/// Super Agents surface hangs off one place rather than scattering keys across <see cref="AppConfig"/>.
/// </summary>
public sealed class SuperAgentSettings
{
    /// <summary>Hand-pinned agents (#494) — the manual seed the registry merges ahead of discovery. Empty
    /// by default; absent in an old config ⇒ empty.</summary>
    public List<AgentSeedEntry> Agents { get; set; } = [];

    /// <summary>True when nothing has been customised (no pinned agents).</summary>
    public bool IsDefault => Agents.Count == 0;
}
