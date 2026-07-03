using System.Reflection;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace ClickUpTodo.Tui;

/// <summary>
/// An ANSI console output that only flushes cells whose content actually changed since the last
/// frame. Terminal.Gui 2.4's output buffer marks every cell a redraw <em>writes</em> as dirty —
/// even when the rune and attribute are identical to what is already on screen — and a ListView
/// selection move redraws its whole viewport. The net effect is that every ↑/↓ keypress re-sends
/// the entire visible list (~18 KB with colored badges) to the terminal, when only the two rows
/// whose highlight changed need bytes. On a slow terminal or remote link that volume is the
/// difference between instant and one-second navigation.
/// <para>
/// This subclass keeps a shadow copy of the last-flushed frame and, before delegating to the stock
/// <see cref="AnsiOutput"/> flush, un-dirties every cell that matches the shadow. The stock flush
/// (which already skips clean cells) then emits only real changes. Skipping is safe mid-run: the
/// flush's attribute run-tracking is per-call, and a skipped cell leaves the terminal cell — and
/// the terminal's current SGR state relative to the emitted stream — untouched.
/// </para>
/// <para>
/// Tradeoff: content corrupted <em>on the terminal side</em> (another process writing to the tty)
/// is no longer repainted on the next frame, because the app believes those cells are current.
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
    /// Clears the dirty flag on every cell identical to the shadow frame (so the stock flush skips
    /// it) and records the cells that will be flushed. A row left with no dirty cells has its
    /// dirty-line flag cleared too, so the flush doesn't emit a cursor move for it. Static and
    /// buffer-in/buffer-out so the diff itself is unit-testable without a console.
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

            var rowChanged = false;
            for (var c = 0; c < cols; c++)
            {
                if (!contents[r, c].IsDirty)
                    continue;

                ref var known = ref shadow[r, c];
                var cell = contents[r, c];
                var url = buffer.GetCellUrl(c, r);
                if (known.Seen && known.Grapheme == cell.Grapheme && known.Attr == cell.Attribute && known.Url == url)
                {
                    contents[r, c].IsDirty = false; // terminal already shows exactly this
                }
                else
                {
                    known.Seen = true;
                    known.Grapheme = cell.Grapheme;
                    known.Attr = cell.Attribute;
                    known.Url = url;
                    rowChanged = true;
                }
            }

            if (!rowChanged)
                dirtyLines[r] = false; // saves the flush's per-dirty-row cursor reposition
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
