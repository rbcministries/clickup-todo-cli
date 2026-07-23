using System.Text;
using ClickUpTodo.ClickUp;
using Microsoft.Kiota.Serialization.Json;
using GenCustomFieldDefinition = ClickUpTodo.ClickUp.Generated.Models.CustomFieldDefinition;

namespace ClickUpTodo.Tests;

/// <summary>
/// Verifies the Kiota-touching seam of the list custom-field <b>definitions</b> fetch (#249):
/// <see cref="ClickUpClient.MapCustomFieldDefinition"/> maps a generated field definition (whose
/// loosely-typed <c>type_config</c> lives in Kiota's <c>AdditionalData</c>) onto the stable
/// <see cref="CustomFieldDefinition"/>, taking <c>id/name/type/required</c> from the typed properties
/// and the drop-down/label options from the re-serialized <c>type_config.options</c>. Exercises the
/// real deserialize→map round-trip with no network, mirroring <see cref="ClickUpClientCustomFieldTests"/>.
/// </summary>
public sealed class ClickUpClientCustomFieldDefinitionsTests
{
    private static async Task<GenCustomFieldDefinition> DeserializeAsync(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var node = await new JsonParseNodeFactory().GetRootParseNodeAsync("application/json", stream);
        return node.GetObjectValue(GenCustomFieldDefinition.CreateFromDiscriminatorValue)!;
    }

    [Fact]
    public async Task Map_DropDown_SurfacesRequiredAndOptions()
    {
        var def = await DeserializeAsync("""
            {"id":"cf1","name":"Stage","type":"drop_down","required":true,
             "type_config":{"options":[
                {"id":"o0","name":"Backlog","orderindex":0},
                {"id":"o1","name":"In progress","orderindex":1}]}}
            """);

        var mapped = ClickUpClient.MapCustomFieldDefinition(def);

        Assert.Equal("cf1", mapped.Id);
        Assert.Equal("Stage", mapped.Name);
        Assert.Equal("drop_down", mapped.Type);
        Assert.True(mapped.Required);
        Assert.Equal(2, mapped.Options.Count);
        Assert.Equal(("o0", "Backlog"), (mapped.Options[0].Id, mapped.Options[0].Name));
        Assert.Equal(("o1", "In progress"), (mapped.Options[1].Id, mapped.Options[1].Name));
    }

    [Fact]
    public async Task Map_Labels_MapsLabelToOptionName()
    {
        // labels-type options name the choice via "label" (not "name") — the shared reader normalizes it.
        var def = await DeserializeAsync("""
            {"id":"cf2","name":"Audience","type":"labels","required":false,
             "type_config":{"options":[
                {"id":"l0","label":"Donors","orderindex":0},
                {"id":"l1","label":"Churches","orderindex":1}]}}
            """);

        var mapped = ClickUpClient.MapCustomFieldDefinition(def);

        Assert.Equal("labels", mapped.Type);
        Assert.False(mapped.Required);
        Assert.Equal(["Donors", "Churches"], mapped.Options.Select(o => o.Name));
    }

    [Fact]
    public async Task Map_ScalarField_HasNoOptions()
    {
        var def = await DeserializeAsync("""{"id":"cf3","name":"Estimate","type":"number","required":true}""");

        var mapped = ClickUpClient.MapCustomFieldDefinition(def);

        Assert.Equal("number", mapped.Type);
        Assert.True(mapped.Required);
        Assert.Empty(mapped.Options);
    }

    [Fact]
    public async Task Map_RequiredAbsent_DefaultsToFalse()
    {
        // The #249 spike confirmed the API surfaces `required`; when a field omits it we default to false.
        var def = await DeserializeAsync("""{"id":"cf4","name":"Notes","type":"text"}""");

        var mapped = ClickUpClient.MapCustomFieldDefinition(def);

        Assert.False(mapped.Required);
        Assert.Empty(mapped.Options);
    }

    [Fact]
    public async Task Map_MalformedTypeConfig_DegradesToIdentityOnly()
    {
        // A type_config that isn't the expected object/array shape must not sink the field: it degrades
        // to identity + required with no options (mirroring MapCustomField's defensive fallback).
        var def = await DeserializeAsync("""
            {"id":"cf5","name":"Weird","type":"drop_down","required":true,"type_config":"not-an-object"}
            """);

        var mapped = ClickUpClient.MapCustomFieldDefinition(def);

        Assert.Equal("cf5", mapped.Id);
        Assert.True(mapped.Required);
        Assert.Empty(mapped.Options);
    }
}
