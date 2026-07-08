namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// The options gathered by the detail view's Dispatch pane (issue #93, D1 of the #90 epic) and
/// carried by <see cref="TaskDetailScreen.AgentDispatchRequested"/>. It holds the prompt and, since
/// #95, the chosen working directory (blank ⇒ null ⇒ the configured-default / task-derived
/// behaviour). The remaining stub controls — one-off vs interactive (#94) and
/// post-results-to-Comments (#97) — extend this record as they land, so the event signature stays
/// stable while those features fill in.
/// </summary>
/// <param name="Prompt">The prompt text the user typed.</param>
/// <param name="WorkingDirectory">
/// The working directory picked in the pane (#95), or null/blank to fall through to the configured
/// default mode / task-derived directory. An explicit value overrides the configured mode.
/// </param>
public sealed record DispatchRequest(string Prompt, string? WorkingDirectory = null);
