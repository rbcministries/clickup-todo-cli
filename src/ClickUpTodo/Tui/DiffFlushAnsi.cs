using System.Reflection;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace ClickUpTodo.Tui;

/// <summary>
/// An ANSI console output that only flushes rows whose content actually changed since the last
/// frame. Terminal.Gui 2.4's output buffer marks every cell a redraw <em>writes</em> as dirty —
/// even when the rune and attribute are identical to what is already on screen — and a ListView
/// selection move redraws its whole viewport. The net effect is that every ↑/↓ keypress re-sends
/// the entire visible list (~18 KB with colored badges) to the terminal, when only the two rows
/// whose highlight changed need bytes. On a slow terminal or remote link that volume is the
/// difference between instant and one-second navigation.
/// <para>
/// This subclass keeps a shadow copy of the last-flushed frame and, before delegating to the stock
/// <see cref="AnsiOutput"/> flush, un-dirties every row whose dirty cells all match the shadow.
/// The diff is deliberately <b>row-atomic</b>, not cell-level: a row with any change flushes all
/// of its dirty cells verbatim, byte-identical to the stock flush. Cell-level skipping created
/// sparse runs with mid-row cursor repositioning computed from the buffer's column model, and
/// around wide/ambiguous-width graphemes (emoji, VS16 sequences like 🛠️) that model can disagree
/// with the terminal's real cursor advance — glyphs then land shifted (doubled letters, stray
/// characters over borders) and half-overwritten wide glyphs persist, because the buffer believed
/// those cells were already correct. A contiguously written row self-aligns the way the stock
/// renderer does, so per-row granularity keeps the volume win without the positioning hazard.
/// </para>
/// <para>
/// Tradeoff: content corrupted <em>on the terminal side</em> (another process writing to the tty)
/// is no longer repainted on the next frame, because the app believes those rows are current.
/// That's the standard diffed-rendering tradeoff (vim/tmux behave the same); a resize repaints
/// everything (the shadow resets when dimensions change).
/// </para>
/// </summary>
internal sealed class DiffFlushAnsiOutput(AppModel appModel) : AnsiOutput(appModel)
{
    private ShadowCell[,] _shadow = new ShadowCell[0, 0];

    /// <summary>What the terminal is known to hold at a cell: the last flushed grapheme,
    /// attribute, and hyperlink. <see cref="Seen"/> distinguishes "never flushed" from defaults.</summary>
    internal struct ShadowCell
    {
        public bool Seen;
        public string? Grapheme;
        public Attribute? Attr;
        public string? Url;
    }

    public override void Write(IOutputBuffer buffer)
    {
        TrimUnchangedCells(buffer, ref _shadow);
        base.Write(buffer);
    }

    /// <summary>
    /// Clears the dirty flags of every row whose dirty cells are all identical to the shadow frame
    /// (so the stock flush skips the row entirely — its dirty-line flag is cleared too, saving the
    /// per-row cursor move). A row with <b>any</b> changed cell is left untouched, so it flushes
    /// byte-identically to the stock renderer — never as sparse mid-row runs, which mis-position
    /// around wide/ambiguous-width graphemes (see class docs). Static and buffer-in/buffer-out so
    /// the diff itself is unit-testable without a console.
    /// </summary>
    internal static void TrimUnchangedCells(IOutputBuffer buffer, ref ShadowCell[,] shadow)
    {
        var contents = buffer.Contents;
        var dirtyLines = buffer.DirtyLines;
        if (contents is null || dirtyLines is null)
            return;

        var rows = Math.Min(contents.GetLength(0), dirtyLines.Length);
        var cols = contents.GetLength(1);

        // Dimensions changed (startup or terminal resize): drop the shadow so everything flushes.
        if (shadow.GetLength(0) != rows || shadow.GetLength(1) != cols)
            shadow = new ShadowCell[rows, cols];

        // A frame with every single cell dirty is a full invalidation — the buffer was cleared
        // (startup, resize, full-window redraw). Flush it verbatim, only recording the shadow.
        // This matters for one real race: two resizes landing back on the same dimensions within
        // one frame would skip the dimension check above while the terminal may have reflowed its
        // actual contents out from under the shadow. Full-invalidation frames are rare one-offs
        // (never plain list navigation), so flushing them whole just matches stock behaviour.
        if (IsFullInvalidation(contents, dirtyLines, rows, cols))
        {
            for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                ref var known = ref shadow[r, c];
                known.Seen = true;
                known.Grapheme = contents[r, c].Grapheme;
                known.Attr = contents[r, c].Attribute;
                known.Url = buffer.GetCellUrl(c, r);
            }
            return;
        }

        for (var r = 0; r < rows; r++)
        {
            if (!dirtyLines[r])
                continue;

            // Pass 1: does any dirty cell in this row differ from what the terminal shows?
            var rowChanged = false;
            for (var c = 0; c < cols && !rowChanged; c++)
            {
                if (!contents[r, c].IsDirty)
                    continue;
                ref var known = ref shadow[r, c];
                var cell = contents[r, c];
                rowChanged = !(known.Seen
                    && known.Grapheme == cell.Grapheme
                    && known.Attr == cell.Attribute
                    && known.Url == buffer.GetCellUrl(c, r));
            }

            if (!rowChanged)
            {
                // Terminal already shows this row exactly — skip it whole.
                for (var c = 0; c < cols; c++)
                    contents[r, c].IsDirty = false;
                dirtyLines[r] = false; // saves the flush's per-dirty-row cursor reposition
                continue;
            }

            // Changed row: flush verbatim (all dirty flags untouched) and record the shadow.
            for (var c = 0; c < cols; c++)
            {
                if (!contents[r, c].IsDirty)
                    continue;
                ref var known = ref shadow[r, c];
                known.Seen = true;
                known.Grapheme = contents[r, c].Grapheme;
                known.Attr = contents[r, c].Attribute;
                known.Url = buffer.GetCellUrl(c, r);
            }
        }
    }

    /// <summary>True when every cell of every row is dirty — the signature of a cleared/rebuilt
    /// buffer. Normal redraws leave some cells untouched (e.g. the window border while the list
    /// repaints), so this exits on the first clean cell it meets.</summary>
    private static bool IsFullInvalidation(Cell[,] contents, bool[] dirtyLines, int rows, int cols)
    {
        for (var r = 0; r < rows; r++)
        {
            if (!dirtyLines[r])
                return false;
            for (var c = 0; c < cols; c++)
                if (!contents[r, c].IsDirty)
                    return false;
        }
        return true;
    }
}

/// <summary>The ANSI driver's component factory with the diff-flushing output swapped in.</summary>
internal sealed class DiffFlushAnsiComponentFactory : AnsiComponentFactory
{
    public override IOutput CreateOutput() => new DiffFlushAnsiOutput(AppModel);
}

/// <summary>
/// Installs <see cref="DiffFlushAnsiComponentFactory"/> as the backend the next
/// <c>Application.Init</c> uses. Terminal.Gui 2.4 offers no public seam for a custom component
/// factory — <c>ApplicationImpl</c> (which accepts one) is an internal class — so this reaches it
/// via reflection against the pinned Terminal.Gui version. Best-effort by design: if the internals
/// move in a future upgrade, <see cref="TryInstall"/> returns false and the caller falls back to
/// the stock driver, so the worst outcome is losing the optimization, never a broken UI.
/// </summary>
internal static class DiffFlushAnsiBackend
{
    internal static bool TryInstall()
    {
        try
        {
            var impl = typeof(Application).Assembly.GetType("Terminal.Gui.App.ApplicationImpl");
            var ctor = impl?.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, [typeof(IComponentFactory)]);
            // Public method, but on an internal class — still only reachable via reflection.
            var setInstance = impl?.GetMethod("SetInstance", BindingFlags.Static | BindingFlags.Public);
            if (ctor is null || setInstance is null)
                return false;
            setInstance.Invoke(null, [ctor.Invoke([new DiffFlushAnsiComponentFactory()])]);
            return true;
        }
        catch
        {
            return false; // fall back to the stock driver
        }
    }
}
