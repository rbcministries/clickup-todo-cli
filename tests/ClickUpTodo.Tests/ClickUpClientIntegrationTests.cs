using System.Text.Json;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Integration tests that hit the real ClickUp API. Each test self-skips (SkippableFact) unless the
/// environment variables it needs are set, so CI stays green without credentials.
///
/// <para><b>Environment variables — how to run the full suite locally.</b></para>
/// <list type="bullet">
///   <item><c>CLICKUP_TOKEN</c> — a ClickUp personal API token (<c>pk_…</c>). Required by every test
///   here; without it, all of them skip.</item>
///   <item><c>CLICKUP_WORKSPACE_ID</c> — a Workspace (team) id. Unlocks the member/assigned-task tests.</item>
///   <item><c>CLICKUP_LIST_ID</c> — a List id with at least two statuses. Unlocks the list-status,
///   create-task, and status/priority tests. Point it at a <b>scratch list you own</b>; create-task
///   creates (and now deletes) a throwaway task here.</item>
///   <item><c>CLICKUP_TASK_ID</c> — a single task id. Unlocks the task-detail, comment, status,
///   priority, description, assignee, and multi-list tests. <b>These tests MUTATE this task</b>
///   (status, priority, description, assignee, and a posted comment). They restore what they change in
///   a <c>finally</c>, but the description restore rewrites as <i>plain text</i> and a posted comment is
///   not deleted — so this MUST be a <b>throwaway/scratch task</b>, not real work. The simplest setup:
///   create a scratch task on <c>CLICKUP_LIST_ID</c> (so the status/priority tests, which look the task
///   up via <c>GetListTasksAsync(CLICKUP_LIST_ID)</c>, can find it), point <c>CLICKUP_TASK_ID</c> at it,
///   run the suite, then delete it (<see cref="ClickUpClient.DeleteTaskAsync"/>).</item>
///   <item><c>CLICKUP_SECONDARY_LIST_ID</c> — a second List id the task is <i>not</i> already in, for the
///   "Tasks in Multiple Lists" test. Prefer a <b>second scratch list you own</b>, for the same reason as
///   <c>CLICKUP_LIST_ID</c>: the test writes into it (it adds the task, then removes the membership in a
///   <c>finally</c>). If you can't create one — the personal API token may lack list-create permission —
///   any list the task isn't already in works, since the test is self-cleaning and self-skips when the
///   (paid) ClickApp is disabled; but avoid a busy shared/team list so a stray add/remove can't disturb
///   real work.</item>
///   <item><c>CLICKUP_OAUTH_CLIENT_ID</c> / <c>CLICKUP_OAUTH_CLIENT_SECRET</c> / <c>CLICKUP_OAUTH_CODE</c>
///   — for <see cref="ClickUpOAuthIntegrationTests"/>. The code is a <b>single-use authorization code</b>
///   from a fresh browser OAuth redirect, so that test can't be automated and stays skipped in unattended
///   runs; run it by hand when validating the OAuth exchange.</item>
/// </list>
/// </summary>
public sealed class ClickUpClientIntegrationTests
{
    private static string? Token => Environment.GetEnvironmentVariable("CLICKUP_TOKEN");
    private static string? WorkspaceId => Environment.GetEnvironmentVariable("CLICKUP_WORKSPACE_ID");
    private static string? ListId => Environment.GetEnvironmentVariable("CLICKUP_LIST_ID");
    private static string? TaskId => Environment.GetEnvironmentVariable("CLICKUP_TASK_ID");
    private static string? SecondaryListId => Environment.GetEnvironmentVariable("CLICKUP_SECONDARY_LIST_ID");
    private static string? CommentId => Environment.GetEnvironmentVariable("CLICKUP_COMMENT_ID");

    [SkippableFact]
    public async Task GetMe_ReturnsAuthenticatedUser()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token), "Set CLICKUP_TOKEN to run ClickUp integration tests.");
        using var client = new ClickUpClient(Token!);

        var me = await client.GetMeAsync();

        Assert.True(me.Id > 0);
        Assert.False(string.IsNullOrWhiteSpace(me.DisplayName));
    }

    [SkippableFact]
    public async Task GetWorkspaces_ReturnsAtLeastOne()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token), "Set CLICKUP_TOKEN to run ClickUp integration tests.");
        using var client = new ClickUpClient(Token!);

        var workspaces = await client.GetWorkspacesAsync();

        Assert.NotEmpty(workspaces);
        Assert.All(workspaces, w => Assert.False(string.IsNullOrWhiteSpace(w.Id)));
    }

    [SkippableFact]
    public async Task GetWorkspaceMembers_ReturnsMembersWithIds()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(WorkspaceId),
            "Set CLICKUP_TOKEN and CLICKUP_WORKSPACE_ID to run this test.");
        using var client = new ClickUpClient(Token!);
        var me = await client.GetMeAsync();

        var members = await client.GetWorkspaceMembersAsync(WorkspaceId!);

        // The workspace always contains at least the authenticated user; every member carries an id.
        Assert.NotEmpty(members);
        Assert.All(members, m => Assert.True(m.Id > 0));
        Assert.Contains(members, m => m.Id == me.Id);
    }

    [SkippableFact]
    public async Task GetAssignedTasks_ReturnsTasksWithIds()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(WorkspaceId),
            "Set CLICKUP_TOKEN and CLICKUP_WORKSPACE_ID to run this test.");
        using var client = new ClickUpClient(Token!);
        var me = await client.GetMeAsync();

        var tasks = await client.GetAssignedTasksAsync(WorkspaceId!, [me.Id]);

        Assert.All(tasks, t => Assert.False(string.IsNullOrWhiteSpace(t.Id)));
    }

    [SkippableFact]
    public async Task GetListStatuses_ReturnsStatuses()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(ListId),
            "Set CLICKUP_TOKEN and CLICKUP_LIST_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        var statuses = await client.GetListStatusesAsync(ListId!);

        Assert.NotEmpty(statuses);
        Assert.All(statuses, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
    }

    [SkippableFact]
    public async Task GetListCustomFields_ReturnsDefinitions()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(ListId),
            "Set CLICKUP_TOKEN and CLICKUP_LIST_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        // A list may legitimately have no custom fields, so we don't assert non-empty; we assert the call
        // succeeds and every returned definition is well-formed (non-blank id + type), which is what the
        // New Task widget/required-enforcement follow-ups depend on.
        var fields = await client.GetListCustomFieldsAsync(ListId!);

        Assert.All(fields, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Id));
            Assert.False(string.IsNullOrWhiteSpace(f.Type));
        });
    }

    [SkippableFact]
    public async Task CreateTask_CreatesTaskInList_AndReturnsItMapped()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(ListId),
            "Set CLICKUP_TOKEN and CLICKUP_LIST_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        // The name flags it as a test artifact in case cleanup can't run (e.g. the process is killed
        // between create and delete). The finally deletes it so the test leaves no residue.
        var name = "[clickup-todo-cli test] create-task smoke — safe to delete";

        var created = await client.CreateTaskAsync(ListId!, new NewTaskRequest
        {
            Name = name,
            Description = "Created by the CreateTask integration test.",
            PriorityLevel = 3,
        });

        try
        {
            Assert.False(string.IsNullOrWhiteSpace(created.Id));
            Assert.Equal(name, created.Name);
            Assert.Equal(3, created.PriorityLevel);
        }
        finally
        {
            // Delete the throwaway task so repeated runs don't pile up artifacts on the list.
            if (!string.IsNullOrWhiteSpace(created.Id))
                await client.DeleteTaskAsync(created.Id);
        }
    }

    [SkippableFact]
    public async Task DeleteComment_RemovesTheCommentFromTheTask()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(ListId),
            "Set CLICKUP_TOKEN and CLICKUP_LIST_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        // A throwaway task carries the comment round-trip so nothing is left on a real task; both the
        // comment (via the delete under test) and the task (in the finally) are cleaned up.
        var created = await client.CreateTaskAsync(ListId!, new NewTaskRequest
        {
            Name = "[clickup-todo-cli test] delete-comment round-trip — safe to delete",
            Description = "Created by the DeleteComment integration test.",
        });
        try
        {
            var posted = await client.CreateTaskCommentAsync(created.Id, "throwaway comment — safe to delete (#594)");
            Assert.False(string.IsNullOrWhiteSpace(posted.Id));

            // The comment is present on the task before the delete...
            var before = await client.GetTaskCommentsAsync(created.Id);
            Assert.Contains(before, c => string.Equals(c.Id, posted.Id, StringComparison.Ordinal));

            // ...the author (us) may delete it, so the write succeeds...
            await client.DeleteCommentAsync(posted.Id);

            // ...and it's gone on a re-read (empty body ⇒ the caller re-fetches to confirm the removal).
            var after = await client.GetTaskCommentsAsync(created.Id);
            Assert.DoesNotContain(after, c => string.Equals(c.Id, posted.Id, StringComparison.Ordinal));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(created.Id))
                await client.DeleteTaskAsync(created.Id);
        }
    }

    [SkippableFact]
    public async Task SetTaskStatus_ReturnsConfirmedStatusFromWriteResponse()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(ListId) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN, CLICKUP_LIST_ID and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        var statuses = await client.GetListStatusesAsync(ListId!);
        Skip.If(statuses.Count < 2, "List needs at least two statuses to flip between for this test.");

        var current = (await client.GetListTasksAsync(ListId!)).FirstOrDefault(t => t.Id == TaskId);
        Skip.If(current is null, "CLICKUP_TASK_ID is not an open task on CLICKUP_LIST_ID.");

        var target = statuses.First(s => !string.Equals(s.Name, current!.StatusName, StringComparison.OrdinalIgnoreCase));
        try
        {
            // The write response should carry the new status — no read-after-write needed.
            var confirmed = await client.SetTaskStatusAsync(TaskId!, target.Name);
            Assert.Equal(target.Name, confirmed, ignoreCase: true);
        }
        finally
        {
            // Restore the original status so the test is idempotent.
            if (!string.IsNullOrWhiteSpace(current!.StatusName))
                await client.SetTaskStatusAsync(TaskId!, current.StatusName!);
        }
    }

    [SkippableFact]
    public async Task SetTaskPriority_RoundTripsThroughWriteResponse()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(ListId) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN, CLICKUP_LIST_ID and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        var original = (await client.GetListTasksAsync(ListId!)).FirstOrDefault(t => t.Id == TaskId);
        Skip.If(original is null, "CLICKUP_TASK_ID is not an open task on CLICKUP_LIST_ID.");

        // Pick a target level different from the current one; the write response reports the truth.
        var target = original!.PriorityLevel == 2 ? 3 : 2;
        try
        {
            var confirmed = await client.SetTaskPriorityAsync(TaskId!, target);
            Assert.Equal(target, confirmed);
        }
        finally
        {
            // Restore the original priority (clearing it when the task had none) for idempotency.
            await client.SetTaskPriorityAsync(TaskId!, original.PriorityLevel);
        }
    }

    [SkippableFact]
    public async Task SetTaskDescription_RoundTripsThroughDetailFetch()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        // Preserve the original description so the test is idempotent (restore in finally). The read
        // model already surfaces plain text (text_content → description); write plain text back.
        var original = (await client.GetTaskDetailAsync(TaskId!)).Description ?? "";
        var marker = $"[clickup-todo-cli test] description round-trip — safe to overwrite ({TaskId})";
        try
        {
            var confirmed = await client.SetTaskDescriptionAsync(TaskId!, marker);
            Assert.Equal(marker, confirmed);

            // The change is reflected on a subsequent detail fetch (read → write → re-read is lossless).
            var reread = await client.GetTaskDetailAsync(TaskId!);
            Assert.Equal(marker, reread.Description);
        }
        finally
        {
            // The restore rewrites the description as *plain text* (that's all the read model exposes),
            // so if CLICKUP_TASK_ID points at a task whose description was authored in markdown, this
            // test flattens that formatting. Point CLICKUP_TASK_ID at a throwaway/scratch task.
            await client.SetTaskDescriptionAsync(TaskId!, original);
        }
    }

    [SkippableFact]
    public async Task SetTaskName_RoundTripsThroughDetailFetch()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        // Preserve the original name so the test is idempotent (restore in finally).
        var original = (await client.GetTaskDetailAsync(TaskId!)).Name;
        var marker = $"[clickup-todo-cli test] rename round-trip — safe to overwrite ({TaskId})";
        try
        {
            var confirmed = await client.SetTaskNameAsync(TaskId!, marker);
            Assert.Equal(marker, confirmed);

            // The rename is reflected on a subsequent detail fetch (read → write → re-read is lossless).
            var reread = await client.GetTaskDetailAsync(TaskId!);
            Assert.Equal(marker, reread.Name);
        }
        finally
        {
            // Point CLICKUP_TASK_ID at a throwaway/scratch task — this overwrites the title.
            await client.SetTaskNameAsync(TaskId!, original);
        }
    }

    [SkippableFact]
    public async Task AddAndRemoveTaskAssignee_ReconcileFromWriteResponse()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);
        var me = await client.GetMeAsync();

        var before = await client.GetTaskDetailAsync(TaskId!);
        var wasAssigned = before.Assignees.Contains(me.DisplayName);

        var afterAdd = await client.AddTaskAssigneeAsync(TaskId!, me.Id);
        Assert.Contains(afterAdd, a => a.Id == me.Id);

        // Only undo what this test added, so a task the user was already on is left unchanged.
        if (!wasAssigned)
        {
            var afterRemove = await client.RemoveTaskAssigneeAsync(TaskId!, me.Id);
            Assert.DoesNotContain(afterRemove, a => a.Id == me.Id);
        }
    }

    [SkippableFact]
    public async Task AddAndRemoveTaskListMembership_RoundTripsThroughTaskDetailLists()
    {
        // "Tasks in Multiple Lists" (#237): add the task to a second list, confirm it surfaces in the
        // task detail's Lists membership, then remove it again. Requires the ClickApp to be enabled and a
        // second list id distinct from the task's home list.
        Skip.If(
            string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(TaskId) || string.IsNullOrWhiteSpace(SecondaryListId),
            "Set CLICKUP_TOKEN, CLICKUP_TASK_ID and CLICKUP_SECONDARY_LIST_ID (a list other than the task's home) to run this test.");
        using var client = new ClickUpClient(Token!);

        var before = await client.GetTaskDetailAsync(TaskId!);
        var wasMember = before.Lists.Any(l => l.Id == SecondaryListId);
        Skip.If(wasMember, "Task is already a member of CLICKUP_SECONDARY_LIST_ID; pick a list it isn't in.");

        try
        {
            await client.AddTaskToListAsync(TaskId!, SecondaryListId!);
        }
        catch (ClickUpApiException ex)
        {
            // "Tasks in Multiple Lists" is an opt-in (paid) ClickApp; a workspace without it returns a
            // 4xx. The facade correctly surfaced that as a typed exception (not a crash), so treat a
            // disabled ClickApp as a skip rather than a spurious failure — and don't run the cleanup
            // remove (it would throw the same way and mask the real cause).
            Skip.If(true, $"Add-to-list failed — the 'Tasks in Multiple Lists' ClickApp is likely disabled on this workspace (HTTP {ex.StatusCode}).");
            return;
        }

        try
        {
            var afterAdd = await client.GetTaskDetailAsync(TaskId!);
            Assert.Contains(afterAdd.Lists, l => l.Id == SecondaryListId);
        }
        finally
        {
            // Always undo, so the task's membership is left exactly as this test found it.
            await client.RemoveTaskFromListAsync(TaskId!, SecondaryListId!);
        }

        var afterRemove = await client.GetTaskDetailAsync(TaskId!);
        Assert.DoesNotContain(afterRemove.Lists, l => l.Id == SecondaryListId);
    }

    [SkippableFact]
    public async Task GetTaskDetail_ReturnsRichTask()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        var detail = await client.GetTaskDetailAsync(TaskId!);

        Assert.Equal(TaskId, detail.Id);
        Assert.False(string.IsNullOrWhiteSpace(detail.Name));
        // Tags/assignees/custom-field collections are always materialized (never null).
        Assert.NotNull(detail.Tags);
        Assert.NotNull(detail.Assignees);
        Assert.NotNull(detail.CustomFields);
    }

    [SkippableFact]
    public async Task GetTaskDetail_ChecklistsRoundTrip_AreMaterializedAndWellFormed()
    {
        // The checklist read model (#454): the task fetch already carries `checklists`, so the mapped
        // detail must surface them (never null) and every checklist/item must be well-formed. A scratch
        // task may legitimately have no checklists, so we don't assert non-empty — pointing
        // CLICKUP_CHECKLIST_TASK_ID at a task that HAS a checklist exercises the nested-item path below.
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        var detail = await client.GetTaskDetailAsync(TaskId!);

        Assert.NotNull(detail.Checklists);
        Assert.All(detail.Checklists, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Id));
            Assert.NotNull(c.Items);
            Assert.True(c.Resolved >= 0 && c.Unresolved >= 0);
            Assert.All(c.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Id)));
        });
    }

    [SkippableFact]
    public async Task GetTaskDetail_ChecklistBearingTask_HasNestedItems()
    {
        // Deep round-trip against a task known to carry a checklist (#454). Gated on its own env var so
        // the general suite doesn't require every scratch task to have one; when set, it proves the whole
        // spec → Kiota → ChecklistReader → domain path surfaces a real checklist with at least one item.
        var checklistTaskId = Environment.GetEnvironmentVariable("CLICKUP_CHECKLIST_TASK_ID");
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(checklistTaskId),
            "Set CLICKUP_TOKEN and CLICKUP_CHECKLIST_TASK_ID (a task that has a checklist) to run this test.");
        using var client = new ClickUpClient(Token!);

        var detail = await client.GetTaskDetailAsync(checklistTaskId!);

        Assert.NotEmpty(detail.Checklists);
        Assert.All(detail.Checklists, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
        Assert.Contains(detail.Checklists, c => c.Items.Count > 0);
    }

    [SkippableFact]
    public async Task SetChecklistItemResolved_TogglesState_AndReturnsConfirmedChecklist()
    {
        // The toggle write (D, #457): flip a real checklist item's `resolved` and confirm the returned
        // parent checklist reflects it, then flip it back so the run is state-neutral. Gated on a task known
        // to have a checklist item (same env var the read test uses) so the general suite needn't grow one.
        var checklistTaskId = Environment.GetEnvironmentVariable("CLICKUP_CHECKLIST_TASK_ID");
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(checklistTaskId),
            "Set CLICKUP_TOKEN and CLICKUP_CHECKLIST_TASK_ID (a task that has a checklist item) to run this test.");
        using var client = new ClickUpClient(Token!);

        var detail = await client.GetTaskDetailAsync(checklistTaskId!);
        var checklist = detail.Checklists.FirstOrDefault(c => c.Items.Count > 0);
        Skip.If(checklist is null, "CLICKUP_CHECKLIST_TASK_ID has a checklist but no items to toggle.");
        var item = checklist!.Items[0];
        var original = item.Resolved;

        try
        {
            var flipped = await client.SetChecklistItemResolvedAsync(checklistTaskId!, checklist.Id, item.Id, !original);
            Assert.Equal(checklist.Id, flipped.Id);
            Assert.Equal(!original, FindItem(flipped.Items, item.Id)?.Resolved);
        }
        finally
        {
            // Restore the original state so the test leaves the real task untouched.
            var restored = await client.SetChecklistItemResolvedAsync(checklistTaskId!, checklist.Id, item.Id, original);
            Assert.Equal(original, FindItem(restored.Items, item.Id)?.Resolved);
        }

        static TaskChecklistItem? FindItem(IReadOnlyList<TaskChecklistItem> items, string id)
        {
            foreach (var i in items)
            {
                if (i.Id == id)
                    return i;
                var nested = FindItem(i.Children, id);
                if (nested is not null)
                    return nested;
            }
            return null;
        }
    }

    [SkippableFact]
    public async Task CreateRenameDeleteChecklistItem_RoundTrips_AndLeavesTheChecklistAsFound()
    {
        // Item CRUD (E, #458): add an item to a real checklist, rename it, then delete it — proving each
        // write against the live API and leaving the checklist exactly as found (the delete is the cleanup).
        // Reuses the same task-with-a-checklist env var as the toggle test so the suite needn't grow one.
        var checklistTaskId = Environment.GetEnvironmentVariable("CLICKUP_CHECKLIST_TASK_ID");
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(checklistTaskId),
            "Set CLICKUP_TOKEN and CLICKUP_CHECKLIST_TASK_ID (a task that has a checklist) to run this test.");
        using var client = new ClickUpClient(Token!);

        var detail = await client.GetTaskDetailAsync(checklistTaskId!);
        var checklist = detail.Checklists.FirstOrDefault();
        Skip.If(checklist is null, "CLICKUP_CHECKLIST_TASK_ID has no checklist to add an item to.");

        var addedName = $"clickup-todo integration item {Guid.NewGuid():N}";
        var afterCreate = await client.CreateChecklistItemAsync(checklist!.Id, addedName);
        var created = FindByName(afterCreate.Items, addedName);
        Assert.NotNull(created); // the new item is present in the reconciled checklist ClickUp echoes

        try
        {
            var renamedName = $"clickup-todo integration renamed {Guid.NewGuid():N}";
            var afterRename = await client.RenameChecklistItemAsync(checklist.Id, created!.Id, renamedName);
            Assert.Equal(renamedName, FindItemById(afterRename.Items, created.Id)?.Name);
            Assert.Null(FindByName(afterRename.Items, addedName)); // old name no longer present
        }
        finally
        {
            await client.DeleteChecklistItemAsync(checklist.Id, created!.Id);
        }

        // Re-fetch: the item is gone, so the task is left as it was found.
        var afterDelete = await client.GetTaskDetailAsync(checklistTaskId!);
        var afterDeleteChecklist = afterDelete.Checklists.FirstOrDefault(c => c.Id == checklist.Id);
        Assert.True(afterDeleteChecklist is null || FindItemById(afterDeleteChecklist.Items, created.Id) is null);

        static TaskChecklistItem? FindByName(IReadOnlyList<TaskChecklistItem> items, string name)
        {
            foreach (var i in items)
            {
                if (i.Name == name)
                    return i;
                var nested = FindByName(i.Children, name);
                if (nested is not null)
                    return nested;
            }
            return null;
        }

        static TaskChecklistItem? FindItemById(IReadOnlyList<TaskChecklistItem> items, string id)
        {
            foreach (var i in items)
            {
                if (i.Id == id)
                    return i;
                var nested = FindItemById(i.Children, id);
                if (nested is not null)
                    return nested;
            }
            return null;
        }
    }

    [SkippableFact]
    public async Task CreateRenameDeleteChecklistGroup_RoundTrips_AndCleansUp()
    {
        // Group CRUD (F, #459): on a throwaway task, create a checklist, add an item to it, rename the
        // checklist, then delete the checklist (which also removes its items) — proving each group write
        // against the live API. The scratch task is created on CLICKUP_LIST_ID and deleted at the end, so
        // the run leaves no residue (self-contained; doesn't need a pre-seeded checklist-bearing task).
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(ListId),
            "Set CLICKUP_TOKEN and CLICKUP_LIST_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        var created = await client.CreateTaskAsync(ListId!, new NewTaskRequest
        {
            Name = "[clickup-todo-cli test] checklist-group CRUD — safe to delete",
            Description = "Created by the checklist-group CRUD integration test.",
        });

        try
        {
            // Create a checklist group on the task.
            var groupName = $"clickup-todo integration checklist {Guid.NewGuid():N}";
            var group = await client.CreateChecklistAsync(created.Id, groupName);
            Assert.False(string.IsNullOrWhiteSpace(group.Id));
            Assert.Equal(groupName, group.Name);

            // Add an item so the delete confirmation's "and its N items" path is exercised server-side.
            var afterItem = await client.CreateChecklistItemAsync(group.Id, "an item");
            Assert.Contains(afterItem.Items, i => i.Name == "an item");

            // Rename the group; the echoed checklist carries the new name.
            var renamedName = $"clickup-todo integration renamed {Guid.NewGuid():N}";
            var renamed = await client.RenameChecklistAsync(group.Id, renamedName);
            Assert.Equal(group.Id, renamed.Id);
            Assert.Equal(renamedName, renamed.Name);

            // Delete the group; a re-fetch of the task must no longer carry it (items go with it).
            await client.DeleteChecklistAsync(group.Id);
            var afterDelete = await client.GetTaskDetailAsync(created.Id);
            Assert.DoesNotContain(afterDelete.Checklists, c => c.Id == group.Id);
        }
        finally
        {
            await client.DeleteTaskAsync(created.Id);
        }
    }

    [SkippableFact]
    public async Task GetTaskComments_ReturnsComments()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        var comments = await client.GetTaskCommentsAsync(TaskId!);

        // May legitimately be empty, but every returned comment must have an id.
        Assert.All(comments, c => Assert.False(string.IsNullOrWhiteSpace(c.Id)));
    }

    [SkippableFact]
    public async Task CreateTaskComment_PostsPlainText_AndAppearsOnRefetch()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);
        // Unique marker so the re-fetch assertion can find exactly this comment (it posts real data).
        var text = $"clickup-todo integration test comment {Guid.NewGuid():N}";

        var created = await client.CreateTaskCommentAsync(TaskId!, text);

        Assert.False(string.IsNullOrWhiteSpace(created.Id));
        Assert.Equal(text, created.Text);
        Assert.Equal(TaskId, created.TaskId);

        var comments = await client.GetTaskCommentsAsync(TaskId!);
        Assert.Contains(comments, c => c.Id == created.Id && c.Text == text);
    }

    [SkippableFact]
    public async Task CreateTaskComment_WithMention_MaterializesMention_OnRefetch()
    {
        // The G spike (#321) confirmed the comment-mention write shape from ClickUp's published example
        // but not from a captured live request, flagging one doubt: does a bare { type:"tag", user:{id} }
        // block (no `attributes`) really materialize a mention? This is that confirmation gate (#322).
        // It self-mentions the authenticated user so no colleague is notified by the test.
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);
        var me = await client.GetMeAsync();
        // Unique marker text so the re-fetch can find exactly this comment; the mention self-tags `me`.
        var marker = $"clickup-todo integration mention test {Guid.NewGuid():N}";

        var created = await client.CreateTaskCommentAsync(
            TaskId!,
            [new CommentRun.Text(marker + " "), new CommentRun.Mention(me.Id)]);

        Assert.False(string.IsNullOrWhiteSpace(created.Id));
        Assert.Contains(me.Id, created.MentionedUserIds);
        Assert.Equal(TaskId, created.TaskId);

        // On read-back ClickUp echoes the persisted structured blocks, so the mapped comment's
        // MentionedUserIds (#167) must contain the tagged id — proving the tag block materialized.
        var comments = await client.GetTaskCommentsAsync(TaskId!);
        Assert.Contains(comments, c => c.Id == created.Id && c.MentionedUserIds.Contains(me.Id));
    }

    [SkippableFact]
    public async Task GetThreadedComments_ReturnsReplies()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(CommentId),
            "Set CLICKUP_TOKEN and CLICKUP_COMMENT_ID (a comment that has replies) to run this test.");
        using var client = new ClickUpClient(Token!);

        var replies = await client.GetThreadedCommentsAsync(CommentId!);

        // May legitimately be empty, but every returned reply must have an id.
        Assert.All(replies, r => Assert.False(string.IsNullOrWhiteSpace(r.Id)));
    }

    [SkippableFact]
    public async Task CreateThreadedComment_PostsReply_AndAppearsOnRefetch()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(CommentId),
            "Set CLICKUP_TOKEN and CLICKUP_COMMENT_ID (a comment to reply to) to run this test.");
        using var client = new ClickUpClient(Token!);
        // Unique marker so the re-fetch assertion can find exactly this reply (it posts real data).
        var text = $"clickup-todo integration test reply {Guid.NewGuid():N}";

        var created = await client.CreateThreadedCommentAsync(CommentId!, text);

        Assert.False(string.IsNullOrWhiteSpace(created.Id));
        Assert.Equal(text, created.Text);

        var replies = await client.GetThreadedCommentsAsync(CommentId!);
        Assert.Contains(replies, r => r.Id == created.Id && r.Text == text);
    }

    [SkippableFact]
    public async Task BadToken_IsReportedAsAuthFailure()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token), "Set CLICKUP_TOKEN to run ClickUp integration tests.");
        using var client = new ClickUpClient("pk_0_INVALIDTOKENVALUE");

        var ex = await Assert.ThrowsAsync<ClickUpApiException>(() => client.GetMeAsync());

        Assert.True(ex.IsAuthFailure);
    }

    [SkippableFact]
    public async Task SetAndClearCustomField_RoundTripsOnAnExistingTask()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(ListId),
            "Set CLICKUP_TOKEN and CLICKUP_LIST_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        // Need a text-like field on the list to write a safe, free-form string value into (a drop-down /
        // labels field would need a real option id). Skip cleanly if the list has none.
        var fields = await client.GetListCustomFieldsAsync(ListId!);
        var textField = fields.FirstOrDefault(f =>
            string.Equals(f.Type, "text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.Type, "short_text", StringComparison.OrdinalIgnoreCase));
        Skip.If(textField is null, "CLICKUP_LIST_ID has no text/short_text custom field to round-trip.");

        // A throwaway task so the write never touches real data; the finally deletes it.
        var name = "[clickup-todo-cli test] custom-field write — safe to delete";
        var created = await client.CreateTaskAsync(ListId!, new NewTaskRequest { Name = name });
        try
        {
            var written = $"cf-roundtrip-{Guid.NewGuid():N}";

            // Set → re-fetch → the value round-trips through the detail read.
            await client.SetTaskCustomFieldAsync(created.Id, textField!.Id, JsonSerializer.SerializeToElement(written));
            var afterSet = await client.GetTaskDetailAsync(created.Id);
            var setField = afterSet.CustomFields.FirstOrDefault(f => f.Id == textField.Id);
            Assert.NotNull(setField);
            Assert.Equal(written, setField!.Value?.GetString());

            // Clear → re-fetch → the value is gone (dropped from the values list, or read back null/empty).
            await client.ClearTaskCustomFieldAsync(created.Id, textField.Id);
            var afterClear = await client.GetTaskDetailAsync(created.Id);
            var clearedField = afterClear.CustomFields.FirstOrDefault(f => f.Id == textField.Id);
            Assert.True(
                clearedField?.Value is null
                || clearedField.Value.Value.ValueKind == JsonValueKind.Null
                || (clearedField.Value.Value.ValueKind == JsonValueKind.String
                    && string.IsNullOrEmpty(clearedField.Value.Value.GetString())),
                "a cleared custom field should read back with no value.");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(created.Id))
                await client.DeleteTaskAsync(created.Id);
        }
    }
}
