using System.Net;
using System.Text;
using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline tests for the per-task Custom Field write path on the <see cref="ClickUpClient"/> facade
/// (#587 §1): <see cref="ClickUpClient.SetTaskCustomFieldAsync"/> and
/// <see cref="ClickUpClient.ClearTaskCustomFieldAsync"/>. They drive the real generated client through a
/// capturing <see cref="HttpMessageHandler"/> (no token, no network), asserting the outgoing
/// <c>POST</c>/<c>DELETE /v2/task/{id}/field/{fid}</c> shape, that the polymorphic <c>value</c> JSON kind
/// is preserved through the Kiota serializer, that a blank field id fails fast client-side, and that a
/// confirmed write records the <c>custom_fields</c> change marker — mirroring
/// <see cref="ClickUpClientCreateTaskTests"/> and <see cref="ClickUpClientChangeMarkerTests"/>.
/// </summary>
public sealed class ClickUpClientCustomFieldWriteTests
{
    [Fact]
    public async Task SetCustomField_PostsToFieldEndpoint_WithValueBody()
    {
        // ClickUp returns an empty object for a field set.
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.SetTaskCustomFieldAsync("t1", "cf_text", JsonSerializer.SerializeToElement("hello"));

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("/v2/task/t1/field/cf_text", handler.RequestUri);
        var body = handler.Body!.RootElement;
        Assert.Equal("hello", body.GetProperty("value").GetString());
    }

    [Theory]
    [MemberData(nameof(ValueKinds))]
    public async Task SetCustomField_PreservesValueJsonKind(JsonElement value, JsonValueKind expected)
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.SetTaskCustomFieldAsync("t1", "cf", value);

        var sent = handler.Body!.RootElement.GetProperty("value");
        Assert.Equal(expected, sent.ValueKind);
    }

    public static IEnumerable<object[]> ValueKinds() =>
    [
        [JsonSerializer.SerializeToElement("s"), JsonValueKind.String],
        [JsonSerializer.SerializeToElement(42L), JsonValueKind.Number],
        [JsonSerializer.SerializeToElement(19.99), JsonValueKind.Number],
        [JsonSerializer.SerializeToElement(true), JsonValueKind.True],
        [JsonSerializer.SerializeToElement(new[] { "l1", "l2" }), JsonValueKind.Array],
        [JsonSerializer.SerializeToElement((string?)null), JsonValueKind.Null],
    ];

    [Fact]
    public async Task SetCustomField_LabelsArray_SentAsArrayOfIds()
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.SetTaskCustomFieldAsync("t1", "cf_labels",
            JsonSerializer.SerializeToElement(new[] { "opt1", "opt2" }));

        var value = handler.Body!.RootElement.GetProperty("value");
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
        Assert.Equal(["opt1", "opt2"], value.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task SetCustomField_NumberValue_KeepsNumericKind()
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.SetTaskCustomFieldAsync("t1", "cf_num", JsonSerializer.SerializeToElement(7L));

        var value = handler.Body!.RootElement.GetProperty("value");
        Assert.Equal(JsonValueKind.Number, value.ValueKind);
        Assert.Equal(7L, value.GetInt64());
    }

    [Fact]
    public async Task ClearCustomField_DeletesFieldEndpoint_WithNoBody()
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.ClearTaskCustomFieldAsync("t1", "cf_text");

        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Contains("/v2/task/t1/field/cf_text", handler.RequestUri);
        Assert.Null(handler.Body); // DELETE carries no request body
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetCustomField_BlankFieldId_Throws_WithoutHittingTheNetwork(string fieldId)
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.SetTaskCustomFieldAsync("t1", fieldId, JsonSerializer.SerializeToElement("x")));

        Assert.Null(handler.Method); // never reached the transport
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ClearCustomField_BlankFieldId_Throws_WithoutHittingTheNetwork(string fieldId)
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ClearTaskCustomFieldAsync("t1", fieldId));

        Assert.Null(handler.Method);
    }

    [Fact]
    public async Task SetCustomField_OnSuccess_RecordsCustomFieldMarker()
    {
        var recorder = new RecordingChangeMarkerStore();
        using var client = new ClickUpClient("pk_x", new HttpClient(new CapturingHandler("{}")), changeMarkers: recorder);

        await client.SetTaskCustomFieldAsync("t1", "cf", JsonSerializer.SerializeToElement("x"));

        var m = Assert.Single(recorder.Records);
        Assert.Equal("t1", m.TaskId);
        Assert.Null(m.ServerDateUpdatedMs); // empty body ⇒ null server date ⇒ consumer always re-fetches
        Assert.Equal(["custom_fields"], m.ChangedFields);
    }

    [Fact]
    public async Task ClearCustomField_OnSuccess_RecordsCustomFieldMarker()
    {
        var recorder = new RecordingChangeMarkerStore();
        using var client = new ClickUpClient("pk_x", new HttpClient(new CapturingHandler("{}")), changeMarkers: recorder);

        await client.ClearTaskCustomFieldAsync("t1", "cf");

        var m = Assert.Single(recorder.Records);
        Assert.Equal(["custom_fields"], m.ChangedFields);
        Assert.Null(m.ServerDateUpdatedMs);
    }

    [Fact]
    public async Task SetCustomField_OnApiError_Throws_AndRecordsNoMarker()
    {
        var recorder = new RecordingChangeMarkerStore();
        using var client = new ClickUpClient(
            "pk_x", new HttpClient(new CapturingHandler("""{ "err": "field not found" }""", HttpStatusCode.BadRequest)),
            changeMarkers: recorder);

        await Assert.ThrowsAsync<ClickUpApiException>(() =>
            client.SetTaskCustomFieldAsync("t1", "cf", JsonSerializer.SerializeToElement("x")));

        Assert.Empty(recorder.Records); // no marker on a non-2xx
    }

    /// <summary>Records the outgoing request (method, URI, parsed JSON body) and returns a canned body.</summary>
    private sealed class CapturingHandler(string responseBody, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? RequestUri { get; private set; }
        public JsonDocument? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri?.ToString();
            if (request.Content is not null)
                Body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(code)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
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
