using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure input-handling logic for the New Task screen (#213/#215), factored out of the Terminal.Gui glue
/// so it can be unit-tested (mirrors <see cref="FilterSortGroupForm"/> / <c>SettingsForm</c>): validate
/// the entered fields and build the domain-facing <see cref="NewTaskRequest"/> the create facade (#209)
/// consumes. Only Name is required; an empty description is omitted (sent as <c>null</c> so ClickUp
/// applies the list default), and the assignee ids are de-duped with non-positive ids dropped, order
/// preserved. The optional Priority (#215) is passed through when it is one of the four canonical levels
/// (else cleared), and the optional Due date (#215) is parsed via the shared date convention — blank
/// means undated, and unparseable input blocks Save. Status is not part of this screen (new tasks take
/// the list default).
/// </summary>
public static class NewTaskForm
{
    /// <summary>The validation error shown when Save is pressed with a blank name.</summary>
    public const string NameRequiredError = "A task name is required.";

    /// <summary>The validation error shown when the Due date field can't be parsed (#215).</summary>
    public const string DueDateInvalidError = "Due date must be a date like 2026-07-15 (yyyy-MM-dd).";

    /// <summary>
    /// Validates the entered fields and, on success, builds the <see cref="NewTaskRequest"/>:
    /// <paramref name="name"/> trimmed and required (else <see cref="NameRequiredError"/>);
    /// <paramref name="description"/> trimmed, becoming <c>null</c> when blank so the facade omits it;
    /// <paramref name="assigneeIds"/> de-duped preserving first-appearance order, dropping non-positive
    /// ids; <paramref name="priorityLevel"/> kept only when it is a canonical importance level (1=Urgent
    /// … 4=Low, see <see cref="ClickUpPriority"/>), else cleared to <c>null</c>; <paramref name="dueDate"/>
    /// blank ⇒ undated, otherwise parsed via <see cref="TaskFieldInfo.TryParseNumeric"/> (the same
    /// <c>yyyy-MM-dd</c>/epoch-ms/ISO convention the F3 date filters use) — an unparseable value fails
    /// with <see cref="DueDateInvalidError"/>. The name check runs first, so a blank name reports the
    /// name error even when the due date is also invalid. Returns false with a non-null
    /// <paramref name="error"/> and a null <paramref name="request"/> when invalid.
    /// </summary>
    public static bool TryBuild(
        string? name,
        string? description,
        IReadOnlyList<long> assigneeIds,
        int? priorityLevel,
        string? dueDate,
        out NewTaskRequest? request,
        out string? error)
    {
        request = null;

        var trimmedName = (name ?? string.Empty).Trim();
        if (trimmedName.Length == 0)
        {
            error = NameRequiredError;
            return false;
        }

        // Due date: blank leaves the task undated; otherwise it must parse, else Save is blocked (rather
        // than silently dropping a date the user typed).
        long? dueDateMs = null;
        var trimmedDue = (dueDate ?? string.Empty).Trim();
        if (trimmedDue.Length > 0)
        {
            if (!TaskFieldInfo.TryParseNumeric(trimmedDue, out var ms))
            {
                error = DueDateInvalidError;
                return false;
            }
            dueDateMs = ms;
        }

        var trimmedDescription = (description ?? string.Empty).Trim();

        var assignees = new List<long>();
        var seen = new HashSet<long>();
        foreach (var id in assigneeIds)
        {
            if (id > 0 && seen.Add(id))
                assignees.Add(id);
        }

        request = new NewTaskRequest
        {
            Name = trimmedName,
            Description = trimmedDescription.Length == 0 ? null : trimmedDescription,
            Assignees = assignees,
            // Only the four canonical levels are meaningful; anything else means "no priority".
            PriorityLevel = priorityLevel is >= 1 and <= 4 ? priorityLevel : null,
            DueDateMs = dueDateMs,
        };
        error = null;
        return true;
    }
}
