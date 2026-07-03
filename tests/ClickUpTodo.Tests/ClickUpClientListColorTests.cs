using System.Text.Json;
using ClickUpTodo.ClickUp;
using Microsoft.Kiota.Serialization.Json;
using GeneratedList = ClickUpTodo.ClickUp.Generated.Models.List;

namespace ClickUpTodo.Tests;

/// <summary>
/// Guards the defensive extraction of a list's color chip out of Kiota's additional data. ClickUp
/// returns the color under a <c>status</c> field the generated model doesn't map, so this runs the
/// real Kiota deserializer over captured API JSON to prove the field lands where the extractor looks.
/// </summary>
public sealed class ClickUpClientListColorTests
{
    private static GeneratedList Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var node = new JsonParseNode(doc.RootElement);
        return node.GetObjectValue(GeneratedList.CreateFromDiscriminatorValue)!;
    }

    [Fact]
    public void ExtractListColor_ReadsColorFromUnmappedStatusField()
    {
        // Captured from GET /v2/list/901404779420 (CoreOps - Production Support).
        const string json = """
            {
              "id": "901404779420",
              "name": "CoreOps - Production Support",
              "status": { "status": "#e16b16", "color": "#e16b16", "hide_label": true },
              "statuses": []
            }
            """;
        Assert.Equal("#e16b16", ClickUpClient.ExtractListColor(Parse(json)));
    }

    [Fact]
    public void ExtractListColor_ReturnsNull_WhenListHasNoStatusField()
    {
        const string json = """{ "id": "1", "name": "No color list", "statuses": [] }""";
        Assert.Null(ClickUpClient.ExtractListColor(Parse(json)));
    }

    [Fact]
    public void ExtractListColor_ReturnsNull_WhenStatusHasNoColor()
    {
        const string json = """{ "id": "1", "name": "L", "status": { "hide_label": true }, "statuses": [] }""";
        Assert.Null(ClickUpClient.ExtractListColor(Parse(json)));
    }

    [Fact]
    public void ExtractListColor_ReturnsNull_ForNullList() => Assert.Null(ClickUpClient.ExtractListColor(null));
}
