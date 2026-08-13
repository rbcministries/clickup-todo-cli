namespace ClickUpTodo.Agent;

/// <summary>
/// The single, pure composer for the interactive dispatch status line (#517, split-pane epic slice L).
/// Three features each independently — and correctly — concluded that a silent change to <em>where or
/// how</em> a dispatched session launched is unexplainable to the user, so each wanted a clause on the
/// status message: #515's split→tab degradation reason, the launcher-level fall-back <see
/// cref="LaunchResult.Note"/> folded into the core message, and #462's Windows Terminal profile note.
/// Left implicit, their composition was decided by merge order (an inline concat in
/// <c>DispatchCoordinator.RunInteractive</c>); this type makes it one deliberate, tested rule.
/// <para>
/// Pure formatting over <b>already-computed</b> facts — it does not launch anything, resolve a
/// directory, match a profile, or rebuild the core message (that stays with
/// <see cref="AgentDispatcher.FormatStatus"/>, so the background/one-off path is untouched). The
/// composition rule, in priority order:
/// </para>
/// <list type="number">
/// <item>a <b>failed</b> launch is exactly the core failure message — the degradation and profile
/// clauses are suppressed, because nothing opened and both would mislead;</item>
/// <item><b>degradation leads</b> — "you asked for a pane and got a tab" is the highest-value clause
/// and is never buried behind the core or the profile;</item>
/// <item>the <b>core message</b> follows, carrying its launcher <see cref="LaunchResult.Note"/>
/// unchanged;</item>
/// <item>the <b>profile</b> trails — lowest-value, parenthetical, and only when a Windows Terminal host
/// actually launched;</item>
/// <item><b>all-defaults</b> (destination honoured, no profile, no note) is just the core message — no
/// empty clauses.</item>
/// </list>
/// </summary>
public static class DispatchStatusLine
{
    /// <summary>
    /// Composes the interactive dispatch status line from the already-computed launch facts.
    /// </summary>
    /// <param name="coreStatusMessage">The launcher's own outcome text
    /// (<see cref="AgentDispatcher.FormatStatus"/> / <see cref="AgentDispatchResult.StatusMessage"/>):
    /// <c>"Launched Claude ({terminal}) for '{task}'."</c> plus any non-fatal
    /// <see cref="LaunchResult.Note"/> on success, or <c>"Could not launch Claude: …"</c> on failure.</param>
    /// <param name="launched"><see cref="AgentDispatchResult.Success"/> — a failed launch suppresses the
    /// extra clauses.</param>
    /// <param name="launchedWith">The terminal the launch actually used
    /// (<see cref="AgentDispatchResult.LaunchedWith"/>) — gates the #462 profile clause.</param>
    /// <param name="splitDegradedReason">The #505/#515 viability-floor reason
    /// (<c>ResolvedDispatch.SplitDegradedReason</c>) when a split was downgraded to a tab, else null.</param>
    /// <param name="windowsTerminalProfile">The #462 matched Windows Terminal profile name
    /// (<c>ResolvedDispatch.WindowsTerminalProfile</c>), else null.</param>
    public static string Compose(
        string coreStatusMessage,
        bool launched,
        string? launchedWith,
        string? splitDegradedReason,
        string? windowsTerminalProfile)
    {
        ArgumentNullException.ThrowIfNull(coreStatusMessage);

        // A failed launch opened nothing, so a "too narrow to split, opening elsewhere" lead or a
        // "under profile X" trailer would both describe a launch that never happened. The core message
        // already carries the failure reason; that is the whole, honest line.
        if (!launched)
            return coreStatusMessage;

        var lead = string.IsNullOrWhiteSpace(splitDegradedReason) ? "" : splitDegradedReason.Trim() + " ";
        var trail = WindowsTerminalProfileNote(windowsTerminalProfile, launchedWith) ?? "";
        return lead + coreStatusMessage + trail;
    }

    /// <summary>
    /// The #462 status-line clause naming the Windows Terminal profile a dispatch launched under (with a
    /// leading space, so it appends after the core message), or <c>null</c> when none applies. A profile
    /// matches on directory alone, so it is claimed <b>only</b> when <paramref name="launchedWith"/> is
    /// actually a Windows Terminal host — a launch that fell to a non-WT terminal (an explicit
    /// <c>PreferredTerminal</c>, a <c>CustomTerminalCommand</c>, or <c>wt</c> absent) or failed outright
    /// (null) never applied the profile, so claiming it would mislead.
    /// </summary>
    public static string? WindowsTerminalProfileNote(string? windowsTerminalProfile, string? launchedWith)
        => !string.IsNullOrWhiteSpace(windowsTerminalProfile)
            && launchedWith is { } host
            && host.StartsWith("Windows Terminal", StringComparison.Ordinal)
            ? $" (Windows Terminal profile '{windowsTerminalProfile}'.)"
            : null;
}
