using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="ListMembershipMigration"/> (#365) — the pure strand-detection used when
/// removing a task from an additional list. Covers: a list-local set value strands; a value still
/// covered by a remaining list (incl. Space-level fields, which appear on every list) does not; unset
/// and blank-id fields never strand; the safe-side fallback when a preflight fetch is missing; and the
/// <see cref="ListMembershipMigration.HasValue"/> "is a value actually set" predicate per JSON kind.
/// </summary>
public sealed class ListMembershipMigrationTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    // A task field with an id, name, and a set value (JSON), for strand tests.
    private static CustomFieldItem Field(string id, string name, string valueJson = "\"x\"")
        => new(name, "text", Json(valueJson), Id: id);

    // A list field definition (only id/name matter to the strand logic).
    private static CustomFieldDefinition Def(string id, string? name = null)
        => new(id, name ?? $"Field {id}", "text", false);

    private static Dictionary<string, IReadOnlyList<CustomFieldDefinition>> Defs(
        params (string ListId, CustomFieldDefinition[] Fields)[] entries)
        => entries.ToDictionary(e => e.ListId, e => (IReadOnlyList<CustomFieldDefinition>)e.Fields);

    [Fact]
    public void ListLocalSetValue_IsStranded()
    {
        // Field f1 is defined only by the removed list "A"; the task keeps list "B" which doesn't define it.
        var fields = new[] { Field("f1", "Sprint Points") };
        var defs = Defs(("A", [Def("f1")]), ("B", [Def("f2")]));

        var stranded = ListMembershipMigration.StrandedFieldsOnRemove(fields, "A", defs, ["B"]);

        Assert.Equal(["Sprint Points"], stranded);
    }

    [Fact]
    public void ValueCoveredByRemainingList_IsNotStranded()
    {
        // f1 lives on both A (removed) and B (remaining) — removing A doesn't hide it.
        var fields = new[] { Field("f1", "Sprint Points") };
        var defs = Defs(("A", [Def("f1")]), ("B", [Def("f1")]));

        var stranded = ListMembershipMigration.StrandedFieldsOnRemove(fields, "A", defs, ["B"]);

        Assert.Empty(stranded);
    }

    [Fact]
    public void SpaceLevelField_AppearsOnEveryList_IsNeverStranded()
    {
        // A Space-level field is returned by GET /list/{id}/field for every list, so it's on the
        // remaining list too and never flagged.
        var fields = new[] { Field("space1", "Owner") };
        var defs = Defs(("A", [Def("space1")]), ("B", [Def("space1")]));

        var stranded = ListMembershipMigration.StrandedFieldsOnRemove(fields, "A", defs, ["B"]);

        Assert.Empty(stranded);
    }

    [Fact]
    public void UnsetField_NeverStrands()
    {
        // No value set → nothing to lose, even though f1 is list-local to A.
        var fields = new[] { new CustomFieldItem("Empty", "text", Value: null, Id: "f1") };
        var defs = Defs(("A", [Def("f1")]), ("B", [Def("f2")]));

        var stranded = ListMembershipMigration.StrandedFieldsOnRemove(fields, "A", defs, ["B"]);

        Assert.Empty(stranded);
    }

    [Fact]
    public void BlankIdField_IsSkipped()
    {
        var fields = new[] { new CustomFieldItem("No Id", "text", Json("\"v\""), Id: "  ") };
        var defs = Defs(("A", [Def("f1")]), ("B", [Def("f2")]));

        var stranded = ListMembershipMigration.StrandedFieldsOnRemove(fields, "A", defs, ["B"]);

        Assert.Empty(stranded);
    }

    [Fact]
    public void RemovedListDefinitionsMissing_ConservativelyFlagsSetValues()
    {
        // Preflight for the removed list "A" failed (absent from the map). A set value not clearly
        // covered by a known remaining list is flagged rather than silently removed.
        var fields = new[] { Field("f1", "Sprint Points"), Field("f2", "Owner") };
        var defs = Defs(("B", [Def("f2")])); // "A" missing; B covers f2 only.

        var stranded = ListMembershipMigration.StrandedFieldsOnRemove(fields, "A", defs, ["B"]);

        // f2 is covered by remaining list B; f1 is not → flagged.
        Assert.Equal(["Sprint Points"], stranded);
    }

    [Fact]
    public void RemainingListDefinitionsMissing_DoesNotRescue()
    {
        // The remaining list "B" couldn't be fetched, so it can't be relied on to still define f1.
        var fields = new[] { Field("f1", "Sprint Points") };
        var defs = Defs(("A", [Def("f1")])); // B missing.

        var stranded = ListMembershipMigration.StrandedFieldsOnRemove(fields, "A", defs, ["B"]);

        Assert.Equal(["Sprint Points"], stranded);
    }

    [Fact]
    public void MultipleRemainingLists_AnyCoverageRescues()
    {
        var fields = new[] { Field("f1", "Sprint Points"), Field("f2", "Owner") };
        var defs = Defs(("A", [Def("f1"), Def("f2")]), ("B", [Def("f1")]), ("C", [Def("f2")]));

        var stranded = ListMembershipMigration.StrandedFieldsOnRemove(fields, "A", defs, ["B", "C"]);

        // f1 covered by B, f2 covered by C → nothing stranded.
        Assert.Empty(stranded);
    }

    [Fact]
    public void DistinctByName_PreservesFieldOrder()
    {
        var fields = new[] { Field("f2", "Beta"), Field("f1", "Alpha") };
        var defs = Defs(("A", [Def("f1"), Def("f2")]), ("B", []));

        var stranded = ListMembershipMigration.StrandedFieldsOnRemove(fields, "A", defs, ["B"]);

        Assert.Equal(["Beta", "Alpha"], stranded);
    }

    [Fact]
    public void DuplicateFieldId_IsReportedOnce()
    {
        // Same field id present twice on the task (defensive) → the name appears once (dedup by id).
        var fields = new[] { Field("f1", "Sprint Points"), Field("f1", "Sprint Points") };
        var defs = Defs(("A", [Def("f1")]), ("B", []));

        var stranded = ListMembershipMigration.StrandedFieldsOnRemove(fields, "A", defs, ["B"]);

        Assert.Equal(["Sprint Points"], stranded);
    }

    [Fact]
    public void ListToRemoveLeftInRemaining_StillDetectsStrand()
    {
        // Defensive: even if a caller mistakenly leaves the removed list in the remaining set, its own
        // definitions must not mask the strand it would cause.
        var fields = new[] { Field("f1", "Sprint Points") };
        var defs = Defs(("A", [Def("f1")]), ("B", [Def("f2")]));

        var stranded = ListMembershipMigration.StrandedFieldsOnRemove(fields, "A", defs, ["A", "B"]);

        Assert.Equal(["Sprint Points"], stranded);
    }

    [Fact]
    public void EmptyFields_ReturnsEmpty()
    {
        var stranded = ListMembershipMigration.StrandedFieldsOnRemove(
            [], "A", Defs(("A", [Def("f1")])), ["B"]);

        Assert.Empty(stranded);
    }

    [Theory]
    [InlineData("\"hello\"", true)]
    [InlineData("\"\"", false)]
    [InlineData("\"   \"", false)]
    [InlineData("null", false)]
    [InlineData("0", true)]
    [InlineData("42", true)]
    [InlineData("false", true)]
    [InlineData("true", true)]
    [InlineData("[]", false)]
    [InlineData("[1]", true)]
    [InlineData("{}", false)]
    [InlineData("{\"a\":1}", true)]
    public void HasValue_PerJsonKind(string valueJson, bool expected)
    {
        var field = new CustomFieldItem("F", "text", Json(valueJson), Id: "f1");
        Assert.Equal(expected, ListMembershipMigration.HasValue(field));
    }

    [Fact]
    public void HasValue_NullValue_IsFalse()
    {
        Assert.False(ListMembershipMigration.HasValue(new CustomFieldItem("F", "text", Value: null, Id: "f1")));
    }
}
