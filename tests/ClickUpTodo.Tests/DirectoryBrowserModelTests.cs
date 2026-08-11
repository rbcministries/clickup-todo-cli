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
    public void SelectionPathAt_SubdirectoryIsItsFullPath()
    {
        // Backs the glue's selection-follows-cursor sync (#95 follow-up): highlighting a subdirectory
        // selects its full path (an explicit pick), NOT its "up-navigation" resolution.
        Directory.CreateDirectory(Sub("child"));
        var model = new DirectoryBrowserModel(_root);

        Assert.Equal(Sub("child"), model.SelectionPathAt(1));
    }

    [Fact]
    public void SelectionPathAt_ParentAtRoot_IsBlank_ToPreserveDefaultDir()
    {
        // At the browser root, ".." resolves to the root itself — the configured base/default dir.
        // Returning it verbatim would read as an explicit pick and drop task-derived per-task output
        // (#98), so the sync must leave the field blank ("no explicit pick") for a stray graze.
        Directory.CreateDirectory(Sub("child"));
        var model = new DirectoryBrowserModel(_root);

        Assert.Equal(string.Empty, model.SelectionPathAt(0));
        // ...and it is deliberately NOT the parent that PathAt(0) resolves to.
        Assert.NotEqual(model.PathAt(0), model.SelectionPathAt(0));
    }

    [Fact]
    public void SelectionPathAt_AfterDescend_ParentRowIsTheDescendedDirectory()
    {
        // Descending re-homes the highlight onto ".." (index 0); below the root that IS an explicit
        // pick, so the sync reflects the dir just entered — a descend alone selects it.
        Directory.CreateDirectory(Sub("child"));
        var model = new DirectoryBrowserModel(_root);
        model.Descend(1); // into "child"

        Assert.Equal(DirectoryBrowserModel.Normalize(Sub("child")), model.SelectionPathAt(0));
    }

    [Fact]
    public void SelectionPathAt_ParentBackAtRootAfterUp_IsBlankAgain()
    {
        // Descending then returning to the root must restore the "blank ⇒ default dir" ".." behaviour,
        // so the root case is genuinely root-relative rather than a one-shot on construction.
        Directory.CreateDirectory(Sub("child"));
        var model = new DirectoryBrowserModel(_root);
        model.Descend(1);   // into "child" (".." now an explicit pick)
        model.NavigateUp(); // back to root

        Assert.Equal(string.Empty, model.SelectionPathAt(0));
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

    // ── SeedTo (#559): seed the browser to the Dispatch pane's pre-filled directory ──────────────

    [Fact]
    public void SeedTo_ExistingDirectChildOfRoot_HighlightsIt_StillAtRoot()
    {
        // The common {base}/{Repository} case (#461): the target is a direct child of the base root, so
        // the browser stays at root and highlights the target row — ↑/↓ start among its siblings.
        Directory.CreateDirectory(Sub("repo"));
        var model = new DirectoryBrowserModel(_root);

        var highlight = model.SeedTo(Sub("repo"));

        Assert.Equal("repo", highlight);
        Assert.Equal(DirectoryBrowserModel.Normalize(_root), model.CurrentDirectory);
        Assert.Contains("repo", model.Entries);
        // The highlighted row resolves back to the pre-filled path (an explicit pick), matching the field.
        var index = model.Entries.ToList().IndexOf("repo");
        Assert.Equal(DirectoryBrowserModel.Normalize(Sub("repo")), model.SelectionPathAt(index));
    }

    [Fact]
    public void SeedTo_ExistingNestedTarget_MovesToParent_HighlightsLeaf()
    {
        // A deeper target (e.g. a #96 cached dir below the base): the browser moves to the target's
        // parent and highlights the leaf, so ↑/↓ start among the target's siblings.
        Directory.CreateDirectory(Sub("a", "b"));
        var model = new DirectoryBrowserModel(_root);

        var highlight = model.SeedTo(Sub("a", "b"));

        Assert.Equal("b", highlight);
        Assert.Equal(DirectoryBrowserModel.Normalize(Sub("a")), model.CurrentDirectory);
    }

    [Fact]
    public void SeedTo_ExistingTargetOutsideBaseTree_MovesThere_ResetStillReturnsToBase()
    {
        // The #96 cached dir can point outside the base tree. Seeding follows it, but _root is unchanged,
        // so Reset() (the every-reopen root restore) still returns to the base working dir (#92).
        var other = Path.Combine(Path.GetTempPath(), "clickup-browser-other-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(other, "cached"));
        try
        {
            var model = new DirectoryBrowserModel(_root);

            var highlight = model.SeedTo(Path.Combine(other, "cached"));

            Assert.Equal("cached", highlight);
            Assert.Equal(DirectoryBrowserModel.Normalize(other), model.CurrentDirectory);

            model.Reset();
            Assert.Equal(DirectoryBrowserModel.Normalize(_root), model.CurrentDirectory);
        }
        finally
        {
            try { Directory.Delete(other, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void SeedTo_NonexistentTarget_EvenAfterNavigatingAway_ResetsToRoot_ReturnsNull()
    {
        // A {base}/{custom-id} target that doesn't exist yet degrades to the base root, and does so as a
        // genuine reset (proven by first navigating the browser away).
        Directory.CreateDirectory(Sub("child"));
        var model = new DirectoryBrowserModel(_root);
        model.Descend(1); // move away from root first

        var highlight = model.SeedTo(Sub("does-not-exist"));

        Assert.Null(highlight);
        Assert.Equal(DirectoryBrowserModel.Normalize(_root), model.CurrentDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SeedTo_BlankTarget_ResetsToRoot_ReturnsNull(string? target)
    {
        Directory.CreateDirectory(Sub("child"));
        var model = new DirectoryBrowserModel(_root);
        model.Descend(1);

        var highlight = model.SeedTo(target);

        Assert.Null(highlight);
        Assert.Equal(DirectoryBrowserModel.Normalize(_root), model.CurrentDirectory);
    }

    [Fact]
    public void SeedTo_TargetEqualToRoot_ResetsToRoot_ReturnsNull()
    {
        // A pre-fill equal to the base root is already what the root view represents (its ".." ⇒ the base
        // dir); seeding above it would be surprising, so keep the rooted-at-base behaviour.
        Directory.CreateDirectory(Sub("child"));
        var model = new DirectoryBrowserModel(_root);
        model.Descend(1);

        var highlight = model.SeedTo(_root);

        Assert.Null(highlight);
        Assert.Equal(DirectoryBrowserModel.Normalize(_root), model.CurrentDirectory);
    }
}
