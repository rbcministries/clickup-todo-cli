using System.Text.Json.Serialization;
using ClickUpTodo.Agent;

namespace ClickUpTodo.Configuration;

/// <summary>Where a dispatched <c>claude</c> session starts (issue #27).</summary>
public enum AgentWorkingDirectory
{
    /// <summary>
    /// The default mode: start in the <b>base working directory</b> (#92). The Ctrl+A Dispatch pane
    /// opens with a <b>task-derived pre-fill</b> the user can accept, edit or clear — a
    /// <c>{base}/{Repository}</c> checkout match (#461) else the per-task <c>{base}/{custom-id}</c>
    /// directory (#98), computed by <see cref="Tui.DispatchWorkingDirectoryPreFill"/>. Clearing the
    /// field launches in the plain base dir. Since #533 the derivation lives entirely in the pre-fill,
    /// not in <see cref="Tui.DispatchCoordinator.Plan"/> at launch — this member was formerly named
    /// <c>TaskDerived</c>, which wrongly implied launch-time derivation; the persisted JSON value stays
    /// <c>"TaskDerived"</c> for config compatibility.
    /// </summary>
    [JsonStringEnumMemberName("TaskDerived")]
    BaseWithTaskPrefill,

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

    /// <summary>
    /// A user-specified terminal launch command / template (#385) for launching an emulator not in the
    /// auto-detection matrix, or to prefer a specific emulator on macOS/Linux (where
    /// <see cref="PreferredTerminal"/> doesn't apply). A shell-style command line where a
    /// <c>{}</c> placeholder marks where the launched command is spliced in (appended if omitted) —
    /// e.g. <c>alacritty -e {}</c>, <c>kitty {}</c>, <c>wezterm start -- {}</c>. When set and its
    /// executable is on PATH it is tried first, ahead of the built-in chain; blank ⇒ auto-detection
    /// only. Absent in an old config ⇒ blank.
    /// </summary>
    public string CustomTerminalCommand { get; set; } = "";

    /// <summary>
    /// Where an interactive dispatch opens its session (#255): a new window (default, today's
    /// behavior) or a new tab of the current terminal where the host supports it. Absent in an old
    /// config ⇒ <see cref="LaunchLocation.NewWindow"/>.
    /// </summary>
    public LaunchLocation LaunchLocation { get; set; } = LaunchLocation.NewWindow;

    /// <summary>
    /// On Windows, try to launch a dispatch under the Windows Terminal <b>profile</b> whose
    /// <c>startingDirectory</c> matches the directory the dispatch resolved (#462) — so the session
    /// inherits that profile's font / colour scheme / tab title / environment while still running
    /// Dispatch's own command. Off by default; a strict no-op when off, when not on Windows, or when no
    /// profile matches. The match is computed per-dispatch in <see cref="Tui.DispatchCoordinator.Plan"/>
    /// (not by <see cref="ToLauncherOptions"/>, which is directory-agnostic). Absent in an old config ⇒
    /// off; no migration.
    /// </summary>
    public bool TryUseWindowsTerminalProfiles { get; set; }

    /// <summary>
    /// The configured dispatch providers (#497) — the source of truth for "which agent" a dispatch
    /// targets, generalizing the pre-#497 single <c>claudeExecutable</c>/<c>extraArgs</c> pair into a
    /// named list. Seeded to a single <see cref="DefaultProviderDisplayName"/> provider by
    /// <see cref="ConfigMigrations"/> (v6), which folds a legacy exe/args pair into it. An empty list is
    /// tolerated everywhere (a hand-<c>new</c>'d settings object, or a hand-emptied config): the
    /// projection/resolution fall back to the synthesized built-in <c>claude</c> default.
    /// </summary>
    public List<DispatchProvider> Providers { get; set; } = [];

    /// <summary>
    /// The <see cref="DispatchProvider.Name"/> of the provider a dispatch uses by default. Resolved by
    /// <see cref="ResolveDefaultProvider"/> (falling back to the first provider, then the built-in
    /// default). Blank on a hand-<c>new</c>'d object; set by migration and the F2 editor.
    /// </summary>
    public string DefaultProviderName { get; set; } = "";

    /// <summary>
    /// The <see cref="DispatchProvider.Name"/> of the provider the Dispatch pane's per-dispatch selector
    /// (#498) last launched — the <b>remembered</b> pick, distinct from the F10-configured
    /// <see cref="DefaultProviderName"/>: the pane seeds its provider control from this (falling back to
    /// the configured default), and the host writes the chosen name back here after a dispatch so the
    /// next one opens on it. Blank ⇒ no remembered pick, so the pane opens on
    /// <see cref="DefaultProviderName"/> exactly as before. Absent in an old config ⇒ blank; no
    /// migration. Left out of <see cref="IsDefault"/> because it is blank on a fresh/zero-config object
    /// and is a UI-continuity hint, not a launch-affecting knob.
    /// </summary>
    public string LastDispatchProviderName { get; set; } = "";

    /// <summary>The executable used when a provider's <see cref="DispatchProvider.Executable"/> is blank.</summary>
    public const string DefaultExecutable = "claude";

    /// <summary>The display name of the provider migration seeds from the legacy single-executable keys.</summary>
    public const string DefaultProviderDisplayName = "Claude";

    /// <summary>
    /// Deserialize-only migration shim (#497) for the retired single-executable <c>claudeExecutable</c>
    /// key. A saved value is folded into a single <see cref="Providers"/> entry by
    /// <see cref="ConfigMigrations"/> (v6), which then nulls it so it is never written again (the
    /// <see cref="JsonIgnoreCondition.WhenWritingNull"/> ignore drops it from <c>config.json</c>).
    /// </summary>
    [JsonPropertyName("claudeExecutable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyClaudeExecutable { get; set; }

    /// <summary>
    /// Deserialize-only migration shim (#497) for the retired <c>extraArgs</c> key, folded into the
    /// migrated provider alongside <see cref="LegacyClaudeExecutable"/> and then nulled (see that
    /// property). Nullable so an absent key is distinguishable from an explicit empty list.
    /// </summary>
    [JsonPropertyName("extraArgs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyExtraArgs { get; set; }

    /// <summary>Which directory the new session starts in.</summary>
    public AgentWorkingDirectory WorkingDirectory { get; set; } = AgentWorkingDirectory.BaseWithTaskPrefill;

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
        && string.IsNullOrWhiteSpace(CustomTerminalCommand)
        && LaunchLocation == LaunchLocation.NewWindow
        && ProvidersAreDefault
        && WorkingDirectory == AgentWorkingDirectory.BaseWithTaskPrefill
        && string.IsNullOrWhiteSpace(FixedWorkingDirectory)
        && DefaultSessionMode == AgentSessionMode.Interactive
        && !DefaultPostResultsToComments
        && !TryUseWindowsTerminalProfiles
        && string.IsNullOrWhiteSpace(PromptTemplate);

    /// <summary>
    /// Whether the provider list is the zero-config default: no providers at all (a hand-<c>new</c>'d
    /// object), or a single provider that is the built-in default (a blank/<c>claude</c> executable and
    /// no <b>effective</b> extra args) — the shape migration seeds on a fresh install. This keeps
    /// <see cref="IsDefault"/> true after v6 seeds the default provider, preserving the zero-config
    /// invariant. Blank-only args count as none, matching <see cref="ToLauncherOptions"/> which drops
    /// them — so <see cref="IsDefault"/> tracks the launch, not the raw list.
    /// </summary>
    private bool ProvidersAreDefault =>
        Providers.Count == 0
        || (Providers.Count == 1
            && (string.IsNullOrWhiteSpace(Providers[0].Executable) || Providers[0].Executable == DefaultExecutable)
            && !Providers[0].ExtraArgs.Any(a => !string.IsNullOrWhiteSpace(a)));

    /// <summary>
    /// The provider a dispatch targets by default: the one whose <see cref="DispatchProvider.Name"/>
    /// matches <see cref="DefaultProviderName"/>, else the first configured provider, else a synthesized
    /// built-in <c>claude</c> default when the list is empty. Never returns null, so callers (and a
    /// hand-<c>new</c>'d settings object that never went through migration) always resolve a runnable
    /// provider with zero config. Names are exact selector keys — the match is
    /// <see cref="StringComparison.Ordinal"/>, so a <see cref="DefaultProviderName"/> differing only in
    /// case falls through to the first provider. Treat the result as read-only: it is a live entry from
    /// <see cref="Providers"/> when the list is non-empty, but a throwaway when it is empty.
    /// </summary>
    public DispatchProvider ResolveDefaultProvider()
    {
        if (Providers.Count == 0)
            return new DispatchProvider { Name = DefaultProviderDisplayName, Executable = DefaultExecutable };
        return Providers.FirstOrDefault(p => string.Equals(p.Name, DefaultProviderName, StringComparison.Ordinal))
            ?? Providers[0];
    }

    /// <summary>
    /// The provider a single dispatch targets given the pane's per-dispatch pick (#498): the one whose
    /// <see cref="DispatchProvider.Name"/> equals <paramref name="name"/> (<see cref="StringComparison.Ordinal"/>,
    /// matching <see cref="ResolveDefaultProvider"/>), else the <see cref="ResolveDefaultProvider">configured
    /// default</see>. A blank/null <paramref name="name"/> — a dispatch that never touched the provider
    /// control — resolves to the default, so behaviour is unchanged. An unknown name (e.g. a provider the
    /// user deleted between opening the pane and submitting) also falls back to the default rather than
    /// failing the launch. Never returns null; treat the result as read-only (see
    /// <see cref="ResolveDefaultProvider"/>).
    /// </summary>
    public DispatchProvider ResolveProvider(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ResolveDefaultProvider();
        return Providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal))
            ?? ResolveDefaultProvider();
    }

    /// <summary>
    /// Projects these settings onto the launcher's <see cref="TerminalLauncherOptions"/> from the
    /// <see cref="ResolveDefaultProvider">resolved default provider</see>, coalescing a blank executable
    /// back to the <c>"claude"</c> default and copying the extra args and preference.
    /// </summary>
    public TerminalLauncherOptions ToLauncherOptions() => ToLauncherOptions(ResolveDefaultProvider());

    /// <summary>
    /// Projects these settings onto the launcher's <see cref="TerminalLauncherOptions"/> from a
    /// <paramref name="provider"/> the caller chose (#498) — the pane's per-dispatch pick, or the
    /// <see cref="ResolveDefaultProvider">default</see> for the no-arg overload. Only the provider's
    /// executable/extra-args differ per pick; the terminal/preference/launch-location fields are
    /// provider-agnostic and copied identically either way, so a dispatch that keeps the default
    /// provider produces byte-identical options to the pre-#498 no-arg projection. Blank executable ⇒
    /// <c>"claude"</c>; args trimmed and blanks dropped (the same cleaning the F2 editor's
    /// <c>ParseExtraArgs</c> applies to typed input).
    /// </summary>
    public TerminalLauncherOptions ToLauncherOptions(DispatchProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new()
        {
            ClaudeExecutable = string.IsNullOrWhiteSpace(provider.Executable) ? DefaultExecutable : provider.Executable.Trim(),
            // Trim and drop blanks so hand-edited config.json values are cleaned the same way the F2
            // dialog's ParseExtraArgs cleans typed input (and the executable is coalesced above).
            ExtraArgs = [.. provider.ExtraArgs.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())],
            Preferred = PreferredTerminal,
            CustomTerminalCommand = TerminalCommandParser.Parse(CustomTerminalCommand),
            LaunchLocation = LaunchLocation,
        };
    }

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
