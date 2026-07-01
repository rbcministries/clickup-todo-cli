using System.Text;
using ClickUpTodo.ClickUp;
using ClickUpTodo.ClickUp.Generated.Models;
using ClickUpTodo.Tui;
using Microsoft.Kiota.Serialization.Json;

namespace ClickUpTodo.Tests;

/// <summary>
/// Verifies the one Kiota-touching seam of the custom-field feature (issue #35):
/// <see cref="ClickUpClient.MapCustomField"/> re-serializes a generated <see cref="CustomField"/>
/// (whose loosely-typed <c>value</c>/<c>type_config</c> live in Kiota's <c>AdditionalData</c>) back
/// to JSON and reads the value + options out of it. Exercises the real deserialize→map round-trip
/// with no network, so it runs as a plain <see cref="FactAttribute"/>.
/// </summary>
public sealed class ClickUpClientCustomFieldTests
{
    private static async Task<CustomField> DeserializeAsync(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var node = await new JsonParseNodeFactory().GetRootParseNodeAsync("application/json", stream);
        return node.GetObjectValue(CustomField.CreateFromDiscriminatorValue)!;
    }

    [Fact]
    public async Task MapCustomField_DropDown_SurfacesValueAndOptions()
    {
        var cf = await DeserializeAsync("""
            {"id":"cf1","name":"Sprint","type":"drop_down","value":1,
             "type_config":{"options":[
                {"id":"o0","name":"Backlog","orderindex":0},
                {"id":"o1","name":"In progress","orderindex":1}]}}
            """);

        var item = ClickUpClient.MapCustomField(cf);

        Assert.Equal("Sprint", item.Name);
        Assert.Equal("drop_down", item.Type);
        Assert.Equal(2, item.Options.Count);
        // The value + options survived the AdditionalData round-trip and resolve to the option name.
        Assert.Equal("In progress", TaskDetailFormatter.CustomFieldValue(item));
    }

    [Fact]
    public async Task MapCustomField_ScalarValue_RoundTrips()
    {
        var cf = await DeserializeAsync("""{"id":"cf2","name":"Estimate","type":"number","value":8}""");

        var item = ClickUpClient.MapCustomField(cf);

        Assert.NotNull(item.Value);
        Assert.Equal("8", TaskDetailFormatter.CustomFieldValue(item));
        Assert.Empty(item.Options);
    }

    [Fact]
    public async Task MapCustomField_NoValue_LeavesValueNull()
    {
        var cf = await DeserializeAsync("""{"id":"cf3","name":"Notes","type":"text"}""");

        var item = ClickUpClient.MapCustomField(cf);

        Assert.Null(item.Value);
        Assert.Null(TaskDetailFormatter.CustomFieldValue(item));
    }
}
