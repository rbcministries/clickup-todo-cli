using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure directory-browser model backing the Dispatch pane's working-dir file-tree
/// (issue #95). Exercised against real scratch directories — the model is the only filesystem-touching
/// piece, so this locks the listing/navigation decisions the (CI-untestable) Terminal.Gui glue delegates.
/// </summary>
public sealed class DirectoryBrowserModelTests : IDisposable
{
    private readonly string _root;

    public DirectoryBrowserModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "clickup-browser-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort scratch cleanup */ }
    }

    private string Sub(params string[] parts) => Path.Combine([_root, .. parts]);

    [Fact]
    public void Entries_ListsSubdirectoriesSorted_ParentFirst_DirectoriesOnly()
    {
        Directory.CreateDirectory(Sub("gamma"));
        Directory.CreateDirectory(Sub("alpha"));
        Directory.CreateDirectory(Sub("Beta"));
        File.WriteAllText(Sub("note.txt"), "x"); // a file must not appear in the listing

        var model = new DirectoryBrowserModel(_root);

        Assert.Equal(new[] { "..", "alpha", "Beta", "gamma" }, model.Entries);
    }

    [Fact]
    public void Entries_EmptyDirectory_IsJustParent()
    {
        var model = new DirectoryBrowserModel(_root);
        Assert.Equal(new[] { ".." }, model.Entries);
    }

    [Fact]
    public void IsParent_OnlyTrueForFirstEntry()
    {
        Directory.CreateDirectory(Sub("child"));
        var model = new DirectoryBrowserModel(_root);

        Assert.True(model.IsParent(0));
        Assert.False(model.IsParent(1));
    }

    [Fact]
    public void PathAt_ResolvesSubdirectoryAndParent()
    {
        Directory.CreateDirectory(Sub("child"));
        var model = new DirectoryBrowserModel(_root);

        Assert.Equal(Sub("child"), model.PathAt(1));
        Assert.Equal(DirectoryBrowserModel.Parent(_root), model.PathAt(0));
    }

    [Fact]
    public void PathAt_OutOfRange_FallsBackToCurrentDirectory()
    {
        var model = new DirectoryBrowserModel(_root);
        Assert.Equal(model.CurrentDirectory, model.PathAt(99));
    }

    [Fact]
    public void SelectionPathAt_SubdirectoryIsItsFullPath_ParentIsCurrentDirectory()
    {
        // Backs the glue's selection-follows-cursor sync (#95 follow-up): highlighting a subdirectory
        // selects its full path, while highlighting ".." selects the directory being browsed itself —
        // NOT its parent (which is what PathAt(0) resolves to, for up-navigation).
        Directory.CreateDirectory(Sub("child"));
        var model = new DirectoryBrowserModel(_root);

        Assert.Equal(Sub("child"), model.SelectionPathAt(1));
        Assert.Equal(model.CurrentDirectory, model.SelectionPathAt(0));
        Assert.NotEqual(model.PathAt(0), model.SelectionPathAt(0));
    }

    [Fact]
    public void SelectionPathAt_AfterDescend_ParentRowIsTheDescendedDirectory()
    {
        // Descending re-homes the highlight onto ".." (index 0); the sync must then reflect the dir
        // just entered, so a descend alone selects that directory as the working dir.
        Directory.CreateDirectory(Sub("child"));
        var model = new DirectoryBrowserModel(_root);
        model.Descend(1); // into "child"

        Assert.Equal(DirectoryBrowserModel.Normalize(Sub("child")), model.SelectionPathAt(0));
    }

    [Fact]
    public void Descend_ThenNavigateUp_RoundTripsToRoot()
    {
        Directory.CreateDirectory(Sub("child", "grandchild"));
        var model = new DirectoryBrowserModel(_root);

        model.Descend(1); // into "child"
        Assert.Equal(DirectoryBrowserModel.Normalize(Sub("child")), model.CurrentDirectory);
        Assert.Equal(new[] { "..", "grandchild" }, model.Entries);

        model.NavigateUp(); // back to root
        Assert.Equal(model.CurrentDirectory, DirectoryBrowserModel.Normalize(_root));
    }

    [Fact]
    public void Descend_OnParentEntry_GoesUp()
    {
        Directory.CreateDirectory(Sub("child"));
        var model = new DirectoryBrowserModel(_root);
        model.Descend(1); // into "child"

        model.Descend(0); // ".." → up
        Assert.Equal(DirectoryBrowserModel.Normalize(_root), model.CurrentDirectory);
    }

    [Fact]
    public void Reset_ReturnsToRootAfterNavigating()
    {
        Directory.CreateDirectory(Sub("child"));
        var model = new DirectoryBrowserModel(_root);
        model.Descend(1);

        model.Reset();
        Assert.Equal(DirectoryBrowserModel.Normalize(_root), model.CurrentDirectory);
    }

    [Fact]
    public void MissingDirectory_ListsJustParent_DoesNotThrow()
    {
        var missing = Sub("does-not-exist");
        var model = new DirectoryBrowserModel(missing);

        Assert.Equal(new[] { ".." }, model.Entries);
        Assert.Equal(DirectoryBrowserModel.Normalize(missing), model.CurrentDirectory);
    }

    [Fact]
    public void NavigateUp_AtFilesystemRoot_IsNoOp()
    {
        var root = DirectoryBrowserModel.Normalize(Path.GetPathRoot(Path.GetTempPath())!);
        Assert.Equal(root, DirectoryBrowserModel.Parent(root));

        var model = new DirectoryBrowserModel(root);
        model.NavigateUp();
        Assert.Equal(root, model.CurrentDirectory);
    }

    [Fact]
    public void Parent_OfSubdirectory_IsItsContainer()
        => Assert.Equal(
            DirectoryBrowserModel.Normalize(_root),
            DirectoryBrowserModel.Parent(Sub("child")));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Normalize_BlankResolvesToCurrentDirectory(string? input)
        => Assert.Equal(
            DirectoryBrowserModel.Normalize(Directory.GetCurrentDirectory()),
            DirectoryBrowserModel.Normalize(input));

    [Fact]
    public void Normalize_TrimsTrailingSeparator_ButNotForARoot()
    {
        Assert.Equal(
            DirectoryBrowserModel.Normalize(_root),
            DirectoryBrowserModel.Normalize(_root + Path.DirectorySeparatorChar));

        // A filesystem root keeps its separator (trimming it would leave "" or a bare drive).
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Equal(DirectoryBrowserModel.Normalize(root), DirectoryBrowserModel.Parent(root));
    }

    [Fact]
    public void UpNavigation_LeafNameOfChild_AppearsInParentListing()
    {
        // Backs the glue's up-navigation highlight: the leaf name of the dir we're leaving is
        // recoverable from CurrentDirectory and appears among the parent's entries after going up.
        Directory.CreateDirectory(Sub("child"));
        var model = new DirectoryBrowserModel(_root);
        model.Descend(1);

        var leaf = Path.GetFileName(model.CurrentDirectory);
        Assert.Equal("child", leaf);

        model.NavigateUp();
        Assert.Contains(leaf, model.Entries);
    }
}
