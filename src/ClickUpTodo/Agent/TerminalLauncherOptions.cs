namespace ClickUpTodo.Agent;

/// <summary>Which terminal to prefer on Windows; <see cref="Auto"/> uses the full fallback chain.</summary>
public enum PreferredTerminal
{
    Auto,
    WindowsTerminal,
    Pwsh,
    PowerShell,

    /// <summary>
    /// Open a new window via <c>cmd /c start</c> hosting a PowerShell process (the last-resort
    /// fallback from #45). Requires a PowerShell host to be present, since the payload runs there.
    /// </summary>
    Cmd,
}

/// <summary>
/// Where an interactive dispatch's <c>claude</c> session opens (issue #255): a
/// <see cref="NewWindow"/> (the historical default), a <see cref="NewTab"/> of the terminal the
/// app is already running in, or a <see cref="SplitPane"/> beside it (#502), where the host supports it.
/// The in-place destinations are best-effort and detection-gated per emulator; when the host can't honour
/// the request it degrades down the split → tab → window ladder. All apply to interactive sessions only —
/// a one-off <c>claude -p</c> runs through the background runner with no terminal, so an in-place
/// location is meaningless there.
/// </summary>
public enum LaunchLocation
{
    /// <summary>Open the session in a new terminal window (default; today's behavior).</summary>
    NewWindow,

    /// <summary>Open the session in a new tab of the current terminal where supported, else a window.</summary>
    NewTab,

    /// <summary>
    /// Open the session in a split pane beside the current one (#502), where the host supports it
    /// (Windows Terminal, tmux, iTerm2, WezTerm, kitty, Zellij). Degrades down the split → tab → window
    /// ladder on a host with no scriptable split. Interactive-only, like <see cref="NewTab"/>.
    /// </summary>
    SplitPane,
}

/// <summary>
/// Which way a <see cref="LaunchLocation.SplitPane"/> divides the current pane (#505, slice C). Expressed
/// once here and mapped to each host's own vocabulary by <see cref="TerminalCommandPlanner"/> (WT
/// <c>-V</c>/<c>-H</c>, tmux <c>-h</c>/<c>-v</c>, WezTerm <c>--right</c>/<c>--bottom</c>, kitty
/// <c>vsplit</c>/<c>hsplit</c>, Zellij <c>right</c>/<c>down</c>, iTerm <c>split vertically</c>/
/// <c>horizontally</c>).
/// </summary>
public enum SplitDirection
{
    /// <summary>
    /// Let the host choose by pane aspect ratio where it can — Windows Terminal omits <c>-V</c>/<c>-H</c>
    /// (the issue's recommended default, right on both a wide and a tall monitor). Hosts with no auto fall
    /// back to <see cref="Beside"/>, keeping the pre-#505 side-by-side split byte-identical.
    /// </summary>
    Auto,

    /// <summary>Side by side — a vertical divider, the new pane to the right.</summary>
    Beside,

    /// <summary>Stacked — a horizontal divider, the new pane beneath.</summary>
    Below,
}

/// <summary>
/// Whether a <see cref="LaunchLocation.SplitPane"/> keeps focus in the current pane after splitting
/// (#505, slice C). Best-effort per host capability: the planner appends the documented stay-put token
/// only on the hosts that support one (Windows Terminal <c>mf previous</c>, tmux <c>-d</c>, kitty
/// <c>--dont-take-focus</c>) and leaves it a documented no-op elsewhere (WezTerm, Zellij, iTerm2), rather
/// than faking it. Driven by launch intent: an ambient <c>--feed</c> sidebar wants <see cref="StayPut"/>;
/// a <c>--task</c>/<c>--chat</c>/dispatch pane wants <see cref="FollowPane"/> (the default).
/// </summary>
public enum SplitFocus
{
    /// <summary>Let focus move to the new pane (default; the host's own behaviour, today's).</summary>
    FollowPane,

    /// <summary>Keep focus in the current pane where the host supports it; a no-op where it doesn't.</summary>
    StayPut,
}

/// <summary>
/// Configuration for <see cref="ITerminalLauncher"/>. Intentionally lean for this slice (issue #25):
/// it must work with zero config (all defaults). The full settings surface — preferred terminal,
/// custom <c>claude</c> path/args, working directory, prompt-template — is wired to
/// <c>AppConfig</c> and the F2 dialog in #27 (S4); this record is the seam it will populate.
/// </summary>
public sealed record TerminalLauncherOptions
{
    /// <summary>The <c>claude</c> executable to invoke in the new terminal (looked up on its PATH).</summary>
    public string ClaudeExecutable { get; init; } = "claude";

    /// <summary>Extra arguments inserted before the prompt argument (e.g. a model flag).</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary>Preferred terminal on Windows; ignored on other platforms.</summary>
    public PreferredTerminal Preferred { get; init; } = PreferredTerminal.Auto;

    /// <summary>
    /// A user-configured terminal launch command as a tokenised argv (#385): the emulator executable
    /// followed by its flags, with the first <see cref="TerminalCommandParser.Placeholder"/> token
    /// marking where the OS host invocation of the command is spliced in (appended if absent).
    /// Empty ⇒ no custom command (auto-detection only). When set and its executable is on PATH, the
    /// planner emits it as the first launch candidate on every platform, ahead of the built-in chain;
    /// otherwise it is skipped, so an unset or unavailable command is a strict no-op.
    /// </summary>
    public IReadOnlyList<string> CustomTerminalCommand { get; init; } = [];

    /// <summary>
    /// Where an interactive session opens (#255): a new window (default) or a new tab of the current
    /// terminal where the host supports it. Ignored for one-off runs (which have no terminal).
    /// </summary>
    public LaunchLocation LaunchLocation { get; init; } = LaunchLocation.NewWindow;

    /// <summary>
    /// Which way a <see cref="LaunchLocation.SplitPane"/> divides the current pane (#505):
    /// <see cref="SplitDirection.Auto"/> (default — WT aspect-ratio auto, else side-by-side),
    /// <see cref="SplitDirection.Beside"/> or <see cref="SplitDirection.Below"/>. Ignored unless the
    /// request is a split. The default emits byte-identical argv to the pre-#505 split.
    /// </summary>
    public SplitDirection SplitDirection { get; init; } = SplitDirection.Auto;

    /// <summary>
    /// The <b>new</b> pane's share of the parent for a <see cref="LaunchLocation.SplitPane"/>, as a
    /// percentage (#505), where the host takes a size — Windows Terminal (<c>-s</c> fraction), tmux
    /// (<c>-l %</c>), WezTerm (<c>--percent</c>). Best-effort: kitty, Zellij and iTerm2 split evenly with
    /// no size argument, so this is silently ignored there. <c>null</c> (default) leaves the host's even
    /// split. Clamped to 1–99 when emitted.
    /// </summary>
    public int? SplitSizePercent { get; init; }

    /// <summary>
    /// Whether a <see cref="LaunchLocation.SplitPane"/> keeps focus in the current pane (#505):
    /// <see cref="SplitFocus.FollowPane"/> (default, today's behaviour) or <see cref="SplitFocus.StayPut"/>
    /// (best-effort per host — WT/tmux/kitty only). Ignored unless the request is a split.
    /// </summary>
    public SplitFocus SplitFocus { get; init; } = SplitFocus.FollowPane;

    /// <summary>
    /// The Windows Terminal profile (a <c>guid</c> or <c>name</c>) to launch under when the "Try to use
    /// WT profiles" feature (#462) matched one for this dispatch's resolved directory — passed as
    /// <c>wt … -p &lt;profile&gt;</c> so the session inherits that profile's appearance / environment /
    /// tab title while still running Dispatch's own command. Blank/null ⇒ no profile: the <c>wt</c>
    /// candidates are byte-identical to today. Set per-dispatch (the match depends on the runtime
    /// directory), not by <see cref="Configuration.AgentDispatchSettings.ToLauncherOptions"/>; only the
    /// Windows <c>wt</c> candidate reads it, and never the app-launch (#301) path.
    /// </summary>
    public string? WindowsTerminalProfile { get; init; }
}
