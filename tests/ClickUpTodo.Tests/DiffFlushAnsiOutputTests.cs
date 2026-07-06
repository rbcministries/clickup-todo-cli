using ClickUpTodo.Tui;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace ClickUpTodo.Tests;

/// <summary>
/// Tests for the frame diff that keeps a redraw from re-sending unchanged cells to the terminal
/// (the cause of ~18 KB flushes per arrow keypress: Terminal.Gui marks every written cell dirty,
/// changed or not, and a ListView selection move rewrites its whole viewport).
/// </summary>
public class DiffFlushAnsiOutputTests
{
    private static readonly Attribute Red = new(new Color(255, 0, 0), new Color(0, 0, 0));
    private static readonly Attribute Blue = new(new Color(0, 0, 255), new Color(0, 0, 0));

    /// <summary>
    /// A 1-row buffer whose cells carry <paramref name="graphemes"/> in <paramref name="attr"/>,
    /// all dirty, plus one trailing untouched (clean) cell. The clean cell models a real frame —
    /// a redraw never rewrites literally every screen cell (e.g. the window border while the list
    /// repaints) — keeping these frames out of the full-invalidation fast path, which is tested
    /// separately.
    /// </summary>
    private static OutputBufferImpl Buffer(string graphemes, Attribute attr)
    {
        var buffer = new OutputBufferImpl();
        buffer.SetSize(graphemes.Length + 1, 1);
        buffer.Contents![0, graphemes.Length].IsDirty = false;
        Fill(buffer, graphemes, attr);
        return buffer;
    }

    /// <summary>Re-marks every content cell the way a redraw does: content set, dirty regardless of change.</summary>
    private static void Fill(OutputBufferImpl buffer, string graphemes, Attribute attr)
    {
        for (var c = 0; c < graphemes.Length; c++)
            buffer.Contents![0, c] = new Cell(attr, IsDirty: true, graphemes[c].ToString());
        buffer.DirtyLines[0] = true;
    }

    private static int DirtyCount(OutputBufferImpl buffer)
    {
        var dirty = 0;
        for (var c = 0; c < buffer.Cols; c++)
            if (buffer.Contents![0, c].IsDirty)
                dirty++;
        return dirty;
    }

    [Fact]
    public void FirstFrame_FlushesEverything()
    {
        var buffer = Buffer("hello", Red);
        var shadow = new DiffFlushAnsiOutput.ShadowCell[0, 0];

        DiffFlushAnsiOutput.TrimUnchangedCells(buffer, ref shadow);

        Assert.Equal(5, DirtyCount(buffer));
        Assert.True(buffer.DirtyLines[0]);
    }

    [Fact]
    public void IdenticalRedraw_FlushesNothing_AndClearsTheDirtyLine()
    {
        var buffer = Buffer("hello", Red);
        var shadow = new DiffFlushAnsiOutput.ShadowCell[0, 0];
        DiffFlushAnsiOutput.TrimUnchangedCells(buffer, ref shadow);

        // Second frame: the redraw rewrites the same content and re-marks everything dirty.
        Fill(buffer, "hello", Red);
        DiffFlushAnsiOutput.TrimUnchangedCells(buffer, ref shadow);

        Assert.Equal(0, DirtyCount(buffer));
        Assert.False(buffer.DirtyLines[0]);
    }

    [Fact]
    public void ChangedGrapheme_FlushesTheWholeRow_NotJustThatCell()
    {
        // Row-atomic on purpose: flushing only the changed cell creates sparse runs with mid-row
        // cursor repositioning, which drifts around wide/ambiguous-width graphemes and corrupts
        // the screen (doubled letters, stray glyphs over borders). A changed row must flush
        // byte-identically to the stock renderer.
        var buffer = Buffer("hello", Red);
        var shadow = new DiffFlushAnsiOutput.ShadowCell[0, 0];
        DiffFlushAnsiOutput.TrimUnchangedCells(buffer, ref shadow);

        Fill(buffer, "hallo", Red);
        DiffFlushAnsiOutput.TrimUnchangedCells(buffer, ref shadow);

        Assert.Equal(5, DirtyCount(buffer)); // every dirty cell of the changed row, not just 'e'→'a'
        Assert.True(buffer.DirtyLines[0]);
    }

    [Fact]
    public void ChangedAttribute_SameText_StillFlushes()
    {
        // Exactly the selection-highlight case: a row's text is unchanged but its colors are.
        var buffer = Buffer("hello", Red);
        var shadow = new DiffFlushAnsiOutput.ShadowCell[0, 0];
        DiffFlushAnsiOutput.TrimUnchangedCells(buffer, ref shadow);

        Fill(buffer, "hello", Blue);
        DiffFlushAnsiOutput.TrimUnchangedCells(buffer, ref shadow);

        Assert.Equal(5, DirtyCount(buffer));
    }

    [Fact]
    public void Resize_DropsTheShadow_SoEverythingFlushesAgain()
    {
        var buffer = Buffer("hello", Red);
        var shadow = new DiffFlushAnsiOutput.ShadowCell[0, 0];
        DiffFlushAnsiOutput.TrimUnchangedCells(buffer, ref shadow);

        // Overlapping content, different dimensions (SetSize recreates the contents array).
        buffer.SetSize(8, 1);
        buffer.Contents![0, 6].IsDirty = false;
        buffer.Contents![0, 7].IsDirty = false;
        Fill(buffer, "hello!", Red);
        DiffFlushAnsiOutput.TrimUnchangedCells(buffer, ref shadow);

        Assert.Equal(6, DirtyCount(buffer));
    }

    [Fact]
    public void FullyInvalidatedFrame_FlushesEverything_EvenWhenIdentical()
    {
        // Every cell dirty = a cleared/rebuilt buffer (startup, resize, full-window redraw).
        // Those frames must flush verbatim even when identical to the shadow: after two quick
        // resizes landing back on the same dimensions, the terminal may have reflowed its real
        // contents out from under the shadow.
        var buffer = new OutputBufferImpl();
        buffer.SetSize(3, 1);
        Fill(buffer, "abc", Red);
        var shadow = new DiffFlushAnsiOutput.ShadowCell[0, 0];
        DiffFlushAnsiOutput.TrimUnchangedCells(buffer, ref shadow);
        Assert.Equal(3, DirtyCount(buffer));

        Fill(buffer, "abc", Red); // identical, but again fully dirty
        DiffFlushAnsiOutput.TrimUnchangedCells(buffer, ref shadow);
        Assert.Equal(3, DirtyCount(buffer));
    }

    [Fact]
    public void CleanCells_AreLeftAlone()
    {
        // A cell the redraw didn't touch (IsDirty already false) must not be flushed or recorded:
        // the flush only looks at dirty cells, and the shadow must only ever mirror flushed state.
        var buffer = Buffer("hi", Red);
        buffer.Contents![0, 1].IsDirty = false;
        var shadow = new DiffFlushAnsiOutput.ShadowCell[0, 0];

        DiffFlushAnsiOutput.TrimUnchangedCells(buffer, ref shadow);

        Assert.True(buffer.Contents![0, 0].IsDirty);
        Assert.False(buffer.Contents![0, 1].IsDirty);
        Assert.False(shadow[0, 1].Seen);
    }
}
