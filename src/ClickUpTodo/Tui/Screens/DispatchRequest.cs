namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// The options gathered by the detail view's Dispatch pane (issue #93, D1 of the #90 epic) and
/// carried by <see cref="TaskDetailScreen.AgentDispatchRequested"/>. Today it holds only the prompt;
/// the pane's stub controls — one-off vs interactive (#94), working directory (#95), and
/// post-results-to-Comments (#97) — extend this record as they land, so the event signature stays
/// stable while those features fill in.
/// </summary>
public sealed record DispatchRequest(string Prompt);
