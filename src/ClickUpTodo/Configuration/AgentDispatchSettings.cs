using System.Text.Json.Serialization;
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
/// How a dispatched <c>claude</c> session runs (issue #94): an <see cref="Interactive"/> session
/// (<c>claude "[prompt]"</c>, today's behaviour) the user drives, or a <see cref="OneOff"/> run
/// (<c>claude -p "[prompt]"</c>) that executes the prompt non-interactively and exits. This is the
/// persisted <b>default</b> for the per-dispatch toggle #94 adds to the Dispatch pane.
/// </summary>
public enum AgentSessionMode
{
    /// <summary>An interactive session the user drives (default).</summary>
    Interactive,

    /// <summary>A non-interactive one-off run (<c>claude -p</c>) that executes and exits.</summary>
    OneOff,
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
    /// The default session mode (#94) the Dispatch pane's toggle initializes from —
    /// <see cref="AgentSessionMode.Interactive"/> (today's behaviour) or a one-off <c>claude -p</c>
    /// run. The per-dispatch toggle (#93/#94) may override it; #94 threads the chosen mode into
    /// dispatch. Absent in an old config ⇒ <see cref="AgentSessionMode.Interactive"/>.
    /// </summary>
    public AgentSessionMode DefaultSessionMode { get; set; } = AgentSessionMode.Interactive;

    /// <summary>
    /// The default for the "post results to Comments" toggle (#97) the Dispatch pane initializes
    /// from. When on, the dispatched agent is instructed (in the composed prompt) to post a summary
    /// comment back to the task. Off by default; absent in an old config ⇒ off. The per-dispatch
    /// toggle may override it.
    /// </summary>
    public bool DefaultPostResultsToComments { get; set; }

    /// <summary>
    /// The whole dispatch prompt as an editable template of placeholders (#100). Blank ⇒ the
    /// <see cref="AgentPromptComposer.DefaultTemplate"/> (whose rendering is byte-for-byte the pre-#100
    /// output). Supersedes the #27 single-line preamble override; edited on the dedicated template
    /// editor screen reached from F2.
    /// </summary>
    public string PromptTemplate { get; set; } = "";

    /// <summary>
    /// Deserialize-only migration shim (#100) for the retired #27 <c>promptPreamble</c> key. A saved
    /// non-blank value is carried forward into <see cref="PromptTemplate"/> by
    /// <see cref="ConfigMigrations"/>, which then nulls this out so it is never written again (the
    /// <see cref="JsonIgnoreCondition.WhenWritingNull"/> ignore drops it from <c>config.json</c>).
    /// </summary>
    [JsonPropertyName("promptPreamble")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPromptPreamble { get; set; }

    /// <summary>True when nothing has been customised, so all launcher/composer defaults apply.</summary>
    public bool IsDefault =>
        PreferredTerminal == PreferredTerminal.Auto
        && (string.IsNullOrWhiteSpace(ClaudeExecutable) || ClaudeExecutable == "claude")
        && ExtraArgs.Count == 0
        && WorkingDirectory == AgentWorkingDirectory.TaskDerived
        && string.IsNullOrWhiteSpace(FixedWorkingDirectory)
        && DefaultSessionMode == AgentSessionMode.Interactive
        && !DefaultPostResultsToComments
        && string.IsNullOrWhiteSpace(PromptTemplate);

    /// <summary>
    /// Projects these settings onto the launcher's <see cref="TerminalLauncherOptions"/>, coalescing a
    /// blank executable back to the <c>"claude"</c> default and copying the extra args and preference.
    /// </summary>
    public TerminalLauncherOptions ToLauncherOptions() => new()
    {
        ClaudeExecutable = string.IsNullOrWhiteSpace(ClaudeExecutable) ? "claude" : ClaudeExecutable.Trim(),
        // Trim and drop blanks so hand-edited config.json values are cleaned the same way the F2
        // dialog's ParseExtraArgs cleans typed input (and the executable is coalesced above).
        ExtraArgs = [.. ExtraArgs.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())],
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

    /// <summary>
    /// Resolves the working directory a dispatch should start in, applying the epic-#90 precedence:
    /// a <b>per-task cached directory</b> (#96) wins when present, otherwise the configured default
    /// mode via <see cref="ResolveWorkingDirectory"/> (which itself backstops onto the task-derived
    /// candidate #98 / base dir #92). Pure so it can be unit-tested; a blank/whitespace cache entry
    /// is treated as "no cache" and falls through. Today all call sites pass a null
    /// <paramref name="cachedDirectory"/> (the #96 cache lands later), so behaviour is unchanged.
    /// </summary>
    public string? ResolveEffectiveWorkingDirectory(
        string? cachedDirectory, string? taskDerivedDirectory, string? homeDirectory)
        => Blank(cachedDirectory) ?? ResolveWorkingDirectory(taskDerivedDirectory, homeDirectory);

    /// <summary>Null out blank/whitespace so the launcher inherits the current directory.</summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
