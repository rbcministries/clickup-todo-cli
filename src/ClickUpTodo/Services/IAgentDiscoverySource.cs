namespace ClickUpTodo.Services;

/// <summary>
/// The seam through which <see cref="AgentDirectoryCache"/> populates its <b>discovered</b> layer — the
/// live "who are the agents in this workspace" lookup that stands in for ClickUp's missing agent
/// enumeration endpoint. The spike (<c>docs/plans/super-agents-spike.md</c>, Finding 2) settled the
/// source as <c>getChatChannelMembers</c> (supplemented by a channel author-scan), but wiring it needs
/// the v3 chat client whose strategy is <b>#493</b>'s open decision — so this interface is the deferred
/// half's insertion point, deliberately left unimplemented in this slice.
/// <para>
/// When no source is supplied the cache runs seed-only: <see cref="AgentDirectoryCache.RefreshAsync"/> is
/// a strict no-op and the config-seeded pins are still served. Implementations should return agent
/// entries (negative id + display name); the cache re-stamps them <see cref="AgentEntrySource.Discovered"/>
/// and applies the <see cref="AgentDirectory.IsValid">agent-id/non-blank-name</see> filter, so a source
/// need not pre-filter.
/// </para>
/// </summary>
public interface IAgentDiscoverySource
{
    /// <summary>Discover the agents currently reachable in <paramref name="workspaceId"/>. May throw on a
    /// transport error — <see cref="AgentDirectoryCache.RefreshAsync"/> lets it propagate and leaves the
    /// existing discovered layer intact.</summary>
    Task<IReadOnlyList<AgentDirectoryEntry>> DiscoverAsync(string workspaceId, CancellationToken ct = default);
}
