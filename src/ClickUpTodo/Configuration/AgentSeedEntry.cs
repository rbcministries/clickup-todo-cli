namespace ClickUpTodo.Configuration;

/// <summary>
/// One hand-pinned Super Agent in <see cref="SuperAgentSettings.Agents"/> — the manual config seed for
/// the agent registry (#494). ClickUp has no agent-enumeration endpoint, so a user can pin an agent's
/// <c>name → negative id</c> mapping by hand here when discovery is unavailable or wrong; the seed is
/// authoritative over discovery. A mutable POCO (like <see cref="DispatchProvider"/>) so it round-trips
/// through System.Text.Json in <c>config.json</c>.
/// </summary>
public sealed class AgentSeedEntry
{
    /// <summary>The agent's ClickUp id — <b>negative</b> for a Super Agent
    /// (see <see cref="Services.AgentIdentity"/>). A non-agent (zero/positive) id is dropped on ingest.</summary>
    public long Id { get; set; }

    /// <summary>The agent's display name, shown in the picker. A blank name drops the entry.</summary>
    public string Name { get; set; } = "";

    /// <summary>Optional purpose blurb the picker may show; null/absent is fine.</summary>
    public string? Purpose { get; set; }
}
