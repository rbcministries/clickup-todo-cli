using System.Collections.Generic;
using System.Linq;

using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// Resolves a workspace member's human-friendly display name to a numeric ClickUp <c>userId</c>, so
/// the @-mention author path (#323, foundation for the picker in #324) can turn a name-driven
/// selection ("Ben Seymour") into the id the mention payload needs.
///
/// <para>Matching is <b>exact</b>, case-insensitive, and trimmed — against the member's
/// <see cref="WorkspaceMember.DisplayName"/>, raw <see cref="WorkspaceMember.Username"/>, or
/// <see cref="WorkspaceMember.Email"/>. There is deliberately <b>no fuzzy/substring/prefix
/// matching</b>: this feeds a write path, and a near-miss would @-mention the wrong person. The
/// picker itself supplies a chosen member, so the common path never relies on free-text matching.</para>
/// </summary>
public static class MemberResolver
{
    /// <summary>The userId for a member chosen in the picker: the member's own id, no matching
    /// involved. This is the primary path (the picker supplies the exact <see cref="WorkspaceMember"/>).</summary>
    public static long ResolveId(WorkspaceMember member) => member.Id;

    /// <summary>Resolves a typed or selected name to a member's userId, or <c>null</c> when no member
    /// matches exactly (case-insensitive, trimmed) on display name, username, or email. On the rare
    /// duplicate-name workspace the first roster match wins, deterministically.</summary>
    public static long? ResolveId(IReadOnlyList<WorkspaceMember> members, string? name)
    {
        var needle = name?.Trim();
        if (string.IsNullOrEmpty(needle) || members is null)
            return null;

        foreach (var m in members)
        {
            if (m.Id == 0)
                continue;
            if (Matches(m.DisplayName, needle)
                || Matches(m.Username, needle)
                || Matches(m.Email, needle))
                return m.Id;
        }
        return null;
    }

    private static bool Matches(string? candidate, string needle)
        => !string.IsNullOrWhiteSpace(candidate)
           && string.Equals(candidate.Trim(), needle, System.StringComparison.OrdinalIgnoreCase);
}
