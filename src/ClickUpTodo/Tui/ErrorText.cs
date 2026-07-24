using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui;

/// <summary>
/// Shared formatting for exceptions surfaced on a host's status line. Both TUI hosts (the dashboard
/// <see cref="TodoApp"/> and the single-task <see cref="SingleTaskApp"/>) flash the same short form,
/// so it lives here rather than being copied into each (#346).
/// </summary>
internal static class ErrorText
{
    /// <summary>
    /// The concise, user-facing form of <paramref name="ex"/> for a status flash: a
    /// <see cref="ClickUpApiException"/> already carries a curated message, so use it verbatim;
    /// anything else falls back to the raw <see cref="Exception.Message"/>.
    /// </summary>
    public static string Short(Exception ex) => ex is ClickUpApiException c ? c.Message : ex.Message;
}
