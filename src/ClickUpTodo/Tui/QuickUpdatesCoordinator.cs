using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;
using ClickUpTodo.Tui.Screens;
using Terminal.Gui.App;

// The static `Application` facade is deprecated in Terminal.Gui 2.4 but remains the supported v2
// pattern; silence the deprecation until the instance-based API stabilizes (mirrors TodoApp).
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

/// <summary>
/// Host-agnostic Quick Updates orchestration (#153/#156/#159), factored out of <see cref="TodoApp"/> so
/// the dashboard and the single-task launch host (<see cref="SingleTaskApp"/>, #296) drive one code path
/// instead of duplicating ~430 lines of glue — the same move #345 made for agent dispatch
/// (<see cref="DispatchCoordinator"/>). It owns everything between "the user pressed Ctrl+U" and "the
/// field settled": the status fast-path/fetch, the four-pane <see cref="QuickUpdatesScreen"/> and its
/// wiring, the optimistic apply → off-thread confirmed write → revert-on-failure for Status/Priority
/// (#157), the immediate-apply Assignees (#158) and Lists (#242/#365) panes, and the supersede guards
/// that make a second commit for the same field win.
/// <para>
/// The <b>write target</b> is the host's (#297): <see cref="IQuickUpdateTarget"/> is the unit of truth a
/// commit resolves against and writes back to — the dashboard's main-list snapshot in list mode, a
/// <see cref="SingleTaskUpdateTarget"/> over one loaded task where there is no list (a feed-opened task,
/// #115; every task in single-task launch mode, #296; a Task Tree node outside the working set). This
/// class never touches a list or a snapshot itself, which is what lets a second host reuse it whole.
/// </para>
/// <para>
/// The host-specific bits are four small delegates over each host's screen stack and footer:
/// <paramref name="isFrontMost"/> (the stacking guard — <c>null</c> means "the host's own root", i.e. the
/// dashboard list, which is why a list-origin launch passes a null origin), <paramref name="isMounted"/>
/// (an async write can resolve after the user Esc'd, so a reconcile only ever touches a still-mounted
/// screen), <paramref name="flash"/> and <paramref name="mount"/>. Terminal.Gui glue is not CI-testable
/// (see CONTRIBUTING); the pure lifted helpers (<see cref="ColorForStatus"/>,
/// <see cref="WithPriority"/>, <see cref="AdditionalLists"/>) are unit-tested and the end-to-end wiring
/// is covered by the <c>tui-validate</c> checks.
/// </para>
/// </summary>
/// <param name="tasks">The ClickUp facade every write goes through.</param>
/// <param name="assignees">The Assignees pane's candidate pool (#155). <c>null</c> ⇒ the pane opens with
/// the task's current assignees and an empty candidate pool (a host that pooled none), rather than the
/// key being dead — Status/Priority don't depend on it.</param>
/// <param name="lists">The Lists pane's candidate pool (#238). <c>null</c> ⇒ as above: current
/// membership and removes still work, there is just nothing to add from.</param>
/// <param name="isFrontMost">Whether the given layer is the host's front-most screen (<c>null</c> = the
/// host's root/list). Guards the open against a stale off-thread status load.</param>
/// <param name="isMounted">Whether a screen is still mounted in the host's stack.</param>
/// <param name="flash">The host's status-line flash.</param>
/// <param name="mount">The host's <c>ShowScreen</c>.</param>
public sealed class QuickUpdatesCoordinator(
    TaskService tasks,
    AssigneeFrequencyCache? assignees,
    ListFrequencyCache? lists,
    Func<Screen?, bool> isFrontMost,
    Func<Screen, bool> isMounted,
    Action<string> flash,
    Action<Screen> mount)
{
    // Monotonic per-field commit counters, keyed PER TASK ID. The Quick Updates screen stays open, so the
    // user can fire a second write for the same field before the first returns; each commit stamps its
    // generation and a late continuation whose generation is no longer current is dropped, so the row + ✓
    // settle on the latest commit regardless of the order the responses arrive in.
    //
    // Keyed by task id rather than one global counter each because a commit can target a task other than
    // the one the screen was opened over: Ctrl+U on the Task Tree tab applies to the highlighted node, so
    // closing one task's Quick Updates and committing against a different node leaves two writes for two
    // DIFFERENT tasks in flight. A single counter would let the later one's generation cancel the earlier
    // task's confirm/revert, stranding its row on an optimistic value the server rejected (silently, with
    // no error flashed) — exactly the reasoning behind TodoApp's per-task `_nameCommitGen`.
    //
    // UI-thread-only (bumped in the commit, read in the continuation's Application.Invoke).
    private readonly Dictionary<string, int> _statusCommitGen = [];
    private readonly Dictionary<string, int> _priorityCommitGen = [];

    private static int NextGen(Dictionary<string, int> gens, string taskId)
        => gens[taskId] = gens.GetValueOrDefault(taskId) + 1;

    private static bool IsCurrentGen(Dictionary<string, int> gens, string taskId, int gen)
        => gens.GetValueOrDefault(taskId) == gen;

    // The armed (taskId, listId) for a stranding List-pane remove awaiting a second-press confirmation
    // (#365). A single field suffices: the pane is modal, one task at a time, and a fresh open re-seeds.
    // Guarded by `_armGate`: the arm/clear inside ApplyListAsync runs on a thread-pool thread (the
    // selector's immediate-apply callback) while a fresh Show clears it on the UI thread, and an untokened
    // in-flight write means those can overlap — an unsynchronised multi-word struct could then be read torn
    // (HasValue true paired with a stale id), silently confirming a strand-hazard remove nobody confirmed.
    private readonly Lock _armGate = new();
    private (string TaskId, string ListId)? _armedListRemoval;

    private (string TaskId, string ListId)? ArmedListRemoval
    {
        get { lock (_armGate) return _armedListRemoval; }
        set { lock (_armGate) _armedListRemoval = value; }
    }

    /// <summary>
    /// Opens Quick Updates for <paramref name="task"/>, stacked over <paramref name="origin"/> — the
    /// host's front-most screen, or <c>null</c> for a launch from the host's root (the dashboard's main
    /// list). Applies the no-list guard, then either the warmed-statuses fast path (open instantly, no
    /// round-trip) or an off-thread status fetch behind a "Loading statuses…" flash, re-checking the
    /// origin is still front-most before mounting.
    /// <para>
    /// <paramref name="reflect"/> is the <see cref="TaskDetailScreen"/> showing <b>this same task</b>, or
    /// null: each committed/confirmed/reverted status and priority is reflected onto it (#159) so the
    /// popped-back detail shows the change, and its loaded <c>Lists</c> seed the List pane. Null (a
    /// list-origin launch, or a Task Tree row that isn't the detail's own task) instead background-enriches
    /// the pane from a fresh detail fetch and leaves the detail header alone.
    /// </para>
    /// </summary>
    public void Open(TaskItem task, IQuickUpdateTarget target, Screen? origin = null,
        TaskDetailScreen? reflect = null)
    {
        if (!isFrontMost(origin))
            return;

        // Quick Updates applies to any selected task, including one that isn't my own work — a context
        // parent (#46) or a foreign subtask pulled in under my parent (#70/#179). The former ownership
        // guards that blocked those rows were lifted in #160; only the no-list data constraint remains.
        // The trailing "(not assigned to you)" row markers still convey the context.
        if (string.IsNullOrWhiteSpace(task.ListId))
        {
            flash("This task has no list, so its statuses can't be loaded.");
            return;
        }

        // Fast path: statuses were warmed by the background prefetch — open instantly, no round-trip.
        if (tasks.TryGetCachedStatuses(task.ListId!, out var cached))
        {
            Show(task, cached, target, origin, reflect);
            return;
        }

        // Cold path: fetch off the UI thread with a loading indicator, then show the screen back on it.
        flash("Loading statuses…");
        _ = Task.Run(async () =>
        {
            try
            {
                var statuses = await tasks.GetStatusesForListAsync(task.ListId!);
                Application.Invoke(() => Show(task, statuses, target, origin, reflect));
            }
            catch (Exception ex)
            {
                Application.Invoke(() => flash($"Could not load statuses: {ErrorText.Short(ex)}"));
            }
        });
    }

    /// <summary>Shows the Quick Updates screen for a task and wires its Status/Priority commits. Must
    /// run on the UI thread. Status and Priority apply on Enter (#157) — the screen stays open (Esc
    /// exits); the Assignees pane applies immediately (#158).
    /// <para>
    /// <paramref name="origin"/> governs the stacking guard (a root launch opens over nothing; a
    /// detail launch stacks over exactly that screen) and <paramref name="reflect"/> receives an
    /// optimistic reflection of each committed status/priority — see <see cref="Open"/>.
    /// </para>
    /// </summary>
    private void Show(TaskItem task, IReadOnlyList<StatusOption> statuses, IQuickUpdateTarget target,
        Screen? origin, TaskDetailScreen? reflect)
    {
        if (statuses.Count == 0)
        {
            flash("No statuses available for this list.");
            return;
        }

        // One screen is focused at a time (#3/#38): the root origin opens over nothing; the detail origin
        // stacks over exactly the screen that requested it. A stale off-thread status load whose origin is
        // no longer front-most is dropped here.
        if (!isFrontMost(origin))
            return;

        // Fresh open ⇒ no armed stranding-remove carried over, so a first remove always re-warns (#365).
        ArmedListRemoval = null;

        // List pane (#242/#365): seed the home list (from the snapshot TaskItem, which always carries the
        // home list) and any additional "Tasks in Multiple Lists" locations. A launch over the detail of
        // this same task has the full membership on hand (reflect.Task.Lists); a root- or tree-row launch
        // has only the home list here and is enriched in the background below.
        var homeList = HomeList(task);
        var additionalLists = AdditionalLists(task, reflect?.Task);

        var screen = new QuickUpdatesScreen(
            task.Name, statuses, task.StatusName, task.PriorityLevel, task.Assignees,
            // Assignees pane (#158): candidate pool from the frequency cache (#155); add/remove apply
            // immediately via ApplyAssigneeAsync (the selector owns the optimistic update + revert).
            AssigneeMatch, AssigneeTopFrequent,
            (kind, person, ct) => ApplyAssigneeAsync(task.Id, kind, person, target, ct),
            // List pane (#242/#365): candidate pool from the list frequency cache; add/remove apply
            // immediately via ApplyListAsync, which runs the field-strand preflight + arm/confirm.
            homeList, additionalLists,
            ListMatch, ListTopFrequent,
            (kind, list, ct) => ApplyListAsync(task.Id, kind, list, ct));
        // Status/Priority apply on Enter and reconcile the screen's ✓ from the server-confirmed value.
        // The commit resolves against and writes back to `target` (#297) — the list snapshot in list mode,
        // the loaded task with no list in single-task mode — decoupling the write path from `_all`.
        // A launch over this task's own detail (#159) also reflects each committed value onto that detail
        // so the popped-back detail shows it; `statuses` supplies the colour for a reflected status.
        screen.StatusCommitted += status => ApplyStatus(task.Id, status, screen, target, reflect, statuses);
        screen.PriorityCommitted += level => ApplyPriority(task.Id, level, screen, target, reflect);
        mount(screen);

        // A root- or tree-row launch has only the home list from the snapshot; fetch the full membership in
        // the background and enrich the pane's additional locations (#242). A launch over this task's own
        // detail already seeded them above.
        if (reflect is null)
            EnrichListMemberships(task.Id, screen);
    }

    /// <summary>The task's additional "Tasks in Multiple Lists" locations (#242) for the List pane: the
    /// memberships carried by <paramref name="loaded"/> — the detail of this same task, when the launch
    /// had one on hand — minus the home list, which the pane seeds separately as the primary. Empty when
    /// no detail was loaded (a root/tree-row launch, enriched in the background instead). Pure.</summary>
    internal static IReadOnlyList<NamedEntity> AdditionalLists(TaskItem task, TaskDetail? loaded)
        => [.. (loaded?.Lists ?? []).Where(l => !string.Equals(l.Id, task.ListId, StringComparison.Ordinal))];

    // The candidate-pool projections, with an empty fallback when the host pooled none (see the ctor's
    // `assignees`/`lists` docs): the panes then show the task's current values with nothing to add from,
    // instead of Quick Updates being unavailable.
    private IReadOnlyList<TaskAssignee> AssigneeMatch(string query, ISet<long> exclude)
        => assignees?.Match(query, exclude) ?? [];

    private IReadOnlyList<TaskAssignee> AssigneeTopFrequent(int count, ISet<long> exclude)
        => assignees?.TopMostFrequent(count, exclude) ?? [];

    private IReadOnlyList<NamedEntity> ListMatch(string query, ISet<string> exclude)
        => lists?.Match(query, exclude) ?? [];

    private IReadOnlyList<NamedEntity> ListTopFrequent(int count, ISet<string> exclude)
        => lists?.TopMostFrequent(count, exclude) ?? [];

    /// <summary>
    /// Background-fetches a task's full membership and merges its additional "Tasks in Multiple Lists"
    /// locations into an open Quick Updates List pane (#242). Only runs for a launch with no loaded
    /// detail for the task, where the snapshot TaskItem carries only the home list. A failed/empty fetch
    /// leaves the pane seeded with the home list; the enrich no-ops if the screen has moved on or the
    /// user already began editing.
    /// </summary>
    private void EnrichListMemberships(string taskId, QuickUpdatesScreen screen)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var detail = await tasks.GetTaskDetailAsync(taskId).ConfigureAwait(false);
                if (detail.Lists.Count == 0)
                    return;
                Application.Invoke(() =>
                {
                    if (isFrontMost(screen))
                        screen.SeedListMemberships(detail.Lists);
                });
            }
            catch
            {
                // Best-effort enrich: on failure the pane keeps the home-list seed and any user
                // add/remove still reconciles from the server truth — not worth a flash.
            }
        });
    }

    /// <summary>
    /// Performs a Quick Updates List-pane add/remove (#242) behind the field-strand handling (#365).
    /// Runs off the UI thread inside the selector's immediate-apply callback and returns the
    /// server-confirmed membership so the embedded <see cref="ListSelectorView"/> can reconcile. The
    /// membership endpoints echo no body, so the confirmed set is read back from a fresh
    /// <see cref="TaskService.GetTaskDetailAsync"/> — the home list plus the additional locations.
    /// <para><b>Add</b> is always safe and writes immediately. <b>Removing the home list</b> is a
    /// <i>move</i> (out of scope): it's blocked with a flash and the membership returned unchanged.
    /// <b>Removing an additional list</b> runs a preflight — comparing the task's set Custom Field values
    /// (from the detail) against each list's field definitions
    /// (<see cref="TaskService.GetListCustomFieldsAsync"/>) via
    /// <see cref="ListMembershipMigration.StrandedFieldsOnRemove"/>. When nothing would be stranded it
    /// writes silently; when set values only the removed list defines would be hidden it flashes them and
    /// <b>arms</b> a second-press confirmation, returning the membership unchanged so the row re-shows.
    /// The confirming press writes. See <c>docs/plans/completed/list-change-field-status-migration.md</c>.</para>
    /// <para>Flashes (home-guard / arm) are marshalled to the UI thread; unlike
    /// <see cref="ApplyAssigneeAsync"/> the main-list row shows only the home list, so there is no host
    /// row to reconcile.</para>
    /// </summary>
    private async Task<IReadOnlyList<NamedEntity>> ApplyListAsync(
        string taskId, ToggleKind kind, NamedEntity list, CancellationToken ct)
    {
        // Deliberately do NOT thread the selector's token into the write: it's cancelled when the screen
        // is disposed (Esc), so forwarding it would drop an add/remove the user already saw applied. Same
        // rationale as ApplyAssigneeAsync / ApplyStatus.
        _ = ct;

        // Add is always safe (it only exposes fields, never hides them) — no preflight, so skip the
        // detail read the strand check would need and just write + read the confirmed set back.
        if (kind == ToggleKind.Added)
        {
            ArmedListRemoval = null;
            await tasks.AddTaskToListAsync(taskId, list.Id).ConfigureAwait(false);
            return await ReadMembershipAsync(taskId).ConfigureAwait(false);
        }

        // A remove needs the current server truth (home + additional locations) to guard the home list
        // and to return the membership unchanged on a block/arm; the task's set field values drive the
        // strand preflight.
        var detail = await tasks.GetTaskDetailAsync(taskId).ConfigureAwait(false);
        var home = HomeListOf(detail);
        var currentMembership = ListSelectorModel.Membership(home, detail.Lists);

        var armed = ArmedListRemoval is { } a
            && string.Equals(a.TaskId, taskId, StringComparison.Ordinal)
            && string.Equals(a.ListId, list.Id, StringComparison.Ordinal);
        var removingHome = home is { } h && string.Equals(h.Id, list.Id, StringComparison.Ordinal);

        // Preflight the strand hazard only when it can change the outcome: not for the home list (blocked
        // regardless) and not once armed (the user already confirmed, so the planner ignores the set).
        IReadOnlyList<string> stranded = [];
        if (!removingHome && !armed)
        {
            var remaining = currentMembership
                .Select(l => l.Id)
                .Where(id => !string.Equals(id, list.Id, StringComparison.Ordinal))
                .ToList();
            var perListDefs = await FetchPerListDefinitionsAsync([list.Id, .. remaining]).ConfigureAwait(false);
            stranded = ListMembershipMigration.StrandedFieldsOnRemove(
                detail.CustomFields, list.Id, perListDefs, remaining);
        }

        var decision = ListMembershipApplyPlanner.Plan(kind, list, home?.Id, stranded, armed);
        switch (decision.Action)
        {
            case ListApplyAction.BlockHomeRemove:
                FlashOnUi(decision.Message!);
                return currentMembership; // unchanged → selector re-shows the (home) row
            case ListApplyAction.ArmRemoveConfirmation:
                ArmedListRemoval = (taskId, list.Id);
                FlashOnUi(decision.Message!);
                return currentMembership; // unchanged → row re-shows; a second remove confirms
            default: // WriteRemove (strand-free, or the armed confirmation)
                ArmedListRemoval = null;
                await tasks.RemoveTaskFromListAsync(taskId, list.Id).ConfigureAwait(false);
                return await ReadMembershipAsync(taskId).ConfigureAwait(false);
        }
    }

    /// <summary>Reads a task's server-confirmed list membership (home + additional locations) back from a
    /// fresh detail fetch — the membership endpoints echo no body, so the confirmed set comes from here.</summary>
    private async Task<IReadOnlyList<NamedEntity>> ReadMembershipAsync(string taskId)
    {
        var detail = await tasks.GetTaskDetailAsync(taskId).ConfigureAwait(false);
        return ListSelectorModel.Membership(HomeListOf(detail), detail.Lists);
    }

    /// <summary>Fetches the Custom Field definitions of each list, keyed by list id (blank ids and
    /// duplicates dropped). A list whose fetch fails is left absent so
    /// <see cref="ListMembershipMigration.StrandedFieldsOnRemove"/> treats it conservatively.</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<CustomFieldDefinition>>> FetchPerListDefinitionsAsync(
        IEnumerable<string> listIds)
    {
        var result = new Dictionary<string, IReadOnlyList<CustomFieldDefinition>>(StringComparer.Ordinal);
        foreach (var id in listIds.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal))
        {
            try
            {
                result[id] = await tasks.GetListCustomFieldsAsync(id).ConfigureAwait(false);
            }
            catch
            {
                // Absent key ⇒ conservative (potentially-stranding) treatment in the migration core.
            }
        }
        return result;
    }

    /// <summary>Flashes a message from a background thread by marshalling to the UI thread.</summary>
    private void FlashOnUi(string message) => Application.Invoke(() => flash(message));

    /// <summary>The home list of a task detail as a NamedEntity, or null when it has no list. Falls the
    /// display name back to the id if the detail carries none (the marker still shows).</summary>
    private static NamedEntity? HomeListOf(TaskDetail detail)
        => HomeList(detail.ListId, detail.ListName);

    /// <summary>The same for a list item, so the pane's initial seed and its server-confirmed reconcile
    /// label the primary row by one rule — a blank-but-present ListName falls back to the id in both,
    /// rather than seeding an empty label that changes after the first add/remove.</summary>
    private static NamedEntity? HomeList(TaskItem task) => HomeList(task.ListId, task.ListName);

    private static NamedEntity? HomeList(string? listId, string? listName)
        => string.IsNullOrWhiteSpace(listId)
            ? null
            : new NamedEntity(listId!, string.IsNullOrWhiteSpace(listName) ? listId! : listName!);

    /// <summary>
    /// Performs a Quick Updates Assignees-pane add/remove (#158): writes the change to ClickUp off the
    /// UI thread and returns the <b>server-confirmed</b> assignee set so the embedded
    /// <see cref="AssigneeSelectorView"/> can reconcile its own pane display. On success it also
    /// reconciles the task through the write target (mirroring <see cref="ApplyStatus"/>) so the main
    /// list — hidden behind the modal — and its assignee badge (#F6) reflect the change once the screen
    /// is dismissed. The selector owns the optimistic pane update and the revert-on-failure; a throw here
    /// propagates to it. The target only ever moves to a confirmed set, so a failed write leaves it
    /// untouched (nothing to revert host-side); overlapping same-task writes settle on the last-returning
    /// confirmed set and self-heal on the next refresh.
    /// </summary>
    private async Task<IReadOnlyList<TaskAssignee>> ApplyAssigneeAsync(
        string taskId, ToggleKind kind, TaskAssignee person, IQuickUpdateTarget target, CancellationToken ct)
    {
        // Deliberately do NOT thread the selector's cancellation token into the write: that token is
        // cancelled when the screen is disposed (Esc), so forwarding it would cancel an in-flight
        // add/remove the user has already seen applied — silently dropping it until the next refresh.
        // Status/Priority commits (ApplyStatus/ApplyPriority) issue their writes untokened for the same
        // reason; assignees match that. The token still guards the *view's* own reconcile/revert (it
        // re-checks IsCancellationRequested), and our reconcile below is guarded by target.Resolve.
        _ = ct;
        var confirmed = kind == ToggleKind.Added
            ? await tasks.AddAssigneeAsync(taskId, person.Id).ConfigureAwait(false)
            : await tasks.RemoveAssigneeAsync(taskId, person.Id).ConfigureAwait(false);
        Application.Invoke(() =>
        {
            // Resolve/apply through the target (#297): the list target reconciles the row in place — for a
            // foreign subtask / context parent too (#160), not just tasks in _all — while a single-task
            // target updates the loaded task with no list present.
            if (target.Resolve(taskId) is { } t)
                target.Apply(t with { Assignees = confirmed }, sending: false);
        });
        return confirmed;
    }

    /// <summary>
    /// Applies a Quick Updates status commit for <paramref name="taskId"/>: move the ✓ optimistically,
    /// optimistic target update, then an off-thread write, confirming with the server's returned status on
    /// success and reverting on failure. The task is resolved fresh from the target so consecutive edits
    /// compose; a superseded (out-of-order) continuation is dropped; the screen's ✓ is reconciled to the
    /// confirmed/reverted value while it's still mounted.
    /// <para>
    /// When launched over the Task Detail view of this same task (#159), <paramref name="reflect"/> is
    /// that screen and <paramref name="statuses"/> its list's status options; the
    /// committed/confirmed/reverted status is reflected onto the detail (with the matching colour) so it
    /// stays in sync with the list row.
    /// </para>
    /// </summary>
    private void ApplyStatus(string taskId, string status, QuickUpdatesScreen screen,
        IQuickUpdateTarget target, TaskDetailScreen? reflect = null, IReadOnlyList<StatusOption>? statuses = null)
    {
        var task = target.Resolve(taskId);
        if (task is null)
        {
            // The screen hasn't moved its ✓ yet (it defers that to us), so just report and bail.
            flash("This task is no longer in the list — status unchanged.");
            return;
        }
        var gen = NextGen(_statusCommitGen, taskId);
        var previousStatus = task.StatusName;
        var previousColor = task.StatusColor;

        var color = ColorForStatus(statuses, status);
        ReconcileScreenStatus(screen, status); // optimistic ✓
        ReflectDetailStatus(reflect, status, color);
        // Carry the colour with the name: TaskService.ApplyStatusChange folds both, so every surface the
        // target repaints (the main-list row, a Task Tree row) shows the new status in ITS colour rather
        // than the previous status's.
        target.Apply(task with { StatusName = status, StatusColor = color }, sending: true);
        flash($"Setting '{status}'…");

        _ = Task.Run(async () =>
        {
            try
            {
                var confirmed = await tasks.SetStatusAsync(taskId, status);
                Application.Invoke(() =>
                {
                    if (!IsCurrentGen(_statusCommitGen, taskId, gen))
                        return; // a newer status commit for this task superseded it
                    var final = confirmed ?? status;
                    var finalColor = ColorForStatus(statuses, final);
                    if (target.Resolve(taskId) is { } t)
                        target.Apply(t with { StatusName = final, StatusColor = finalColor }, sending: false);
                    ReconcileScreenStatus(screen, final);
                    ReflectDetailStatus(reflect, final, finalColor);
                    flash($"Set status to '{final}'.");
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    if (!IsCurrentGen(_statusCommitGen, taskId, gen))
                        return;
                    if (target.Resolve(taskId) is { } t)
                        // Revert name AND colour: the optimistic apply moved both.
                        target.Apply(t with { StatusName = previousStatus, StatusColor = previousColor },
                            sending: false);
                    ReconcileScreenStatus(screen, previousStatus);
                    ReflectDetailStatus(reflect, previousStatus, previousColor);
                    flash($"Could not set status: {ErrorText.Short(ex)}");
                });
            }
        });
    }

    /// <summary>
    /// Applies a Quick Updates priority commit for <paramref name="taskId"/> (<paramref name="level"/>
    /// null = clear), mirroring <see cref="ApplyStatus"/>: optimistic ✓ + target update, off-thread write,
    /// confirm-from-server on success, revert on failure, drop a superseded continuation.
    /// </summary>
    private void ApplyPriority(string taskId, int? level, QuickUpdatesScreen screen,
        IQuickUpdateTarget target, TaskDetailScreen? reflect = null)
    {
        var task = target.Resolve(taskId);
        if (task is null)
        {
            flash("This task is no longer in the list — priority unchanged.");
            return;
        }
        var gen = NextGen(_priorityCommitGen, taskId);
        var previousLevel = task.PriorityLevel;

        ReconcileScreenPriority(screen, level); // optimistic ✓
        ReflectDetailPriority(reflect, level);
        target.Apply(WithPriority(task, level), sending: true);
        flash($"Setting priority '{ClickUpPriority.NameFromLevel(level) ?? "none"}'…");

        _ = Task.Run(async () =>
        {
            try
            {
                var confirmed = await tasks.SetPriorityAsync(taskId, level);
                Application.Invoke(() =>
                {
                    if (!IsCurrentGen(_priorityCommitGen, taskId, gen))
                        return; // a newer priority commit for this task superseded it
                    if (target.Resolve(taskId) is { } t)
                        target.Apply(WithPriority(t, confirmed), sending: false);
                    ReconcileScreenPriority(screen, confirmed);
                    ReflectDetailPriority(reflect, confirmed);
                    flash($"Set priority to '{ClickUpPriority.NameFromLevel(confirmed) ?? "none"}'.");
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    if (!IsCurrentGen(_priorityCommitGen, taskId, gen))
                        return;
                    if (target.Resolve(taskId) is { } t)
                        target.Apply(WithPriority(t, previousLevel), sending: false); // revert
                    ReconcileScreenPriority(screen, previousLevel);
                    ReflectDetailPriority(reflect, previousLevel);
                    flash($"Could not set priority: {ErrorText.Short(ex)}");
                });
            }
        });
    }

    /// <summary>A copy of <paramref name="task"/> carrying priority <paramref name="level"/> with the
    /// canonical name + colour for that level (null clears all three). Pure.</summary>
    internal static TaskItem WithPriority(TaskItem task, int? level) => task with
    {
        PriorityLevel = level,
        PriorityName = ClickUpPriority.NameFromLevel(level),
        PriorityColor = ClickUpPriority.ColorFromLevel(level),
    };

    // The async write can resolve after the user has Esc'd or stacked another screen; only touch the
    // screen's ✓ while it's still mounted (a disposed/detached screen's list would throw or be moot).
    private void ReconcileScreenStatus(QuickUpdatesScreen screen, string? status)
    {
        if (isMounted(screen))
            screen.SetEffectiveStatus(status);
    }

    private void ReconcileScreenPriority(QuickUpdatesScreen screen, int? level)
    {
        if (isMounted(screen))
            screen.SetEffectivePriority(level);
    }

    /// <summary>The colour of the status option named <paramref name="status"/> in
    /// <paramref name="statuses"/> (case-insensitive), or null when unknown — used to colour the status
    /// reflected onto the detail view (#159). Pure.</summary>
    internal static string? ColorForStatus(IReadOnlyList<StatusOption>? statuses, string? status)
        => status is null
            ? null
            : statuses?.FirstOrDefault(s => string.Equals(s.Name, status, StringComparison.OrdinalIgnoreCase))?.Color;

    // Reflect a committed status/priority onto the Task Detail view Quick Updates was launched over (#159),
    // guarded on that screen still being mounted, so the popped-back detail shows the change. A null
    // reflect (a root-origin launch, or a Task Tree row that isn't that detail's own task) is a no-op.
    // Priority uses the canonical name/colour for the level, matching the list row's WithPriority.
    private void ReflectDetailStatus(TaskDetailScreen? reflect, string? status, string? color)
    {
        if (reflect is not null && isMounted(reflect))
            reflect.ApplyOptimisticStatus(status, color);
    }

    private void ReflectDetailPriority(TaskDetailScreen? reflect, int? level)
    {
        if (reflect is not null && isMounted(reflect))
            reflect.ApplyOptimisticPriority(ClickUpPriority.NameFromLevel(level), ClickUpPriority.ColorFromLevel(level));
    }
}
