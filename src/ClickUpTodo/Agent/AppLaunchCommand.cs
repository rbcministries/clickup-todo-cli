namespace ClickUpTodo.Agent;

/// <summary>
/// How to relaunch <b>this app</b> in a new terminal for a single task (#301): an executable plus its
/// argument vector (built as an array, never a concatenated shell string), running
/// <c>clickup-todo --task &lt;id&gt;</c>. The pure <see cref="ForTask(string, Func{string, bool}, string?)"/>
/// resolver decides which executable to invoke; <see cref="TerminalCommandPlanner.PlanAppLaunch"/> wraps
/// it in the same cross-platform emulator matrix the agent-dispatch launcher (#25/#307) already uses.
/// </summary>
/// <param name="FileName">Executable to run (resolved via <c>PATH</c> when it's a bare name).</param>
/// <param name="Arguments">Argument vector (<c>["--task", "&lt;id&gt;"]</c>).</param>
public sealed record AppLaunchCommand(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>The installed global-tool / apphost command name.</summary>
    public const string ToolName = "clickup-todo";

    /// <summary>
    /// Resolve how to relaunch this app to open <paramref name="taskId"/> in its own tab, given a
    /// PATH probe (<paramref name="exists"/>) and the current process path
    /// (<paramref name="processPath"/>, i.e. <see cref="Environment.ProcessPath"/>):
    /// <list type="bullet">
    /// <item><c>clickup-todo</c> on PATH → <c>clickup-todo --task &lt;id&gt;</c> (the installed
    /// global-tool / apphost case, the common one).</item>
    /// <item>else a real apphost (the process file name isn't <c>dotnet</c>) → relaunch it directly with
    /// <c>--task &lt;id&gt;</c>.</item>
    /// <item>else (a <c>dotnet run</c> / <c>dotnet ClickUpTodo.dll</c> dev launch, whose muxer can't be
    /// relaunched with <c>--task</c>) → best-effort <c>clickup-todo --task &lt;id&gt;</c>; the launch
    /// will fail to find it and the caller shows the copy-command fallback rather than silently
    /// mis-launching.</item>
    /// </list>
    /// Pure (probe + path injected) so it's unit-testable without touching the real PATH or process.
    /// </summary>
    public static AppLaunchCommand ForTask(string taskId, Func<string, bool> exists, string? processPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(exists);

        IReadOnlyList<string> args = [ClickUpTodo.TaskLaunchArg.Flag, taskId.Trim()];

        if (exists(ToolName))
            return new AppLaunchCommand(ToolName, args);

        if (!string.IsNullOrWhiteSpace(processPath) && !IsDotnetMuxer(processPath))
            return new AppLaunchCommand(processPath, args);

        return new AppLaunchCommand(ToolName, args);
    }

    /// <summary>
    /// Convenience overload for the real call site: probes the live <c>PATH</c>
    /// (<see cref="TerminalLauncher.ExecutableOnPath"/>) and reads <see cref="Environment.ProcessPath"/>.
    /// </summary>
    public static AppLaunchCommand ForTask(string taskId)
        => ForTask(taskId, TerminalLauncher.ExecutableOnPath, Environment.ProcessPath);

    /// <summary>An <b>approximate</b>, human-readable command string for the copy-to-clipboard /
    /// status-line fallback when no terminal can be launched — quotes only the tokens that contain
    /// spaces, the way a user would type it. This is deliberately simpler than the real launch quoting
    /// (<c>PwshQuote</c>/<c>PosixQuote</c> in the planner): it's for a human to read/paste, and task ids
    /// are alphanumeric, so shell-metacharacter escaping isn't attempted here.</summary>
    public string ToDisplayCommand()
        => string.Join(" ", new[] { FileName }.Concat(Arguments).Select(QuoteForDisplay));

    private static string QuoteForDisplay(string token)
        => token.Length == 0 || token.Contains(' ') ? $"\"{token}\"" : token;

    private static bool IsDotnetMuxer(string processPath)
    {
        // Split on both separators explicitly: a Windows path (`…\dotnet.exe`) must be recognised even
        // when the resolver runs on Linux (where Path.GetFileName treats '\' as an ordinary character).
        var fileName = processPath.Split('/', '\\')[^1];
        var stem = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
        return string.Equals(stem, "dotnet", StringComparison.OrdinalIgnoreCase);
    }
}
