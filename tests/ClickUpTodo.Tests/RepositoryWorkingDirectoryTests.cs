using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure repo-sub-directory matcher (#461): value extraction from the
/// <c>Repository</c> custom field, normalisation to a safe single segment (traversal rejection), and
/// the exact-then-case-insensitive direct-child match against an injected in-memory directory set. No
/// filesystem, no API, no Terminal.Gui — the filesystem is the two injected delegates.
/// </summary>
public sealed class RepositoryWorkingDirectoryTests
{
    private const string Base = "/work";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static TaskDetail TaskWith(params CustomFieldItem[] fields)
        => new() { Id = "9x", Name = "T", CustomId = "ABC-1", CustomFields = fields };

    private static CustomFieldItem Field(string name, string type, string valueJson, params CustomFieldOption[] options)
        => new(name, type, Json(valueJson), options);

    // ── Value extraction ─────────────────────────────────────────────────────

    [Fact]
    public void RepositoryValue_TextField_ReturnsString()
        => Assert.Equal("my-repo", RepositoryWorkingDirectory.RepositoryValue(
            TaskWith(Field("Repository", "text", "\"my-repo\""))));

    [Theory]
    [InlineData("short_text")]
    [InlineData("url")]
    [InlineData("email")]
    [InlineData("phone")]
    public void RepositoryValue_StringTypes_ReturnString(string type)
        => Assert.Equal("r", RepositoryWorkingDirectory.RepositoryValue(
            TaskWith(Field("Repository", type, "\"r\""))));

    [Fact]
    public void RepositoryValue_FieldName_MatchedCaseInsensitively()
        => Assert.Equal("r", RepositoryWorkingDirectory.RepositoryValue(
            TaskWith(Field("repository", "text", "\"r\""))));

    [Fact]
    public void RepositoryValue_NoRepositoryField_ReturnsNull()
        => Assert.Null(RepositoryWorkingDirectory.RepositoryValue(
            TaskWith(Field("Other", "text", "\"r\""))));

    [Fact]
    public void RepositoryValue_BlankOrWhitespace_ReturnsNull()
    {
        Assert.Null(RepositoryWorkingDirectory.RepositoryValue(TaskWith(Field("Repository", "text", "\"\""))));
        Assert.Null(RepositoryWorkingDirectory.RepositoryValue(TaskWith(Field("Repository", "text", "\"   \""))));
        Assert.Null(RepositoryWorkingDirectory.RepositoryValue(TaskWith(Field("Repository", "text", "null"))));
        Assert.Null(RepositoryWorkingDirectory.RepositoryValue(TaskWith(new CustomFieldItem("Repository", "text"))));
    }

    [Fact]
    public void RepositoryValue_DropDown_ReturnsSelectedOptionName()
    {
        var field = Field("Repository", "drop_down", "\"opt-b\"",
            new CustomFieldOption("opt-a", "Repo-A", 0),
            new CustomFieldOption("opt-b", "Repo-B", 1));
        Assert.Equal("Repo-B", RepositoryWorkingDirectory.RepositoryValue(TaskWith(field)));
    }

    [Fact]
    public void RepositoryValue_DropDown_ByOrderIndex_ReturnsSelectedOptionName()
    {
        var field = Field("Repository", "drop_down", "1",
            new CustomFieldOption("opt-a", "Repo-A", 0),
            new CustomFieldOption("opt-b", "Repo-B", 1));
        Assert.Equal("Repo-B", RepositoryWorkingDirectory.RepositoryValue(TaskWith(field)));
    }

    [Fact]
    public void RepositoryValue_LabelsWithExactlyOne_ReturnsThatLabelName()
    {
        var field = Field("Repository", "labels", "[\"opt-b\"]",
            new CustomFieldOption("opt-a", "Repo-A", 0),
            new CustomFieldOption("opt-b", "Repo-B", 1));
        Assert.Equal("Repo-B", RepositoryWorkingDirectory.RepositoryValue(TaskWith(field)));
    }

    [Fact]
    public void RepositoryValue_LabelsWithMultiple_ReturnsNull_Ambiguous()
    {
        var field = Field("Repository", "labels", "[\"opt-a\",\"opt-b\"]",
            new CustomFieldOption("opt-a", "Repo-A", 0),
            new CustomFieldOption("opt-b", "Repo-B", 1));
        Assert.Null(RepositoryWorkingDirectory.RepositoryValue(TaskWith(field)));
    }

    [Fact]
    public void RepositoryValue_LabelsEmpty_ReturnsNull()
        => Assert.Null(RepositoryWorkingDirectory.RepositoryValue(TaskWith(Field("Repository", "labels", "[]"))));

    [Fact]
    public void RepositoryValue_LabelsSingleNonStringElement_ReturnsNull()
        // A non-string label element yields no id; a null id must not match an option carrying a null Id.
        => Assert.Null(RepositoryWorkingDirectory.RepositoryValue(
            TaskWith(Field("Repository", "labels", "[{\"x\":1}]", new CustomFieldOption(null, "NullId", 0)))));

    [Fact]
    public void RepositoryValue_StructuredValueWithoutKnownType_ReturnsNull()
    {
        // A `users`/object-valued Repository field isn't a repo name → no match.
        Assert.Null(RepositoryWorkingDirectory.RepositoryValue(TaskWith(Field("Repository", "users", "[123]"))));
        Assert.Null(RepositoryWorkingDirectory.RepositoryValue(TaskWith(Field("Repository", "location", "{\"x\":1}"))));
    }

    [Fact]
    public void RepositoryValue_UnknownTypeWithBareString_AcceptedLeniently()
        => Assert.Equal("r", RepositoryWorkingDirectory.RepositoryValue(
            TaskWith(new CustomFieldItem("Repository", Type: null, Value: Json("\"r\"")))));

    // ── Normalisation ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("my-repo", "my-repo")]
    [InlineData("owner/repo", "repo")]
    [InlineData("https://github.com/owner/repo", "repo")]
    [InlineData("https://github.com/owner/repo.git", "repo")]
    [InlineData("https://github.com/owner/repo/", "repo")]
    [InlineData("git@github.com:owner/repo.git", "repo")]
    [InlineData("repo.git", "repo")]
    [InlineData("  spaced  ", "spaced")]
    public void NormalizeSegment_AcceptsRealWorldForms(string raw, string expected)
        => Assert.Equal(expected, RepositoryWorkingDirectory.NormalizeSegment(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("foo/..")]
    [InlineData("/etc")]              // rooted → last segment guard / rooted reject
    [InlineData("../..")]
    public void NormalizeSegment_RejectsTraversalAndEmpties(string raw)
    {
        // Whatever survives normalisation must be a plain segment; these must never yield `.`/`..`/rooted.
        var result = RepositoryWorkingDirectory.NormalizeSegment(raw);
        Assert.True(result is null or not ("." or ".."), $"unexpectedly produced '{result}'");
        Assert.False(result is not null && Path.IsPathRooted(result));
    }

    [Fact]
    public void NormalizeSegment_BareDotDot_IsRejected()
        => Assert.Null(RepositoryWorkingDirectory.NormalizeSegment(".."));

    // ── Resolution against an in-memory directory set ────────────────────────

    private static Func<string, bool> Exists(params string[] dirs)
    {
        var set = new HashSet<string>(dirs, StringComparer.Ordinal);
        return p => set.Contains(p);
    }

    private static Func<string, IReadOnlyList<string>> Children(params string[] names)
        => _ => names;

    private static RepositoryWorkingDirectory.Match? Resolve(
        TaskDetail detail, Func<string, bool> exists, Func<string, IReadOnlyList<string>> children)
        => RepositoryWorkingDirectory.Resolve(detail, Base, exists, children);

    [Fact]
    public void Resolve_ExactChildExists_ReturnsIt()
    {
        var match = Resolve(
            TaskWith(Field("Repository", "text", "\"my-repo\"")),
            Exists("/work/my-repo"),
            Children("my-repo"));
        Assert.NotNull(match);
        Assert.Equal(Path.Combine(Base, "my-repo"), match!.Value.Directory);
        Assert.Equal("my-repo", match.Value.Name);
    }

    [Fact]
    public void Resolve_CaseInsensitiveChild_MatchesAndReportsOnDiskCasing()
    {
        // Case-sensitive FS: exact `/work/my-repo` doesn't exist, but a child `My-Repo` does.
        var match = Resolve(
            TaskWith(Field("Repository", "text", "\"my-repo\"")),
            Exists("/work/My-Repo"),
            Children("My-Repo"));
        Assert.NotNull(match);
        Assert.Equal(Path.Combine(Base, "My-Repo"), match!.Value.Directory);
        Assert.Equal("My-Repo", match.Value.Name);
    }

    [Fact]
    public void Resolve_NoMatchingDirectory_ReturnsNull()
        => Assert.Null(Resolve(
            TaskWith(Field("Repository", "text", "\"my-repo\"")),
            Exists("/work/other"),
            Children("other")));

    [Fact]
    public void Resolve_ValueNamesAFileNotADirectory_ReturnsNull()
        // A file child is never in the directory-only probes → no match.
        => Assert.Null(Resolve(
            TaskWith(Field("Repository", "text", "\"README.md\"")),
            Exists(),
            Children("my-repo")));

    [Fact]
    public void Resolve_NoRepositoryField_ReturnsNull_WithoutProbing()
    {
        var probed = false;
        var match = RepositoryWorkingDirectory.Resolve(
            TaskWith(Field("Other", "text", "\"my-repo\"")),
            Base,
            _ => { probed = true; return true; },
            _ => { probed = true; return []; });
        Assert.Null(match);
        Assert.False(probed);
    }

    [Fact]
    public void Resolve_OwnerRepoValue_MatchesRepoChild()
    {
        var match = Resolve(
            TaskWith(Field("Repository", "url", "\"https://github.com/acme/my-repo.git\"")),
            Exists("/work/my-repo"),
            Children("my-repo"));
        Assert.NotNull(match);
        Assert.Equal("my-repo", match!.Value.Name);
    }

    [Fact]
    public void Resolve_TraversalValue_NeverEscapesBase()
    {
        // `../../etc` normalises to its last segment `etc`; with no `etc` child it simply misses — it
        // can never resolve to `/etc`.
        Assert.Null(Resolve(
            TaskWith(Field("Repository", "text", "\"../../etc\"")),
            Exists("/etc"),
            Children()));
    }
}
