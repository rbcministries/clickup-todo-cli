using System.Globalization;
using System.Text;
using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tui;

/// <summary>
/// Pure text formatting for the task detail view (issue #17). Builds the header line and the body of
/// each tab from the domain DTOs, with no Terminal.Gui dependency, so the layout logic is unit-tested
/// while the (untestable) Terminal.Gui glue in <see cref="TaskDetailView"/> stays thin.
/// </summary>
public static class TaskDetailFormatter
{
    /// <summary>
    /// Header shown above the tabs, as structured, per-run-coloured lines (#162): the title line
    /// (<c>{Name}  ({CustomId})</c>) with trailing coloured <c>○ {status}</c> / <c>⚑ {priority}</c>
    /// badges (sharing <see cref="StatusPriorityBadge"/> with the main-list text badges), then the Tags
    /// line (when present) and the Assignees line — both uncoloured. Absent status/priority contribute
    /// no badge (and no separator). The badge <em>values</em> carry their hex colour
    /// (<see cref="TaskDetail.StatusColor"/> / <see cref="TaskDetail.PriorityColor"/>) so the view badges
    /// them via <see cref="StatusBadgeListSource.HeaderAttr"/>, exactly like the Other tab (#66). The
    /// single source both <see cref="Header"/> (plain text) and <see cref="DetailAttributesView"/> draw
    /// from, so the two can't drift.
    /// </summary>
    public static IReadOnlyList<DetailLine> HeaderLines(TaskDetail task)
    {
        var title = new StringBuilder();
        title.Append(task.Name);
        if (!string.IsNullOrWhiteSpace(task.CustomId))
            title.Append("  (").Append(task.CustomId).Append(')');

        var runs = new List<DetailRun> { new(title.ToString()) };
        // A two-space separator (uncoloured) precedes each badge, then the coloured "{icon} {name}".
        if (!string.IsNullOrWhiteSpace(task.StatusName))
        {
            runs.Add(new DetailRun("  "));
            runs.Add(new DetailRun(StatusPriorityBadge.Status(task.StatusName!), task.StatusColor));
        }
        if (!string.IsNullOrWhiteSpace(task.Priority))
        {
            runs.Add(new DetailRun("  "));
            runs.Add(new DetailRun(StatusPriorityBadge.Priority(task.Priority!), task.PriorityColor));
        }

        var lines = new List<DetailLine> { new(runs) };
        if (task.Tags.Count > 0)
            lines.Add(new DetailLine([new DetailRun("Tags: " + string.Join(", ", task.Tags))]));
        lines.Add(new DetailLine([new DetailRun(
            "Assignees: " + (task.Assignees.Count > 0 ? string.Join(", ", task.Assignees) : "(none)"))]));
        return lines;
    }

    /// <summary>The header as plain text (its <see cref="HeaderLines"/> concatenated, one per line) —
    /// used where per-span colour isn't available. Assembled from <see cref="HeaderLines"/> so the two
    /// can't drift; the title line therefore includes the badge labels (<c>○ …</c> / <c>⚑ …</c>).</summary>
    public static string Header(TaskDetail task)
        => string.Join("\n", HeaderLines(task).Select(l => l.Text));

    /// <summary>The Description tab body.</summary>
    public static string Description(TaskDetail task)
        => string.IsNullOrWhiteSpace(task.Description) ? "(no description)" : task.Description!.Trim();

    /// <summary>
    /// Horizontal rule drawn between adjacent comment blocks (#105) so each comment reads as its own
    /// block instead of being divided only by an easy-to-miss blank line. A fixed-width run of the
    /// box-drawing light-horizontal glyph — long enough to read as a divider, short enough not to fold
    /// on a normal-width terminal (the Comments/Stream panes word-wrap, so an over-long rule would
    /// wrap). Exposed so the Stream tab (#106) reuses the same separator for a consistent look.
    /// </summary>
    public const string CommentSeparator = "────────────────────────────────────────";

    /// <summary>The Comments tab body: one <b>thread</b> per top-level comment (a parent
    /// <see cref="CommentBlock"/> followed by its nested, indented replies — see <see cref="Thread"/>),
    /// separated by <see cref="CommentSeparator"/> (only between threads — never leading/trailing).
    /// Top-level comments are ordered by <paramref name="sort"/> the same way the Stream tab orders its
    /// comments (#106) — <see cref="StreamSort.Ascending"/> oldest-first, <see cref="StreamSort.Descending"/>
    /// newest-first — so the single activity-order preference governs both tabs. Undated comments cluster at
    /// the oldest end (see <see cref="InActivityOrder"/>). A comment with no replies renders exactly as its
    /// <see cref="CommentBlock"/>, so the flat (thread-less) case is byte-for-byte unchanged (#329).</summary>
    public static string Comments(IReadOnlyList<CommentItem> comments, StreamSort sort = StreamSort.Ascending)
        => comments.Count == 0 ? "(no comments)" : JoinBlocks(OrderedThreads(comments, sort));

    /// <summary>
    /// The Stream tab body (#106): the Description and the comments as a single timeline of blocks,
    /// separated by the shared <see cref="CommentSeparator"/> so it reads consistently with the
    /// Comments tab. Ordering:
    /// <list type="bullet">
    /// <item><see cref="StreamSort.Ascending"/> (oldest-first): the Description block, then the
    /// comments by date ascending.</item>
    /// <item><see cref="StreamSort.Descending"/> (newest-first): the comments by date descending,
    /// then the Description block last.</item>
    /// </list>
    /// <para>
    /// Comments without a date are treated as the <em>oldest</em> (sort key <c>DateMs ?? long.MinValue</c>,
    /// with an ordinal <c>Id</c> tiebreak for determinism), so they cluster at the same end as the
    /// Description — matching the feed's "nulls last in newest-first order" convention (#112).
    /// Descending is the exact reverse of ascending. The Description is always present (it shows
    /// <c>(no description)</c> when empty), so unlike <see cref="Comments"/> there is no empty
    /// placeholder.
    /// </para>
    /// Terminal.Gui-free, so the ordering and layout are unit-tested; the screen glue just re-renders
    /// this on a sort toggle.
    /// </summary>
    public static string Stream(TaskDetail task, IReadOnlyList<CommentItem> comments, StreamSort sort)
    {
        var description = DescriptionBlock(task);
        var commentThreads = OrderedThreads(comments, sort);
        var blocks = sort == StreamSort.Ascending
            ? new[] { description }.Concat(commentThreads)
            : commentThreads.Append(description);
        return JoinBlocks(blocks);
    }

    /// <summary>The top-level comments in activity order (<see cref="InActivityOrder"/>), each expanded to
    /// its <see cref="Thread"/> (parent block + nested replies). The single thread projection both the
    /// Comments and Stream (#106) tabs share, so their comment sequences stay byte-for-byte identical.</summary>
    private static IEnumerable<string> OrderedThreads(IReadOnlyList<CommentItem> comments, StreamSort sort)
        => InActivityOrder(comments, sort).Select(c => Thread(c, sort));

    /// <summary>Comments in activity order — oldest-first for <see cref="StreamSort.Ascending"/>, newest-
    /// first for <see cref="StreamSort.Descending"/> — the single ordering both the Stream (#106) and
    /// Comments tabs share (and, within a thread, that a parent's replies follow — see <see cref="Thread"/>).
    /// Undated items sort as the <em>oldest</em> (key <c>DateMs ?? long.MinValue</c>), with an ordinal
    /// <c>Id</c> tiebreak for determinism; descending is the exact reverse of ascending.</summary>
    private static IEnumerable<CommentItem> InActivityOrder(IReadOnlyList<CommentItem> comments, StreamSort sort)
    {
        var ascending = comments
            .OrderBy(c => c.DateMs ?? long.MinValue)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
        return sort == StreamSort.Ascending ? ascending : Enumerable.Reverse(ascending);
    }

    /// <summary>One comment as a Stream/Comments block: an <c>author · date · [resolved]</c> header
    /// line above the (trimmed) body, with no trailing newline. The single source of the block shape
    /// both tabs render.</summary>
    private static string CommentBlock(CommentItem c)
    {
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(c.Author) ? "(unknown)" : c.Author);
        if (c.DateMs is { } ms)
            sb.Append("  ·  ").Append(FormatDate(ms));
        if (c.Resolved)
            sb.Append("  ·  [resolved]");
        sb.Append('\n');
        sb.Append(string.IsNullOrWhiteSpace(c.Text) ? "(empty comment)" : c.Text.Trim());
        return sb.ToString();
    }

    /// <summary>The reply gutter: a down-right arrow glyph then a space (two display cells), prefixed to a
    /// reply's header line so a nested comment reads as an answer to the one above it (#329). Exposed like
    /// <see cref="CommentSeparator"/> so the view and tests can reference the marker without hard-coding the
    /// glyph.</summary>
    public const string ReplyMarker = "↳ ";

    /// <summary>Indentation added per nesting level (below the first), so a reply-of-a-reply steps in again.
    /// Two spaces — the width of <see cref="ReplyMarker"/> — so a reply body aligns under its header text.</summary>
    private const string IndentUnit = "  ";

    /// <summary>
    /// One top-level comment rendered as a <b>thread</b> (#329): its <see cref="CommentBlock"/> followed by
    /// its replies, each indented under it (depth-first, recursively). A parent's replies are ordered by the
    /// same <paramref name="sort"/> as the top-level list (<see cref="InActivityOrder"/>) so the chosen
    /// ascending/descending order is preserved within a thread; the parent always renders first, as the thread
    /// root. Blocks within a thread are separated by a blank line — deliberately <em>not</em> the heavy
    /// <see cref="CommentSeparator"/> rule, which is reserved for the boundary <em>between</em> threads
    /// (see <see cref="JoinBlocks"/>) so a thread coheres visually. A comment with no replies yields exactly
    /// its <see cref="CommentBlock"/> (no indent, no separator), keeping the flat case unchanged.
    /// </summary>
    private static string Thread(CommentItem comment, StreamSort sort)
        => string.Join("\n\n", FlattenThread(comment, sort, depth: 0).Select(x => IndentBlock(x.Block, x.Depth)));

    /// <summary>The comment and its whole reply subtree as <c>(depth, block)</c> pairs in top-down render
    /// order: the comment first, then each reply (ordered by <paramref name="sort"/>) with its own subtree.
    /// ClickUp threads are one level deep today, but the recursion handles any depth uniformly.</summary>
    private static IEnumerable<(int Depth, string Block)> FlattenThread(CommentItem comment, StreamSort sort, int depth)
    {
        yield return (depth, CommentBlock(comment));
        foreach (var reply in InActivityOrder(comment.Replies, sort))
            foreach (var nested in FlattenThread(reply, sort, depth + 1))
                yield return nested;
    }

    /// <summary>Indents every line of a comment <paramref name="block"/> by its <paramref name="depth"/>: one
    /// <see cref="IndentUnit"/> of leading blank per level, then the header line carries <see cref="ReplyMarker"/>
    /// and the body lines an equal-width blank — so a depth-1 reply is indented one step in with the marker,
    /// its body lines up under its header text, and deeper replies step in further. <paramref name="depth"/> 0
    /// (a top-level comment, or the Stream Description block) is returned unchanged. Note: the panes word-wrap,
    /// so a wrapped continuation of an indented line falls back to column 0 — the leading indent + marker still
    /// make the thread structure clear.</summary>
    private static string IndentBlock(string block, int depth)
    {
        if (depth <= 0)
            return block;

        var pad = string.Concat(Enumerable.Repeat(IndentUnit, depth));
        var headerPrefix = pad + ReplyMarker;
        var bodyPrefix = pad + new string(' ', ReplyMarker.Length);
        var lines = block.Split('\n');
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                sb.Append('\n');
            sb.Append(i == 0 ? headerPrefix : bodyPrefix).Append(lines[i]);
        }
        return sb.ToString();
    }

    /// <summary>The Description as a Stream block: a <c>Description</c> header line (with the task's
    /// created date when present, in the comment header's <c>·</c> shape) above the description body.</summary>
    private static string DescriptionBlock(TaskDetail task)
    {
        var sb = new StringBuilder();
        sb.Append("Description");
        if (task.CreatedMs is { } ms)
            sb.Append("  ·  ").Append(FormatDate(ms));
        sb.Append('\n');
        sb.Append(Description(task));
        return sb.ToString();
    }

    /// <summary>Joins top-level blocks (each a Description block or a whole comment <see cref="Thread"/>)
    /// with the standard divider — a blank line, the <see cref="CommentSeparator"/> rule, then a blank line —
    /// so it sits clear of both the previous body and the next header, and is never leading or trailing. This
    /// heavy rule marks a <em>thread</em> boundary; replies within a thread are joined by a plain blank line
    /// instead (see <see cref="Thread"/>). Each block carries no trailing newline.</summary>
    private static string JoinBlocks(IEnumerable<string> blocks)
        => string.Join("\n\n" + CommentSeparator + "\n\n", blocks);

    /// <summary>One coloured span of a detail line: its text and, when it should be badged, a ClickUp
    /// hex colour (null = rendered in the view's normal attribute). Terminal.Gui-free (the colour is a
    /// raw hex string, mapped to an attribute by the view), so the layout stays unit-testable.</summary>
    public readonly record struct DetailRun(string Text, string? Color = null);

    /// <summary>One line of the Other tab's header attributes, as an ordered list of coloured runs.</summary>
    public sealed record DetailLine(IReadOnlyList<DetailRun> Runs)
    {
        /// <summary>The line's plain text — its runs concatenated (what the uncoloured layout shows).</summary>
        public string Text => string.Concat(Runs.Select(r => r.Text));
    }

    /// <summary>
    /// The Other tab's header attributes (List / Lists / Priority / Status / dates) as structured,
    /// coloured runs — the single source of truth for both the coloured detail view (#66) and the
    /// plain <see cref="OtherAttributes"/> string. The Priority and Status <em>values</em> carry their
    /// hex colour (<see cref="TaskDetail.PriorityColor"/> / <see cref="TaskDetail.StatusColor"/>) so the
    /// view can badge them; the labels and every other line are uncoloured. The em-dash placeholder for
    /// a missing value is never coloured.
    /// </summary>
    public static IReadOnlyList<DetailLine> HeaderAttributeLines(TaskDetail task)
    {
        var lines = new List<DetailLine> { Line("List:          " + Coalesce(task.ListName)) };
        // ClickUp's "Tasks in Multiple Lists": show the full membership only when the task belongs to
        // more than its home list; otherwise the single "List:" line above already covers it.
        var lists = ListMembership(task);
        if (lists.Count > 1)
            lines.Add(Line("Lists:         " + string.Join(", ", lists)));

        var priority = Coalesce(task.Priority);
        lines.Add(new DetailLine([new DetailRun("Priority:      "), new DetailRun(priority, ValueColor(priority, task.PriorityColor))]));
        var status = Coalesce(task.StatusName);
        lines.Add(new DetailLine([new DetailRun("Status:        "), new DetailRun(status, ValueColor(status, task.StatusColor))]));

        lines.Add(Line("Created:       " + FormatDateOrDash(task.CreatedMs)));
        lines.Add(Line("Last activity: " + FormatDateOrDash(task.UpdatedMs)));
        lines.Add(Line("Due:           " + FormatDateOrDash(task.DueDateMs)));
        return lines;

        static DetailLine Line(string text) => new([new DetailRun(text)]);
        // Only a real value gets its colour — the em-dash placeholder for an absent value stays uncoloured.
        static string? ValueColor(string value, string? color) => value == EmDash ? null : color;
    }

    /// <summary>The Other tab's "Custom fields:" section (below the header attributes).</summary>
    public static string CustomFieldsBody(TaskDetail task)
    {
        var sb = new StringBuilder();
        sb.Append("Custom fields:").Append('\n');
        if (task.CustomFields.Count == 0)
            sb.Append("  (none)");
        else
            foreach (var f in task.CustomFields)
            {
                sb.Append("  • ").Append(f.Name);
                if (!string.IsNullOrWhiteSpace(f.Type))
                    sb.Append("  (").Append(f.Type).Append(')');
                var value = CustomFieldValue(f);
                if (!string.IsNullOrWhiteSpace(value))
                    sb.Append(": ").Append(value);
                sb.Append('\n');
            }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The Other-attributes tab body as plain text (dates, list, priority, custom fields) —
    /// used where per-span colour isn't available. Assembled from the same
    /// <see cref="HeaderAttributeLines"/> + <see cref="CustomFieldsBody"/> the coloured view draws from,
    /// so the two can't drift.</summary>
    public static string OtherAttributes(TaskDetail task)
    {
        var sb = new StringBuilder();
        foreach (var line in HeaderAttributeLines(task))
            sb.Append(line.Text).Append('\n');
        sb.Append('\n');
        sb.Append(CustomFieldsBody(task));
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Longest custom-field value rendered on one line before it's truncated with an ellipsis.</summary>
    private const int MaxValueLength = 200;

    /// <summary>
    /// Renders a custom field's loosely-typed value for the terminal, dispatched by the field
    /// <see cref="CustomFieldItem.Type"/> then the JSON kind. Returns <c>null</c> when the field has
    /// no value (so the caller shows just its name/type). Never throws — any unexpected shape falls
    /// back to a compact stringified value. Pure (operates on the DTO only), so it is unit-tested.
    /// </summary>
    public static string? CustomFieldValue(CustomFieldItem field)
    {
        if (field.Value is not { } value || value.ValueKind == JsonValueKind.Null)
            return null;

        try
        {
            return (field.Type?.ToLowerInvariant()) switch
            {
                "drop_down" => DropDownValue(value, field.Options),
                "labels" => LabelsValue(value, field.Options),
                "users" => UsersValue(value),
                "date" => DateValue(value),
                "checkbox" => CheckboxValue(value),
                "manual_progress" or "automatic_progress" => ProgressValue(value),
                // Note: "emoji" (rating) is intentionally not here — its value shape isn't a bare
                // number, so it falls through to the compact fallback rather than mis-render.
                "number" or "currency" => NumberValue(value),
                // Note: "location" is an object ({formatted_address,…}), so it isn't here — it falls
                // through to the compact fallback rather than dumping raw object JSON as "text".
                "text" or "short_text" or "url" or "email" or "phone"
                    => Truncate(ScalarString(value)),
                _ => CompactFallback(value),
            };
        }
        catch
        {
            return CompactFallback(value);
        }
    }

    // A drop-down's value is the selected option's orderindex (number) or its id (string); resolve to
    // the option's display name via type_config.options, falling back to the raw selection.
    private static string DropDownValue(JsonElement value, IReadOnlyList<CustomFieldOption> options)
    {
        CustomFieldOption? match = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var idx)
                => options.FirstOrDefault(o => o.OrderIndex is { } oi && oi == idx),
            JsonValueKind.String => options.FirstOrDefault(o => o.Id == value.GetString()),
            _ => null,
        };
        return match?.Name ?? ScalarString(value);
    }

    // A labels/multi-select value is an array of option ids; map each to its option name.
    private static string LabelsValue(JsonElement value, IReadOnlyList<CustomFieldOption> options)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return CompactFallback(value);
        if (value.GetArrayLength() == 0)
            return ""; // no labels selected → omitted by the caller
        var names = value.EnumerateArray()
            .Select(id => id.ValueKind == JsonValueKind.String ? id.GetString() : ScalarString(id))
            .Select(id => options.FirstOrDefault(o => o.Id == id)?.Name ?? id ?? "")
            .Where(n => n.Length > 0);
        var joined = string.Join(", ", names);
        return Truncate(joined.Length > 0 ? joined : CompactFallback(value));
    }

    // A users value is an array of user objects; show username, then email, then id.
    private static string UsersValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return CompactFallback(value);
        if (value.GetArrayLength() == 0)
            return ""; // no users assigned → omitted by the caller
        var names = value.EnumerateArray()
            .Where(u => u.ValueKind == JsonValueKind.Object)
            .Select(u => Prop(u, "username") ?? Prop(u, "email") ?? Prop(u, "id") ?? "")
            .Where(n => n.Length > 0);
        var joined = string.Join(", ", names);
        return Truncate(joined.Length > 0 ? joined : CompactFallback(value));
    }

    private static string DateValue(JsonElement value)
    {
        long? ms = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var n) => (long)n,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
        return ms is { } v ? FormatDate(v) : ScalarString(value);
    }

    private static string CheckboxValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.String => string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase) ? "Yes"
                              : string.Equals(value.GetString(), "false", StringComparison.OrdinalIgnoreCase) ? "No"
                              : ScalarString(value),
        _ => ScalarString(value),
    };

    // Progress fields carry an object like { "percent_complete": 42, ... }.
    private static string ProgressValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("percent_complete", out var pc)
            && pc.ValueKind == JsonValueKind.Number
            && pc.TryGetDouble(out var percent))
            return FormatNumber(percent) + "%";
        return CompactFallback(value);
    }

    private static string NumberValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var n) => FormatNumber(n),
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) => FormatNumber(n),
        _ => ScalarString(value),
    };

    // The scalar as a human string: JSON strings without quotes, everything else via its raw text.
    private static string ScalarString(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();

    // Last-resort rendering for an unexpected shape: compact, single-line, truncated JSON.
    private static string CompactFallback(JsonElement value)
    {
        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
        return Truncate(string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
    }

    private static string FormatNumber(double n)
        => n.ToString("0.############", CultureInfo.InvariantCulture);

    private static string? Prop(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var p)
            ? (p.ValueKind == JsonValueKind.String ? p.GetString() : p.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : p.GetRawText())
            : null;

    private static string Truncate(string value)
        => value.Length <= MaxValueLength ? value : value[..MaxValueLength] + "…";

    /// <summary>
    /// The task's full list membership: the home list unioned with its <c>locations</c>
    /// (ClickUp multiple-lists), home-first and de-duplicated. An entry is dropped if its id was
    /// already seen, or (when it has no id — ClickUp only reliably returns a list's name) if its
    /// display name was already seen. Names are the universal fallback key, so identical labels
    /// collapse regardless of id: two lists that render the same name are indistinguishable in this
    /// line, so showing one avoids meaningless duplicates. Robust to whether ClickUp includes the
    /// home list in <c>locations</c>.
    /// </summary>
    private static IReadOnlyList<string> ListMembership(TaskDetail task)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();

        void Add(string? id, string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;
            if (!string.IsNullOrEmpty(id) && !seenIds.Add(id))
                return;
            if (!seenNames.Add(name!))
                return;
            names.Add(name!);
        }

        Add(task.ListId, task.ListName);
        foreach (var l in task.Lists)
            Add(l.Id, l.Name);
        return names;
    }

    /// <summary>Placeholder shown for a missing/blank attribute value (never coloured as a badge).</summary>
    private const string EmDash = "—";

    private static string Coalesce(string? value) => string.IsNullOrWhiteSpace(value) ? EmDash : value!;

    private static string FormatDateOrDash(long? ms) => ms is { } v ? FormatDate(v) : EmDash;

    private static string FormatDate(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("MMM d, yyyy HH:mm");
}
