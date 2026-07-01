using ClickUpTodo.Agent;

namespace ClickUpTodo.Configuration;

/// <summary>Where a dispatched <c>claude</c> session starts (issue #27).</summary>
public enum AgentWorkingDirectory
{
    /// <summary>Use a directory derived from the task (supplied at dispatch time); inherit if none.</summary>
    TaskDerived,

    /// <summary>Start in the user's home directory.</summary>
    Home,

    /// <summary>Start in <see cref="AgentDispatchSettings.FixedWorkingDirectory"/>.</summary>
    Fixed,
}

/// <summary>
/// User-facing configuration for the agent-dispatch feature (#23), persisted in
/// <c>config.json</c>. Every setting is optional with a sensible default, so dispatch works with
/// zero configuration; this record is the seam that populates <see cref="TerminalLauncherOptions"/>
/// (see its doc comment) and the composer preamble. The <c>A</c>-key wiring (#26) consumes these.
/// </summary>
public sealed class AgentDispatchSettings
{
    /// <summary>Which terminal to prefer on Windows; <see cref="PreferredTerminal.Auto"/> uses the fallback chain.</summary>
    public PreferredTerminal PreferredTerminal { get; set; } = PreferredTerminal.Auto;

    /// <summary>The <c>claude</c> executable to invoke (looked up on PATH). Blank ⇒ <c>"claude"</c>.</summary>
    public string ClaudeExecutable { get; set; } = "claude";

    /// <summary>Extra arguments inserted before the prompt argument (e.g. a model flag).</summary>
    public List<string> ExtraArgs { get; set; } = [];

    /// <summary>Which directory the new session starts in.</summary>
    public AgentWorkingDirectory WorkingDirectory { get; set; } = AgentWorkingDirectory.TaskDerived;

    /// <summary>The directory used when <see cref="WorkingDirectory"/> is <see cref="AgentWorkingDirectory.Fixed"/>.</summary>
    public string FixedWorkingDirectory { get; set; } = "";

    /// <summary>
    /// Overrides the composer's fixed preamble line (the text between the user prompt and the JSON
    /// payload). Blank ⇒ the default <see cref="AgentPromptComposer.Preamble"/>.
    /// </summary>
    public string PromptPreamble { get; set; } = "";

    /// <summary>True when nothing has been customised, so all launcher/composer defaults apply.</summary>
    public bool IsDefault =>
        PreferredTerminal == PreferredTerminal.Auto
        && (string.IsNullOrWhiteSpace(ClaudeExecutable) || ClaudeExecutable == "claude")
        && ExtraArgs.Count == 0
        && WorkingDirectory == AgentWorkingDirectory.TaskDerived
        && string.IsNullOrWhiteSpace(FixedWorkingDirectory)
        && string.IsNullOrWhiteSpace(PromptPreamble);

    /// <summary>
    /// Projects these settings onto the launcher's <see cref="TerminalLauncherOptions"/>, coalescing a
    /// blank executable back to the <c>"claude"</c> default and copying the extra args and preference.
    /// </summary>
    public TerminalLauncherOptions ToLauncherOptions() => new()
    {
        ClaudeExecutable = string.IsNullOrWhiteSpace(ClaudeExecutable) ? "claude" : ClaudeExecutable.Trim(),
        ExtraArgs = [.. ExtraArgs],
        Preferred = PreferredTerminal,
    };

    /// <summary>
    /// Resolves the directory to start the session in (null ⇒ inherit the current directory), given
    /// the task-derived candidate and the user's home directory. Pure so it can be unit-tested; the
    /// dispatch call site (#26) supplies <paramref name="taskDerivedDirectory"/> and
    /// <paramref name="homeDirectory"/>. A blank fixed/home path falls back to inherit.
    /// </summary>
    public string? ResolveWorkingDirectory(string? taskDerivedDirectory, string? homeDirectory) => WorkingDirectory switch
    {
        AgentWorkingDirectory.Home => Blank(homeDirectory),
        AgentWorkingDirectory.Fixed => Blank(FixedWorkingDirectory),
        _ => Blank(taskDerivedDirectory),
    };

    /// <summary>Null out blank/whitespace so the launcher inherits the current directory.</summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
