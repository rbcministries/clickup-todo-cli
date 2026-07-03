using System.Text.Json;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="CustomFieldReader"/> — the pure extraction of a custom field's
/// loosely-typed <c>value</c> and <c>type_config.options</c> from raw JSON (issue #35).
/// </summary>
public sealed class CustomFieldReaderTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Read_SurfacesScalarValue()
    {
        var (value, options) = CustomFieldReader.Read(Parse("""{"id":"c","name":"N","type":"number","value":42}"""));
        Assert.NotNull(value);
        Assert.Equal(JsonValueKind.Number, value!.Value.ValueKind);
        Assert.Equal(42, value.Value.GetInt32());
        Assert.Empty(options);
    }

    [Fact]
    public void Read_AbsentValue_IsNull()
    {
        var (value, _) = CustomFieldReader.Read(Parse("""{"id":"c","name":"N","type":"text"}"""));
        Assert.Null(value);
    }

    [Fact]
    public void Read_JsonNullValue_IsNull()
    {
        var (value, _) = CustomFieldReader.Read(Parse("""{"name":"N","value":null}"""));
        Assert.Null(value);
    }

    [Fact]
    public void Read_ValueOutlivesSourceDocument()
    {
        // Read must clone the value so it survives after the backing JsonDocument is disposed.
        JsonElement? value;
        using (var doc = JsonDocument.Parse("""{"value":"kept"}"""))
            (value, _) = CustomFieldReader.Read(doc.RootElement);
        Assert.Equal("kept", value!.Value.GetString());
    }

    [Fact]
    public void Read_ParsesDropDownOptions()
    {
        var json = """
            {"name":"Sprint","type":"drop_down","value":1,
             "type_config":{"options":[
                {"id":"o0","name":"Backlog","orderindex":0},
                {"id":"o1","name":"In progress","orderindex":1}]}}
            """;
        var (_, options) = CustomFieldReader.Read(Parse(json));
        Assert.Equal(2, options.Count);
        Assert.Equal("o1", options[1].Id);
        Assert.Equal("In progress", options[1].Name);
        Assert.Equal(1, options[1].OrderIndex);
    }

    [Fact]
    public void Read_LabelsOptions_UseLabelWhenNameAbsent()
    {
        var json = """{"type":"labels","type_config":{"options":[{"id":"x","label":"Important"}]}}""";
        var (_, options) = CustomFieldReader.Read(Parse(json));
        Assert.Single(options);
        Assert.Equal("Important", options[0].Name);
        Assert.Null(options[0].OrderIndex);
    }

    [Fact]
    public void Read_MissingTypeConfig_YieldsNoOptions()
    {
        var (_, options) = CustomFieldReader.Read(Parse("""{"type":"drop_down","value":0}"""));
        Assert.Empty(options);
    }

    [Fact]
    public void Read_SkipsNonObjectOptionEntries()
    {
        var json = """{"type_config":{"options":["oops",{"id":"a","name":"Alpha"}]}}""";
        var (_, options) = CustomFieldReader.Read(Parse(json));
        Assert.Single(options);
        Assert.Equal("Alpha", options[0].Name);
    }

    [Fact]
    public void Read_NonObjectField_ReturnsEmpty()
    {
        var (value, options) = CustomFieldReader.Read(Parse("\"not an object\""));
        Assert.Null(value);
        Assert.Empty(options);
    }
}
