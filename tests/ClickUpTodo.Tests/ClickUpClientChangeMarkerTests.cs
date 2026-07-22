using System.Net;
using System.Text;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline tests that the <see cref="ClickUpClient"/> facade emits a change-marker nudge (#294) after —
/// and only after — a confirmed (2xx) write, carrying the server-confirmed <c>date_updated</c> parsed
/// from the write response. They drive the real generated client through a fake
/// <see cref="HttpMessageHandler"/> (no token, no network) with a recording <see cref="IChangeMarkerStore"/>.
/// </summary>
public sealed class ClickUpClientChangeMarkerTests
{
    private const long ConfirmedDate = 1_700_000_000_000;

    [Fact]
    public async Task SetTaskStatus_OnSuccess_RecordsMarkerWithConfirmedDateUpdated()
    {
        var recorder = new RecordingChangeMarkerStore();
        using var client = new ClickUpClient(
            "pk_x", new HttpClient(Ok($$"""{ "id": "t1", "status": { "status": "done" }, "date_updated": "{{ConfirmedDate}}" }""")),
            changeMarkers: recorder);

        await client.SetTaskStatusAsync("t1", "done");

        var m = Assert.Single(recorder.Records);
        Assert.Equal("t1", m.TaskId);
        Assert.Equal(ConfirmedDate, m.ServerDateUpdatedMs);
        Assert.Equal(["status"], m.ChangedFields);
    }

    [Fact]
    public async Task SetTaskPriority_OnSuccess_RecordsPriorityMarker()
    {
        var recorder = new RecordingChangeMarkerStore();
        using var client = new ClickUpClient(
            "pk_x", new HttpClient(Ok($$"""{ "id": "t1", "priority": { "id": "1" }, "date_updated": "{{ConfirmedDate}}" }""")),
            changeMarkers: recorder);

        await client.SetTaskPriorityAsync("t1", 1);

        var m = Assert.Single(recorder.Records);
        Assert.Equal("t1", m.TaskId);
        Assert.Equal(ConfirmedDate, m.ServerDateUpdatedMs);
        Assert.Equal(["priority"], m.ChangedFields);
    }

    [Fact]
    public async Task SetTaskDescription_OnSuccess_RecordsDescriptionMarker()
    {
        var recorder = new RecordingChangeMarkerStore();
        using var client = new ClickUpClient(
            "pk_x", new HttpClient(Ok($$"""{ "id": "t1", "text_content": "x", "date_updated": "{{ConfirmedDate}}" }""")),
            changeMarkers: recorder);

        await client.SetTaskDescriptionAsync("t1", "x");

        var m = Assert.Single(recorder.Records);
        Assert.Equal(["description"], m.ChangedFields);
        Assert.Equal(ConfirmedDate, m.ServerDateUpdatedMs);
    }

    [Fact]
    public async Task AddTaskAssignee_OnSuccess_RecordsAssigneesMarker()
    {
        var recorder = new RecordingChangeMarkerStore();
        using var client = new ClickUpClient(
            "pk_x", new HttpClient(Ok($$"""{ "id": "t1", "assignees": [ { "id": 1 } ], "date_updated": "{{ConfirmedDate}}" }""")),
            changeMarkers: recorder);

        await client.AddTaskAssigneeAsync("t1", 1);

        var m = Assert.Single(recorder.Records);
        Assert.Equal(["assignees"], m.ChangedFields);
        Assert.Equal(ConfirmedDate, m.ServerDateUpdatedMs);
    }

    [Fact]
    public async Task RemoveTaskAssignee_OnSuccess_RecordsAssigneesMarker()
    {
        var recorder = new RecordingChangeMarkerStore();
        using var client = new ClickUpClient(
            "pk_x", new HttpClient(Ok("""{ "id": "t1", "assignees": [] }""")),
            changeMarkers: recorder);

        await client.RemoveTaskAssigneeAsync("t1", 1);

        var m = Assert.Single(recorder.Records);
        Assert.Equal("t1", m.TaskId);
        Assert.Equal(["assignees"], m.ChangedFields);
    }

    [Fact]
    public async Task CreateTaskComment_OnSuccess_RecordsCommentMarkerWithNullServerDate()
    {
        // The create-comment response carries only the comment's own id/date, not the task's
        // date_updated — so the nudge carries a null serverDateUpdated by design.
        var recorder = new RecordingChangeMarkerStore();
        using var client = new ClickUpClient(
            "pk_x", new HttpClient(Ok("""{ "id": "c1", "date": "1700000000001" }""")),
            changeMarkers: recorder);

        await client.CreateTaskCommentAsync("t1", "hello");

        var m = Assert.Single(recorder.Records);
        Assert.Equal("t1", m.TaskId);
        Assert.Null(m.ServerDateUpdatedMs);
        Assert.Equal(["comment"], m.ChangedFields);
    }

    [Fact]
    public async Task Write_MissingDateUpdatedInResponse_RecordsMarkerWithNullServerDate()
    {
        var recorder = new RecordingChangeMarkerStore();
        using var client = new ClickUpClient(
            "pk_x", new HttpClient(Ok("""{ "id": "t1", "status": { "status": "done" } }""")),
            changeMarkers: recorder);

        await client.SetTaskStatusAsync("t1", "done");

        Assert.Null(Assert.Single(recorder.Records).ServerDateUpdatedMs);
    }

    [Fact]
    public async Task FailedWrite_RecordsNoMarker()
    {
        // The 2xx gate: a non-success response throws before the nudge is reached, so nothing is recorded.
        var recorder = new RecordingChangeMarkerStore();
        using var client = new ClickUpClient(
            "pk_x", new HttpClient(Status(HttpStatusCode.BadRequest, """{ "err": "nope" }""")),
            changeMarkers: recorder);

        await Assert.ThrowsAsync<ClickUpApiException>(() => client.SetTaskStatusAsync("t1", "done"));

        Assert.Empty(recorder.Records);
    }

    [Fact]
    public async Task Write_WithNoMarkerStore_DoesNotThrow()
    {
        // The channel is opt-in: a client built without a marker store writes exactly as before.
        using var client = new ClickUpClient(
            "pk_x", new HttpClient(Ok($$"""{ "id": "t1", "status": { "status": "done" }, "date_updated": "{{ConfirmedDate}}" }""")));

        var confirmed = await client.SetTaskStatusAsync("t1", "done");

        Assert.Equal("done", confirmed);
    }

    private static StubHandler Ok(string body) => new(HttpStatusCode.OK, body);

    private static StubHandler Status(HttpStatusCode code, string body) => new(code, body);

    /// <summary>Returns a canned response with the given status and body.</summary>
    private sealed class StubHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>Captures every <see cref="IChangeMarkerStore.Record"/> call for assertions.</summary>
    private sealed class RecordingChangeMarkerStore : IChangeMarkerStore
    {
        public List<(string TaskId, long? ServerDateUpdatedMs, IReadOnlyList<string> ChangedFields)> Records { get; } = [];

        public string InstanceId => "test";

        public void Record(string taskId, long? serverDateUpdatedMs, IReadOnlyList<string> changedFields)
            => Records.Add((taskId, serverDateUpdatedMs, changedFields));

        public IReadOnlyList<ChangeMarker> ReadAll() => [];
    }
}
