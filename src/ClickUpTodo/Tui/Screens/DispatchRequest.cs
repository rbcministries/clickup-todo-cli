using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// The options gathered by the detail view's Dispatch pane (issue #93, D1 of the #90 epic) and
/// carried by <see cref="TaskDetailScreen.AgentDispatchRequested"/>. It holds the prompt plus the
/// pane's per-dispatch options as they land — <see cref="SessionMode"/> (one-off vs interactive,
/// #94); working directory (#95) and post-results-to-Comments (#97) extend this record next, so the
/// event signature stays stable while those features fill in.
/// </summary>
/// <param name="Prompt">The user's typed prompt.</param>
/// <param name="SessionMode">
/// Whether to launch an interactive session (default) or a one-off <c>claude -p</c> run (#94).
/// </param>
public sealed record DispatchRequest(string Prompt, AgentSessionMode SessionMode = AgentSessionMode.Interactive);
