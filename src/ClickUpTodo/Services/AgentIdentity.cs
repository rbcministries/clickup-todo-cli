namespace ClickUpTodo.Services;

/// <summary>
/// The single place that decides whether a ClickUp user id belongs to a <b>Super Agent</b> rather than a
/// human. ClickUp exposes agents as chat actors with <b>negative</b> ids (the spike observed
/// <c>-10466700</c> "Recap Rio" and others creating/owning chat channels — see
/// <c>docs/plans/super-agents-spike.md</c>, Finding 0), whereas every human member returned by
/// <c>GET /team</c> carries a positive id. Human-facing code elsewhere keeps the inverse guard inline
/// (<see cref="MentionDetector"/> and <see cref="AssigneeFrequency"/> drop non-positive ids), so agents
/// are invisible to today's mention/assignee matching by design.
/// <para>
/// This is treated as a <b>heuristic, not a contract</b> — the sign convention is undocumented — but
/// keeping it behind one predicate (per #494) means a single edit moves the whole agent surface if the
/// convention ever changes, rather than chasing scattered <c>&lt; 0</c> comparisons through call sites.
/// </para>
/// </summary>
public static class AgentIdentity
{
    /// <summary>True when <paramref name="id"/> looks like a Super Agent id (negative). Zero and
    /// positive ids are humans / the system id and are never agents.</summary>
    public static bool IsAgentId(long id) => id < 0;

    /// <summary>Nullable convenience overload: a null id is never an agent.</summary>
    public static bool IsAgentId(long? id) => id is { } value && IsAgentId(value);
}
