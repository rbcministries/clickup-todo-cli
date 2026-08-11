namespace ClickUpTodo;

/// <summary>
/// Parses the standalone-feed launch flag — <c>--feed</c> (#509) — out of the process argv. The
/// mentions &amp; comments feed, until now reachable only inside the dashboard via <c>Ctrl+E</c>, can be
/// booted as its own application host (the Split-pane epic #502's sub-issue G), mirroring how
/// <see cref="TaskLaunchArg"/> gates the single-task host (<c>--task</c>, #296).
/// <para>
/// Unlike <c>--task</c> the feed flag carries <b>no value</b> — it is a bare presence switch, like
/// <c>--reset</c>/<c>--help</c> — so this type owns only the present/absent distinction. Kept a distinct,
/// pure, unit-tested type (rather than an inline <c>args.Contains</c>) so the flag has one shared,
/// testable definition and the shape matches <see cref="TaskLaunchArg"/> at the <c>Program</c> dispatch.
/// </para>
/// </summary>
internal readonly record struct FeedLaunchArg(bool Present)
{
    /// <summary>The launch flag itself.</summary>
    public const string Flag = "--feed";

    /// <summary>
    /// Scans <paramref name="args"/> for the launch flag. Returns <c>Present=true</c> when
    /// <see cref="Flag"/> appears as an exact token, otherwise <c>Present=false</c>. An <c>=</c>-form
    /// (<c>--feed=…</c>) is not accepted — the flag takes no value — and a different flag sharing the
    /// prefix (a hypothetical <c>--feedback</c>) never matches, since only an exact-token compare is used.
    /// </summary>
    public static FeedLaunchArg Parse(string[] args)
        => new(Array.Exists(args, a => a == Flag));
}
