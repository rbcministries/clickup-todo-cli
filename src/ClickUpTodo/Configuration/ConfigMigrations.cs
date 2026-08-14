using ClickUpTodo.Agent;

namespace ClickUpTodo.Configuration;

/// <summary>
/// One-shot, forward-only migrations applied to <see cref="AppConfig"/> as it's loaded, gated by
/// <see cref="AppConfig.SchemaVersion"/> so each runs exactly once. Pure (no I/O) so it's unit-testable
/// and callable from <see cref="ConfigStore.Load"/>.
/// </summary>
public static class ConfigMigrations
{
    /// <summary>The version an up-to-date config carries once all migrations have run.</summary>
    public const int CurrentVersion = 6;

    /// <summary>Applies any migrations the config hasn't seen yet, then stamps it current.</summary>
    public static void Apply(AppConfig config)
    {
        // Normalize a null sub-object from a hand-edited/corrupted config.json ("AgentDispatch": null):
        // the property's `= new()` default only fills a *missing* key, not an explicit null. Dispatch
        // settings are now read at startup (#91), so a null here would fault the whole launch, not just
        // the F2 dialog — coalesce it back to defaults so a bad key degrades to zero-config, not a crash.
        config.AgentDispatch ??= new AgentDispatchSettings();

        // Same guard for the per-task working-dir cache (#96): a hand-edited
        // "taskWorkingDirectories": null would defeat the `= []` default and NRE the pre-fill/update
        // call sites, so coalesce it back to an empty map.
        config.TaskWorkingDirectories ??= [];

        // Same guard for the Super Agents seed (#494): a hand-edited "superAgents": null would defeat the
        // `= new()` default, so the registry's seed read would NRE. Coalesce it back to defaults. No
        // version bump — there is no legacy data to fold, only a null to normalize (like the two above).
        config.SuperAgents ??= new SuperAgentSettings();

        // Same guard for the launch-chord overrides (#506): a hand-edited "launchChords": null would defeat
        // the `= new()` default, so LaunchChordOverrides.FromConfig's read would NRE. Coalesce it back to
        // defaults (both chords null ⇒ shipped Ctrl+Enter/Ctrl+Alt+Enter). No version bump — there is no
        // legacy data to fold, only a null to normalize (like the guards above).
        config.LaunchChords ??= new LaunchChordSettings();

        // Widening the persisted LaunchLocation enum to three values (#508, split pane): an out-of-range
        // value — a future ordinal, or a hand-edited "launchLocation": 99 — deserializes through
        // JsonStringEnumConverter to an undefined (LaunchLocation)N without throwing, so clamp it back to
        // the NewWindow default rather than let a bogus destination flow to the launcher. Unconditional
        // (not version-gated) like the null-guards above, since it's a normalization, not a one-shot fold.
        if (!Enum.IsDefined(config.AgentDispatch.LaunchLocation))
            config.AgentDispatch.LaunchLocation = LaunchLocation.NewWindow;

        // v1 (#68): assignee became a first-class filter field. Seed the default "Assignee IS me" rule
        // so an existing/blank view keeps reproducing the original "my tasks" fetch. Version-gated (not
        // "seed whenever absent") so a user who deliberately clears the assignee rule to see everyone
        // isn't re-seeded on the next load.
        if (config.SchemaVersion < 1)
            SeedDefaultAssigneeRule(config.View);

        // v2 (#69): the standalone excluded-statuses setting became ordinary "Status IS NOT" filter
        // rules. Migrate any saved exclusions (or seed the defaults on a fresh install) into the view,
        // then drop the legacy shim so it's never written again.
        if (config.SchemaVersion < 2)
            MigrateStatusExclusions(config);

        // v3 (#100): the single-line PromptPreamble override was superseded by a full, editable
        // PromptTemplate. Carry a saved non-blank preamble forward — it was live at dispatch (#91), so
        // dropping it would silently change a user's prompt — by seeding the equivalent full template
        // (the default with its preamble line swapped). Version-gated so a user who later clears their
        // template isn't re-seeded.
        if (config.SchemaVersion < 3)
            MigratePromptPreamble(config.AgentDispatch);

        // The preamble shim is deserialize-only: null it regardless of version so a stray promptPreamble
        // key (e.g. hand-added to an already-v3 config) is dropped rather than re-persisted forever.
        config.AgentDispatch.LegacyPromptPreamble = null;

        // v4 (#179): the ShowSubtasks + ShowAllSubtasksOfAssignedParents boolean pair became the
        // three-state SubtaskView (F4 cycle). Fold a saved pair onto the enum so an existing user's
        // subtasks view is preserved; version-gated so a user who later returns to Hidden isn't re-seeded.
        if (config.SchemaVersion < 4)
            MigrateSubtaskView(config.View);

        // The subtask boolean shims are deserialize-only: null them regardless of version so stray keys
        // (e.g. hand-added to an already-v4 config) are dropped rather than re-persisted forever.
        config.View.LegacyShowSubtasks = null;
        config.View.LegacyShowAllSubtasks = null;

        // v5 (#191): the ShowCompleted boolean (#178) became the three-state CompletedView (F12 cycle).
        // Fold a saved value onto the enum so an existing user's completed view is preserved;
        // version-gated so a user who later returns to Active isn't re-seeded.
        if (config.SchemaVersion < 5)
            MigrateCompletedView(config.View);

        // The ShowCompleted shim is deserialize-only: null it regardless of version so a stray
        // showCompleted key (e.g. hand-added to an already-v5 config) is dropped rather than re-persisted.
        config.View.LegacyShowCompleted = null;

        // v6 (#497): the single hard-wired claudeExecutable/extraArgs pair became a list of
        // DispatchProviders + a chosen default. Fold the legacy pair into a single provider so an
        // existing config dispatches byte-identically; version-gated so a user who later edits their
        // provider list isn't re-seeded.
        if (config.SchemaVersion < 6)
            MigrateDispatchProviders(config.AgentDispatch);

        // The dispatch legacy shims are deserialize-only: null them regardless of version so stray
        // claudeExecutable/extraArgs keys (e.g. hand-added to an already-v6 config) are dropped rather
        // than re-persisted forever.
        config.AgentDispatch.LegacyClaudeExecutable = null;
        config.AgentDispatch.LegacyExtraArgs = null;

        config.SchemaVersion = CurrentVersion;
    }

    /// <summary>
    /// Maps a legacy boolean subtask pair onto <see cref="ViewSettings.Subtasks"/> (#179): a saved
    /// <c>showSubtasks == true</c> becomes <see cref="SubtaskView.All"/> when the #70
    /// <c>showAllSubtasksOfAssignedParents</c> was also set, otherwise <see cref="SubtaskView.MineAndUnassigned"/>
    /// (the new default on-state). An absent or false <c>showSubtasks</c> means Hidden — the enum's default —
    /// so nothing is written. Only the legacy bools are consulted; the shims are nulled by the caller.
    /// </summary>
    private static void MigrateSubtaskView(ViewSettings view)
    {
        if (view.LegacyShowSubtasks == true)
            view.Subtasks = view.LegacyShowAllSubtasks == true
                ? SubtaskView.All
                : SubtaskView.MineAndUnassigned;
    }

    /// <summary>
    /// Maps the legacy boolean "Show Completed" (#178) onto <see cref="ViewSettings.Completed"/> (#191).
    /// The pre-tri-state toggle off (the saved default) hid only <c>closed</c>-type and left
    /// <c>done</c>-type visible — exactly <see cref="CompletedView.WithDone"/>; on showed everything —
    /// <see cref="CompletedView.All"/>. Only the boolean's presence drives the mapping: a saved
    /// <c>false</c> (written by any post-#178 run) preserves that user's done-visible view, and a saved
    /// <c>true</c> preserves the show-all view. An <b>absent</b> value (a fresh install, or a config that
    /// never carried the key) leaves the enum at its <see cref="CompletedView.Active"/> default — the new
    /// default that hides done + closed. The shim is nulled by the caller.
    /// </summary>
    private static void MigrateCompletedView(ViewSettings view)
    {
        if (view.LegacyShowCompleted is { } legacy)
            view.Completed = legacy ? CompletedView.All : CompletedView.WithDone;
    }

    /// <summary>
    /// Folds the legacy single-executable dispatch keys (#497) into a single <see cref="DispatchProvider"/>.
    /// A config that already carries providers (a hand-authored or future config) is left untouched. A
    /// blank/absent legacy executable coalesces to <see cref="AgentDispatchSettings.DefaultExecutable"/>,
    /// and legacy args are trimmed with blanks dropped — so a fresh install seeds
    /// <c>{ "Claude", "claude", [] }</c> and an existing config produces a provider equal to its old
    /// exe/args pair, keeping <see cref="AgentDispatchSettings.ToLauncherOptions"/> byte-identical. The
    /// shims are nulled by the caller.
    /// </summary>
    private static void MigrateDispatchProviders(AgentDispatchSettings dispatch)
    {
        if (dispatch.Providers.Count > 0)
            return;

        var exe = string.IsNullOrWhiteSpace(dispatch.LegacyClaudeExecutable)
            ? AgentDispatchSettings.DefaultExecutable
            : dispatch.LegacyClaudeExecutable.Trim();
        var args = dispatch.LegacyExtraArgs is { } legacy
            ? legacy.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList()
            : [];

        dispatch.Providers.Add(new DispatchProvider
        {
            Name = AgentDispatchSettings.DefaultProviderDisplayName,
            Executable = exe,
            ExtraArgs = args,
            Kind = DispatchProviderKind.LocalCli,
        });
        dispatch.DefaultProviderName = AgentDispatchSettings.DefaultProviderDisplayName;
    }

    private static void MigratePromptPreamble(AgentDispatchSettings dispatch)
    {
        var legacy = dispatch.LegacyPromptPreamble;
        if (!string.IsNullOrWhiteSpace(legacy) && string.IsNullOrWhiteSpace(dispatch.PromptTemplate))
            dispatch.PromptTemplate = AgentPromptComposer.DefaultTemplateWithPreamble(legacy);
    }

    private static void SeedDefaultAssigneeRule(ViewSettings view)
    {
        if (!view.Filters.Any(r => r.Field == TaskField.Assignee))
            view.Filters.Insert(0, ViewSettings.DefaultAssigneeRule());
    }

    /// <summary>
    /// Converts the legacy <c>excludedStatuses</c> array into <c>Status IS NOT</c> filter rules. The
    /// legacy value being <b>absent</b> (null) means a fresh install (or a config that never carried
    /// the key), so the default exclusions are seeded to preserve today's behaviour; an <b>empty</b>
    /// list means the user cleared their exclusions, so nothing is seeded; a <b>non-empty</b> list is
    /// migrated entry-for-entry. Each rule is added only when not already covered (case-insensitive),
    /// so re-running — or a config that already has the matching rule — never duplicates. The shim is
    /// then nulled so the migration is one-shot and <c>excludedStatuses</c> stops being persisted.
    /// </summary>
    private static void MigrateStatusExclusions(AppConfig config)
    {
        var toExclude = config.LegacyExcludedStatuses ?? ViewSettings.DefaultExcludedStatuses;
        foreach (var status in toExclude)
            AddStatusIsNotRuleIfAbsent(config.View, status);
        config.LegacyExcludedStatuses = null;
    }

    private static void AddStatusIsNotRuleIfAbsent(ViewSettings view, string status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return;
        // Trim first, then compare and insert the same trimmed value — otherwise a whitespace variant
        // (e.g. "won't do" then "  won't do  ") would pass the covered-check and add a duplicate rule.
        var trimmed = status.Trim();
        var covered = view.Filters.Any(r =>
            r.Field == TaskField.Status && r.Op == FilterOp.IsNot
            && string.Equals(r.Value, trimmed, StringComparison.OrdinalIgnoreCase));
        if (!covered)
            view.Filters.Add(ViewSettings.StatusIsNotRule(trimmed));
    }
}
