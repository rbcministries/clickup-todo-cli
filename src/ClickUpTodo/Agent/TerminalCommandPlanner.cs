using System.Text;

namespace ClickUpTodo.Agent;

/// <summary>
/// Pure, I/O-free builder for the ordered list of terminal-launch candidates. Given the OS, a way to
/// probe whether an executable exists, the environment, and the inputs, it returns the
/// <see cref="LaunchSpec"/>s to try in order — already filtered to executables that are present, so
/// the launcher just starts each until one succeeds.
///
/// The <c>claude</c> invocation always reads the prompt <b>from the file</b>
/// (<c>Get-Content -Raw</c> on Windows, <c>$(cat …)</c> on POSIX); only the file <b>path</b> ever
/// enters a command string, never the prompt content. Argument vectors are built as arrays.
///
/// The working directory is baked <b>into the command</b> (<c>Set-Location</c> on Windows,
/// <c>cd … &amp;&amp;</c> on POSIX) as well as onto <see cref="LaunchSpec.WorkingDirectory"/>.
/// The spec's directory only takes effect for hosts we start in-process (a direct <c>pwsh</c>); the
/// emulators we prefer — Windows Terminal, gnome-terminal, konsole, Terminal.app via <c>osascript</c>
/// — hand off to a pre-existing server/new login shell that ignores the launcher's cwd and would
/// otherwise open in <c>$HOME</c>. Changing directory inside the command is what actually lands the
/// session in the chosen directory (and lets Claude pick up that project's MCP config).
///
/// <paramref name="oneOff"/> selects the session mode (#94): interactive (default,
/// <c>claude "…"</c>) or a one-off <c>claude -p "…"</c> run that executes and exits. One-off adds the
/// <c>-p</c> flag and, on POSIX/macOS, a keep-alive so the terminal doesn't vanish before the user
/// reads the output (Windows hosts already launch with <c>-NoExit</c>). The full background-run
/// experience is #99; this is the interim terminal path.
/// </summary>
public static class TerminalCommandPlanner
{
    public static IReadOnlyList<LaunchSpec> Plan(
        OSPlatformKind os,
        Func<string, bool> exists,
        Func<string, string?> getEnv,
        string promptFilePath,
        string? workingDir,
        TerminalLauncherOptions options,
        bool oneOff = false)
        => PlanFor(
            os, exists, getEnv,
            new InnerCommand(
                PwshCommand(promptFilePath, workingDir, options, oneOff),
                PosixCommand(promptFilePath, workingDir, options, oneOff),
                workingDir,
                oneOff),
            options);

    /// <summary>
    /// The ordered launch candidates for opening <b>this app</b> in a new terminal tab/window running
    /// <c>clickup-todo --task &lt;id&gt;</c> (#301). Reuses the exact per-OS emulator matrix (and new-tab
    /// detection gates) as <see cref="Plan"/> — only the inner command differs: a plain executable
    /// invocation (no prompt-file indirection, no one-off <c>-p</c>, no keep-alive — the app is a
    /// long-running TUI that owns the new terminal), with no working directory (a single-task tab needs
    /// none). New-tab is honoured per <paramref name="options"/> and stays detection-gated per emulator.
    /// </summary>
    public static IReadOnlyList<LaunchSpec> PlanAppLaunch(
        OSPlatformKind os,
        Func<string, bool> exists,
        Func<string, string?> getEnv,
        AppLaunchCommand command,
        TerminalLauncherOptions options)
    {
        ArgumentNullException.ThrowIfNull(command);
        // A WT-profile match (#462) keys off the dispatch working directory; the "open this app in a new
        // tab" gesture (#301) has none, so profile matching is meaningless here. Strip any profile so the
        // shared PlanWindows never emits `-p` for an app launch, regardless of what the caller passed.
        var appOptions = options.WindowsTerminalProfile is null ? options : options with { WindowsTerminalProfile = null };
        return PlanFor(
            os, exists, getEnv,
            new InnerCommand(PwshAppCommand(command), PosixAppCommand(command), WorkingDir: null, OneOff: false),
            appOptions);
    }

    /// <summary>
    /// The single OS-dispatch matrix shared by both public entry points (#438). Given an
    /// <see cref="InnerCommand"/> — the already-built pwsh and POSIX payloads plus the working directory
    /// and one-off mode that vary per caller — it picks the per-OS emulator builder. Windows runs the
    /// pwsh payload; macOS and Linux run the POSIX one. This is the only place the OS is switched on, so
    /// <see cref="Plan"/> and <see cref="PlanAppLaunch"/> reduce to building their respective payloads.
    /// </summary>
    private static IReadOnlyList<LaunchSpec> PlanFor(
        OSPlatformKind os,
        Func<string, bool> exists,
        Func<string, string?> getEnv,
        in InnerCommand inner,
        TerminalLauncherOptions options) => os switch
        {
            OSPlatformKind.Windows => PlanWindows(exists, getEnv, inner.Pwsh, inner.WorkingDir, options, inner.OneOff),
            OSPlatformKind.MacOS => PlanMacOS(exists, getEnv, inner.Posix, inner.WorkingDir, options, inner.OneOff),
            OSPlatformKind.Linux => PlanLinux(exists, getEnv, inner.Posix, inner.WorkingDir, options, inner.OneOff),
            _ => [],
        };

    /// <summary>
    /// The per-caller inputs to the shared <see cref="PlanFor"/> dispatch: the two OS-specific inner
    /// command payloads (<paramref name="Pwsh"/> for Windows, <paramref name="Posix"/> for macOS/Linux),
    /// the working directory baked onto each <see cref="LaunchSpec"/>, and whether this is a one-off
    /// <c>claude -p</c> run (which gates new-tab). A dispatch (<see cref="Plan"/>) supplies the
    /// file-indirected claude payloads and a real working dir; an app launch (<see cref="PlanAppLaunch"/>)
    /// supplies the plain executable payloads with no working dir and <c>OneOff = false</c>. Both payloads
    /// are built eagerly by the caller — they are pure, I/O-free string construction, so building the one
    /// the target OS won't use is a harmless discarded string.
    /// </summary>
    private readonly record struct InnerCommand(string Pwsh, string Posix, string? WorkingDir, bool OneOff);

    // ── In-place launch locations: new-tab (#255) and split-pane (#502/#504) ─────
    //
    // Both are opt-in, interactive-only, and detection-gated per emulator: a spec is emitted only when
    // the user asked for that location AND an env var proves we're inside a host that supports it.
    // Otherwise the request degrades down the split → tab → window ladder (see the per-OS builders).
    // A one-off `-p` run never gets an in-place location — it runs through the background runner with no
    // terminal, so tab/split are meaningless there.

    // The tab *rung* is wanted for an explicit new-tab request AND for a split request that degrades to a
    // tab — so this widens the old NewTabRequested to include SplitPane. A plain NewTab request is
    // unchanged; NewWindow still emits neither rung.
    private static bool TabRungRequested(TerminalLauncherOptions options, bool oneOff)
        => options.LaunchLocation is LaunchLocation.NewTab or LaunchLocation.SplitPane && !oneOff;

    // The split rung is wanted only for an explicit split request (#504); it sits ahead of the tab rung.
    private static bool SplitRequested(TerminalLauncherOptions options, bool oneOff)
        => options.LaunchLocation == LaunchLocation.SplitPane && !oneOff;

    /// <summary>True if any of <paramref name="vars"/> is set to a non-blank value.</summary>
    private static bool EnvPresent(Func<string, string?> getEnv, params string[] vars)
        => vars.Any(v => !string.IsNullOrWhiteSpace(getEnv(v)));

    // ── User-configured custom launch command (#385) ────────────────────────────
    //
    // An explicit, user-configured emulator/wrapper, tried ahead of the built-in chain on every
    // platform (the explicit-preference-first shape the Windows `Preferred` setting already uses,
    // generalised). It carries no new-tab detection — the user encodes window-vs-tab in their own
    // template (e.g. `gnome-terminal --tab -- {}`). Gated on the executable being present so an unset
    // or unavailable command is a strict no-op and the normal chain runs unchanged.

    /// <summary>
    /// The custom launch candidate (#385) from <see cref="TerminalLauncherOptions.CustomTerminalCommand"/>,
    /// or null when none is set or its executable isn't on PATH. <paramref name="hostArgs"/> is the OS
    /// host invocation of the command to run (POSIX: <c>bash -lc &lt;inner&gt;</c>; Windows:
    /// <c>&lt;host&gt; -NoExit -Command &lt;command&gt;</c>), spliced in at the <b>first</b> <c>{}</c>
    /// placeholder token (any further <c>{}</c> are passed through literally), or appended when the
    /// template has none.
    /// </summary>
    private static LaunchSpec? CustomLaunchSpec(
        Func<string, bool> exists, TerminalLauncherOptions options, IReadOnlyList<string> hostArgs, string? cwd)
    {
        var template = options.CustomTerminalCommand;
        if (template.Count == 0)
            return null;

        var exe = template[0];
        if (string.IsNullOrWhiteSpace(exe) || !exists(exe))
            return null;

        var args = new List<string>();
        var placed = false;
        foreach (var token in template.Skip(1))
        {
            // Splice at the first placeholder only; a stray second `{}` (a malformed template) is a
            // literal token rather than a second, argv-breaking splice.
            if (token == TerminalCommandParser.Placeholder && !placed)
            {
                args.AddRange(hostArgs);
                placed = true;
            }
            else
            {
                args.Add(token);
            }
        }
        if (!placed)
            args.AddRange(hostArgs);

        return new LaunchSpec(exe, args, cwd, $"{exe} (configured)");
    }

    // ── Windows: Windows Terminal → pwsh → powershell → cmd, all running the same pwsh command ──
    //
    // The first three launch the PowerShell host directly. The `cmd` last resort (#45) opens a new
    // window via `cmd /c start "" <host> …` and carries the payload as `-EncodedCommand <base64>`:
    // Base64 is [A-Za-z0-9+/=] only, so the `&`/parenthesis command survives cmd.exe's tokenizer
    // intact (that mis-tokenization is exactly why PR #42 first omitted the cmd path). `-EncodedCommand`
    // needs a PowerShell host, so the cmd candidate is gated on one being present — cmd alone can't run
    // the file-reading `claude` invocation. In practice powershell.exe is always in-box, so cmd sits
    // last and is reached only if the direct launches fail to start, or when it's explicitly preferred.

    private static IReadOnlyList<LaunchSpec> PlanWindows(
        Func<string, bool> exists, Func<string, string?> getEnv, string command, string? cwd, TerminalLauncherOptions options, bool oneOff)
    {
        // <paramref name="command"/> is the pwsh command to run in the host — built by the caller
        // (`Set-Location …; & 'claude' [-p] … (Get-Content -Raw '<file>')` for a dispatch, or
        // `& 'clickup-todo' '--task' '<id>'` for an app launch).

        // Windows Terminal is the only Windows host with a tab notion: `wt -w 0 new-tab` targets the
        // current window (vs. today's `wt new-tab`, which opens a new window). Gated on WT_SESSION so
        // we only do it when we're actually running inside Windows Terminal. The rung is also wanted when
        // a split was requested (it degrades split → tab → window).
        var wtTab = TabRungRequested(options, oneOff) && EnvPresent(getEnv, "WT_SESSION");

        // WT is also the only Windows host with a split notion: `wt -w 0 sp` splits the current pane
        // (#502/#504). Gated on WT_SESSION + `wt` present; the split rung sits ahead of the tab/window
        // chain. Geometry (`-V`/`-s`) is slice C — B emits the minimal split. Reuses WtArgs so the
        // profile (#462) and the `;`-delimiter escaping (#534) are applied exactly as for the tab spec.
        var wtSplit = SplitRequested(options, oneOff) && exists("wt") && EnvPresent(getEnv, "WT_SESSION")
            ? new LaunchSpec(
                "wt", WtArgs(["-w", "0", "sp"], options.WindowsTerminalProfile, command), cwd, "Windows Terminal (split pane)")
            : null;

        // Candidate builders keyed by the terminal they represent, in default fallback order.
        var order = new[]
        {
            PreferredTerminal.WindowsTerminal,
            PreferredTerminal.Pwsh,
            PreferredTerminal.PowerShell,
            PreferredTerminal.Cmd,
        };

        // Honor an explicit preference by moving it to the front of the chain (fallback preserved).
        IEnumerable<PreferredTerminal> chain = options.Preferred == PreferredTerminal.Auto
            ? order
            : new[] { options.Preferred }.Concat(order.Where(t => t != options.Preferred));

        var specs = new List<LaunchSpec>();

        // A user-configured custom command (#385) runs the pwsh payload inside its own emulator, so it
        // needs a PowerShell host; skipped when neither pwsh nor powershell is present. Tried first.
        if (PwshHost(exists) is { } customHost
            && CustomLaunchSpec(exists, options, [customHost, "-NoExit", "-Command", command], cwd) is { } custom)
            specs.Add(custom);

        // The split rung, if requested and we're inside WT — ahead of the tab/window chain.
        if (wtSplit is not null)
            specs.Add(wtSplit);

        foreach (var terminal in chain)
        {
            var spec = terminal switch
            {
                PreferredTerminal.WindowsTerminal when exists("wt") => wtTab
                    ? new LaunchSpec(
                        "wt", WtArgs(["-w", "0", "new-tab"], options.WindowsTerminalProfile, command), cwd, "Windows Terminal (new tab)")
                    : new LaunchSpec(
                        "wt", WtArgs(["new-tab"], options.WindowsTerminalProfile, command), cwd, "Windows Terminal"),
                PreferredTerminal.Pwsh when exists("pwsh") => new LaunchSpec(
                    "pwsh", ["-NoExit", "-Command", command], cwd, "PowerShell (pwsh)"),
                PreferredTerminal.PowerShell when exists("powershell") => new LaunchSpec(
                    "powershell", ["-NoExit", "-Command", command], cwd, "Windows PowerShell"),
                PreferredTerminal.Cmd when exists("cmd") && PwshHost(exists) is { } host => new LaunchSpec(
                    "cmd",
                    ["/c", "start", "", host, "-NoExit", "-EncodedCommand", EncodePwshCommand(command)],
                    cwd,
                    "Command Prompt (cmd)"),
                _ => null,
            };
            if (spec is not null)
                specs.Add(spec);
        }
        return specs;
    }

    /// <summary>
    /// The <c>wt</c> subcommand argv for a dispatch launch: the subcommand <paramref name="prefix"/>
    /// (<c>new-tab</c>, or <c>-w 0 new-tab</c> for a current-window tab), an optional
    /// <c>-p &lt;profile&gt;</c> (#462) when a Windows Terminal profile matched this dispatch's directory,
    /// then the trailing <c>pwsh -NoExit -Command &lt;command&gt;</c> that WT runs <b>instead of</b> the
    /// profile's own commandline. A blank/null <paramref name="profile"/> omits <c>-p</c> entirely, so
    /// the argv is byte-identical to the pre-#462 launch.
    ///
    /// Every emitted argument is run through <see cref="EscapeWtDelimiter"/> (#534): <c>;</c> is Windows
    /// Terminal's own subcommand delimiter and WT splits on it <b>inside</b> arguments (quoting doesn't
    /// protect it), so an unescaped <c>;</c> — from the <c>Set-Location …;</c> working-directory prefix,
    /// or from a <c>;</c> in <c>ClaudeExecutable</c>/<c>ExtraArgs</c>/a matched profile name — would tear
    /// the command in two and open a bogus second tab. WT's documented escape is <c>\;</c>, which it
    /// unescapes before handing the commandline to the profile.
    /// </summary>
    private static string[] WtArgs(IReadOnlyList<string> prefix, string? profile, string command)
    {
        string[] args = string.IsNullOrWhiteSpace(profile)
            ? [.. prefix, "pwsh", "-NoExit", "-Command", command]
            : [.. prefix, "-p", profile, "pwsh", "-NoExit", "-Command", command];
        for (var i = 0; i < args.Length; i++)
            args[i] = EscapeWtDelimiter(args[i]);
        return args;
    }

    /// <summary>
    /// Escape Windows Terminal's subcommand delimiter in a single <c>wt</c> argument: every <c>;</c>
    /// becomes <c>\;</c> (WT's documented escape). A no-op on arguments with no <c>;</c> — including the
    /// structural WT tokens (<c>new-tab</c>, <c>-w</c>, <c>-p</c>, …), which never contain one — so it is
    /// safe to apply to every emitted argument. Only <c>;</c> is touched: backslashes in Windows paths are
    /// left alone, as WT does not treat <c>\</c> as a general escape elsewhere.
    /// </summary>
    private static string EscapeWtDelimiter(string arg) => arg.Replace(";", "\\;");

    /// <summary>The PowerShell host to run inside the cmd window — pwsh preferred, else powershell; null if neither.</summary>
    private static string? PwshHost(Func<string, bool> exists) =>
        exists("pwsh") ? "pwsh" : exists("powershell") ? "powershell" : null;

    // ── macOS: osascript drives Terminal to run the bash command ──

    private static IReadOnlyList<LaunchSpec> PlanMacOS(
        Func<string, bool> exists, Func<string, string?> getEnv, string inner, string? cwd, TerminalLauncherOptions options, bool oneOff)
    {
        // <paramref name="inner"/> is the POSIX shell command to run — `cd …; 'claude' [-p] …
        // "$(cat '<file>')"` for a dispatch, or `'clickup-todo' '--task' '<id>'` for an app launch.
        var specs = new List<LaunchSpec>();

        // A user-configured custom emulator (#385) runs `bash -lc <inner>` directly — no osascript
        // needed — so it's emitted first and even when Terminal.app scripting is unavailable.
        if (CustomLaunchSpec(exists, options, ["bash", "-lc", inner], cwd) is { } custom)
            specs.Add(custom);

        if (!exists("osascript"))
            return specs;

        // Terminal.app new-window path (the historical default) — `do script` only ever makes windows,
        // so it stays window-only and doubles as the fallback for the iTerm tab path below.
        var windowScript = $"tell application \"Terminal\" to do script \"{AppleScriptEscape(inner)}\"";
        var windowSpec = new LaunchSpec("osascript", ["-e", windowScript], cwd, "Terminal (osascript)");

        // iTerm2 also has a real split-scripting API (#502/#504). When a split was requested and
        // TERM_PROGRAM says we're inside iTerm, split the current session and write the command into the
        // new pane. The split rung sits ahead of the tab rung, which sits ahead of the window fallback.
        if (SplitRequested(options, oneOff) && getEnv("TERM_PROGRAM") == "iTerm.app")
        {
            var escaped = AppleScriptEscape(inner);
            specs.Add(new LaunchSpec(
                "osascript",
                [
                    "-e", "tell application \"iTerm\"",
                    "-e", "tell current session of current window",
                    "-e", "set newSession to (split vertically with default profile)",
                    "-e", "end tell",
                    "-e", $"tell newSession to write text \"{escaped}\"",
                    "-e", "end tell",
                ],
                cwd,
                "iTerm2 (split pane)"));
        }

        // iTerm2 has a real tab-scripting API. When the user asked for a tab (or a split degrading to
        // one) and TERM_PROGRAM says we're inside iTerm, open a tab in the current window and run the
        // command there, keeping the Terminal.app window spec after it as the fallback (macOS has no
        // cross-emulator chain).
        if (TabRungRequested(options, oneOff) && getEnv("TERM_PROGRAM") == "iTerm.app")
        {
            var escaped = AppleScriptEscape(inner);
            specs.Add(new LaunchSpec(
                "osascript",
                [
                    "-e", "tell application \"iTerm\"",
                    "-e", "tell current window",
                    "-e", "create tab with default profile",
                    "-e", $"tell current session to write text \"{escaped}\"",
                    "-e", "end tell",
                    "-e", "end tell",
                ],
                cwd,
                "iTerm2 (new tab)"));
        }

        specs.Add(windowSpec);
        return specs;
    }

    // ── Linux: honor $TERMINAL, else probe common emulators ──

    private static IReadOnlyList<LaunchSpec> PlanLinux(
        Func<string, bool> exists, Func<string, string?> getEnv, string inner, string? cwd, TerminalLauncherOptions options, bool oneOff)
    {
        // <paramref name="inner"/> is the POSIX shell command run via `bash -lc` (or tmux) — built by
        // the caller (a `cd …; 'claude' …` dispatch, or an `'clickup-todo' '--task' '<id>'` app launch).
        var tab = TabRungRequested(options, oneOff);

        // The split rung (#502/#504): the in-place hosts with a scriptable split — tmux, WezTerm, kitty
        // and Zellij — each gated on the env var proving we're inside it AND its executable being present,
        // emitted only for an explicit SplitPane request. These sit *ahead* of the tab/window specs so the
        // ladder degrades split → tab → window. Geometry (`-l %` / `--percent`) is slice C; B emits the
        // minimal split. Ordered per #504's host table (tmux, WezTerm, kitty, Zellij); in practice these
        // env vars are mutually exclusive, so order only matters under a nested multiplexer.
        var splitSpecs = new List<LaunchSpec>();
        if (SplitRequested(options, oneOff))
        {
            // tmux stops option parsing at `bash` (a non-option), so `-lc` reaches the shell intact —
            // the same reason the tmux new-window spec passes `bash -lc <inner>` bare.
            if (EnvPresent(getEnv, "TMUX") && exists("tmux"))
                splitSpecs.Add(new LaunchSpec("tmux", ["split-window", "-h", "bash", "-lc", inner], cwd, "tmux (split pane)"));
            if (EnvPresent(getEnv, "WEZTERM_PANE") && exists("wezterm"))
                splitSpecs.Add(new LaunchSpec(
                    "wezterm", ["cli", "split-pane", "--right", "--", "bash", "-lc", inner], cwd, "WezTerm (split pane)"));
            // kitty's gate is KITTY_LISTEN_ON — only set when `allow_remote_control` is enabled — so it
            // probes the actual capability, not merely that kitty is running. The split runs through the
            // `kitten` binary (`kitten @ launch`), so its presence is the exe gate.
            if (EnvPresent(getEnv, "KITTY_LISTEN_ON") && exists("kitten"))
                splitSpecs.Add(new LaunchSpec(
                    "kitten", ["@", "launch", "--location=vsplit", "--cwd=current", "bash", "-lc", inner], cwd, "kitty (split pane)"));
            if (EnvPresent(getEnv, "ZELLIJ") && exists("zellij"))
                splitSpecs.Add(new LaunchSpec(
                    "zellij", ["action", "new-pane", "-d", "right", "--", "bash", "-lc", inner], cwd, "Zellij (split pane)"));
        }

        // A user-configured custom emulator/wrapper (#385) is tried first, ahead of $TERMINAL, the probe
        // list and tmux — an explicit preference beats auto-detection. It runs `bash -lc <inner>` like
        // every other Linux candidate; the user encodes any tab flag in their own template.
        var custom = CustomLaunchSpec(exists, options, ["bash", "-lc", inner], cwd);

        // Tab specs are collected separately and returned *ahead* of the window specs. The launcher
        // starts candidates in order and stops at the first that starts, and a valid emulator always
        // starts — so a generic window spec ordered first (x-terminal-emulator, which on Debian/Ubuntu
        // is an update-alternatives symlink present whenever gnome-terminal/konsole is; or an explicit
        // $TERMINAL) would silently preempt a detected-emulator tab and defeat the opt-in. Keeping the
        // window specs as the fallback chain after the tab spec fixes that.
        var tabSpecs = new List<LaunchSpec>();
        var windowSpecs = new List<LaunchSpec>();

        // The argv that makes an emulator run our `bash -lc <inner>` command — its per-emulator prefix
        // (see ExecPrefix) followed by the shell invocation.
        string[] WindowArgs(string name) => [.. ExecPrefix(name), "bash", "-lc", inner];

        // Emulators for which a *window* spec has been emitted, so an explicit $TERMINAL that also
        // appears in the probe list below doesn't add a duplicate window. A detected tab spec is
        // distinct (and more specific) and is never suppressed by this.
        var windowAdded = new HashSet<string>(StringComparer.Ordinal);

        // An explicit $TERMINAL stays window-only: it's an arbitrary emulator with no portable tab flag.
        var configured = getEnv("TERMINAL");
        if (!string.IsNullOrWhiteSpace(configured) && exists(configured))
        {
            windowSpecs.Add(new LaunchSpec(configured, WindowArgs(configured), cwd, configured));
            windowAdded.Add(configured);
        }

        foreach (var name in LinuxEmulators)
        {
            if (!exists(name))
                continue;

            // gnome-terminal (shared server → `--tab` lands in the current window) and konsole
            // (`--new-tab`) can open a tab in the running instance when we detect we're inside them —
            // that tab spec is always kept, even when $TERMINAL already added a window for the same
            // emulator. Every other emulator has no portable in-place tab flag, so it stays window-only,
            // and its window is emitted once (windowAdded dedupes a $TERMINAL that names it).
            if (tab && name == "gnome-terminal" && EnvPresent(getEnv, "GNOME_TERMINAL_SCREEN", "VTE_VERSION"))
                tabSpecs.Add(new LaunchSpec(name, ["--tab", "--", "bash", "-lc", inner], cwd, "gnome-terminal (new tab)"));
            else if (tab && name == "konsole" && EnvPresent(getEnv, "KONSOLE_VERSION"))
                tabSpecs.Add(new LaunchSpec(name, ["--new-tab", "-e", "bash", "-lc", inner], cwd, "konsole (new tab)"));
            else if (windowAdded.Add(name))
                windowSpecs.Add(new LaunchSpec(name, WindowArgs(name), cwd, name));
        }

        // Multiplexer path: inside a tmux session `tmux new-window` opens a new window in the current
        // session — the multiplexer analog of a tab, and the only path that works in a headless
        // tmux-over-SSH session with no GUI emulator on PATH (tmux stops option parsing at `bash`, so
        // the `-lc` reaches the shell intact). When a tab was asked for it joins the tab specs (after a
        // detected GUI-emulator tab); otherwise it's appended as the last-resort window fallback so a
        // local GUI window is still preferred when one is available.
        if (EnvPresent(getEnv, "TMUX") && exists("tmux"))
        {
            var tmuxSpec = new LaunchSpec("tmux", ["new-window", "bash", "-lc", inner], cwd, "tmux (new window)");
            if (tab)
                tabSpecs.Add(tmuxSpec);
            else
                windowSpecs.Add(tmuxSpec);
        }

        return custom is null
            ? [.. splitSpecs, .. tabSpecs, .. windowSpecs]
            : [custom, .. splitSpecs, .. tabSpecs, .. windowSpecs];
    }

    /// <summary>
    /// Linux terminal emulators probed in fallback order: the Debian generic alias first, then the
    /// tab-capable VTE/KDE emulators, then common modern emulators, with <c>xterm</c> as the
    /// near-universal lowest-common-denominator and <c>terminator</c> last.
    /// </summary>
    private static readonly string[] LinuxEmulators =
    [
        "x-terminal-emulator", "gnome-terminal", "konsole", "xfce4-terminal",
        "alacritty", "kitty", "wezterm", "foot", "xterm", "terminator",
    ];

    /// <summary>
    /// The token(s) that make a Linux terminal run a command vector, inserted between the emulator and
    /// <c>bash -lc &lt;inner&gt;</c>. Syntax differs per emulator: gnome-terminal dropped <c>-e</c> for
    /// <c>--</c>; xfce4-terminal and terminator use <c>-x</c>; kitty and foot take the command as bare
    /// positional args (no flag); wezterm needs its <c>start --</c> subcommand; everything else (and an
    /// unknown <c>$TERMINAL</c>) takes <c>-e</c>.
    /// </summary>
    private static string[] ExecPrefix(string terminal) => terminal switch
    {
        "gnome-terminal" => ["--"],
        "xfce4-terminal" or "terminator" => ["-x"],
        "kitty" or "foot" => [],
        "wezterm" => ["start", "--"],
        _ => ["-e"],
    };

    // ── Command construction (file-indirected; prompt content never inlined) ──

    /// <summary>
    /// PowerShell command that runs claude with the prompt read from the file via Get-Content -Raw.
    /// One-off (#94) inserts <c>-p</c> right after the executable; the PowerShell hosts already launch
    /// with <c>-NoExit</c>, so the window stays open after a one-off run finishes. A non-blank
    /// <paramref name="cwd"/> is prepended as a <c>Set-Location -LiteralPath … -ErrorAction Stop;</c>
    /// so the session starts there even when the host (e.g. a <c>wt</c> tab) ignores the process
    /// working directory, and a failed directory change aborts before claude runs.
    /// </summary>
    private static string PwshCommand(string file, string? cwd, TerminalLauncherOptions options, bool oneOff)
    {
        var parts = new List<string> { "&", PwshQuote(options.ClaudeExecutable) };
        if (oneOff)
            parts.Add(PwshQuote("-p"));
        parts.AddRange(options.ExtraArgs.Select(PwshQuote));
        parts.Add($"(Get-Content -Raw {PwshQuote(file)})");
        var command = string.Join(" ", parts);
        // -ErrorAction Stop makes a failed directory change terminating, so claude never runs in the
        // wrong directory — parity with the POSIX `&&` guard. -NoExit keeps the window open on the error.
        return string.IsNullOrWhiteSpace(cwd)
            ? command
            : $"Set-Location -LiteralPath {PwshQuote(cwd)} -ErrorAction Stop; {command}";
    }

    /// <summary>
    /// POSIX shell command that runs claude with the prompt read from the file via $(cat …). One-off
    /// (#94) inserts <c>-p</c> right after the executable and appends a keep-alive so the terminal
    /// (Linux <c>bash -lc</c> / macOS <c>do script</c>) doesn't close before the user reads the
    /// output — the interim terminal path until the background-run experience (#99) lands. A non-blank
    /// <paramref name="cwd"/> is prepended as <c>cd '<dir>' &amp;&amp;</c> so the session starts there
    /// even when the emulator opens its new shell in <c>$HOME</c> rather than the launcher's cwd;
    /// the <c>&amp;&amp;</c> means claude only runs once the directory change succeeds, while a one-off's
    /// keep-alive is joined with <c>;</c> so the window still stays open to show a <c>cd</c> failure.
    /// </summary>
    private static string PosixCommand(string file, string? cwd, TerminalLauncherOptions options, bool oneOff)
    {
        var parts = new List<string> { PosixQuote(options.ClaudeExecutable) };
        if (oneOff)
            parts.Add("-p");
        parts.AddRange(options.ExtraArgs.Select(PosixQuote));
        parts.Add($"\"$(cat {PosixQuote(file)})\"");
        var command = string.Join(" ", parts);
        if (!string.IsNullOrWhiteSpace(cwd))
            command = $"cd {PosixQuote(cwd)} && {command}";
        return oneOff ? command + PosixKeepAlive : command;
    }

    /// <summary>
    /// PowerShell command that runs the app for a single-task launch (#301): <c>&amp; 'clickup-todo'
    /// '--task' '&lt;id&gt;'</c>. No prompt-file indirection, no one-off flag, no keep-alive, no working
    /// directory — the app is a long-running TUI that takes over the new terminal. The host still
    /// launches with <c>-NoExit</c>, so the pwsh prompt stays after the app quits.
    /// </summary>
    private static string PwshAppCommand(AppLaunchCommand command)
        => string.Join(" ", new[] { "&", PwshQuote(command.FileName) }.Concat(command.Arguments.Select(PwshQuote)));

    /// <summary>
    /// POSIX shell command that runs the app for a single-task launch (#301): <c>'clickup-todo' '--task'
    /// '&lt;id&gt;'</c>, run via <c>bash -lc</c> (or the emulator equivalent). No <c>$(cat …)</c>, no
    /// one-off <c>-p</c>, and no keep-alive — when the TUI exits the shell exits and the tab closes,
    /// which is the natural lifetime for a single-task tab.
    /// </summary>
    private static string PosixAppCommand(AppLaunchCommand command)
        => string.Join(" ", new[] { command.FileName }.Concat(command.Arguments).Select(PosixQuote));

    /// <summary>
    /// Appended after a one-off POSIX/macOS <c>claude -p</c> run so the terminal stays open until the
    /// user dismisses it (otherwise the shell exits and the window vanishes with the output). No prompt
    /// content enters here — it stays file-indirected.
    /// </summary>
    private const string PosixKeepAlive =
        "; printf '\\n[claude -p finished - press Enter to close] '; read -r _";

    // ── Escaping helpers ──

    /// <summary>Single-quote for PowerShell: literal text, embedded <c>'</c> doubled.</summary>
    private static string PwshQuote(string s) => $"'{s.Replace("'", "''")}'";

    /// <summary>Single-quote for POSIX shells: literal text, embedded <c>'</c> → <c>'\''</c>.</summary>
    private static string PosixQuote(string s) => $"'{s.Replace("'", "'\\''")}'";

    /// <summary>
    /// Encode a PowerShell command for <c>-EncodedCommand</c>: Base64 of the UTF-16LE bytes. The
    /// result is <c>[A-Za-z0-9+/=]</c> only, so it survives cmd.exe's parser (and <c>start</c>) intact.
    /// </summary>
    private static string EncodePwshCommand(string command) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

    /// <summary>Escape for an AppleScript double-quoted string literal.</summary>
    private static string AppleScriptEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
