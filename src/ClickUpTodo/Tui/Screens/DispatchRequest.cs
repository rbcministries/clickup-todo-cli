using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// The options gathered by the detail view's Dispatch pane (issue #93, D1 of the #90 epic) and
/// carried by <see cref="TaskDetailScreen.AgentDispatchRequested"/>. It holds the prompt plus the
/// pane's per-dispatch options — <see cref="SessionMode"/> (one-off vs interactive, #94), the chosen
/// <see cref="WorkingDirectory"/> (#95), and <see cref="PostToComments"/> (#97). The event signature
/// stays stable as further options land.
/// </summary>
/// <param name="Prompt">The user's typed prompt.</param>
/// <param name="SessionMode">
/// Whether to launch an interactive session (default) or a one-off <c>claude -p</c> run (#94).
/// </param>
/// <param name="WorkingDirectory">
/// The working directory picked in the pane (#95), or null/blank to fall through to the configured
/// default mode / task-derived directory. An explicit value overrides the configured mode.
/// </param>
/// <param name="PostToComments">
/// Whether to append an instruction (to the composed prompt) telling the dispatched agent to post a
/// summary comment back to the ClickUp task (#97). Default off.
/// </param>
public sealed record DispatchRequest(
    string Prompt,
    AgentSessionMode SessionMode = AgentSessionMode.Interactive,
    string? WorkingDirectory = null,
    bool PostToComments = false);
