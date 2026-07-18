namespace ClickUpTodo;

/// <summary>
/// Parses the single-task launch flag — <c>--task &lt;id&gt;</c> or <c>--task=&lt;id&gt;</c> (#296) —
/// out of the process argv. Pure and unit-tested; <c>Program</c> consumes the result to choose
/// between the normal dashboard boot and booting straight into one task's Task Detail view.
/// <para>
/// Kept a distinct type (rather than reusing the inline <c>GetOption</c>) so "flag present but no
/// value" is a first-class, testable state: a bare <c>--task</c> (or <c>--task=</c> / whitespace)
/// must fail with a clear message instead of silently launching the dashboard or opening a blank id.
/// Only the raw ClickUp API task id is accepted for now; URL / custom-id forms are a noted follow-up
/// (they'll share the task-URL parser #316 adds).
/// </para>
/// </summary>
internal readonly record struct TaskLaunchArg(bool Present, string? TaskId)
{
    /// <summary>The launch flag itself.</summary>
    public const string Flag = "--task";

    /// <summary>The flag was given with a usable (non-blank) task id.</summary>
    public bool HasId => Present && !string.IsNullOrWhiteSpace(TaskId);

    /// <summary>The flag was given but without a task id (bare <c>--task</c>, <c>--task=</c>, or blank).</summary>
    public bool MissingValue => Present && string.IsNullOrWhiteSpace(TaskId);

    /// <summary>
    /// Scans <paramref name="args"/> for the launch flag. Returns <c>Present=false</c> when the flag is
    /// absent; otherwise <c>Present=true</c> with the trimmed id (or <c>null</c> when the flag carries no
    /// value). The first occurrence wins, mirroring <c>Program.GetOption</c>.
    /// </summary>
    public static TaskLaunchArg Parse(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == Flag)
            {
                var value = i + 1 < args.Length ? args[i + 1].Trim() : null;
                return new TaskLaunchArg(true, string.IsNullOrEmpty(value) ? null : value);
            }
            if (arg.StartsWith(Flag + "=", StringComparison.Ordinal))
            {
                var value = arg[(Flag.Length + 1)..].Trim();
                return new TaskLaunchArg(true, value.Length == 0 ? null : value);
            }
        }
        return new TaskLaunchArg(false, null);
    }
}
