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
    public const int CurrentVersion = 5;

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
