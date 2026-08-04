using System.Text.Json;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// Resolves a task-derived Dispatch working directory to a <c>{base}/{Repository}</c> checkout
/// sub-directory (#461): when a task carries a <c>Repository</c> custom field whose value names a
/// <b>direct child</b> of the base directory, a dispatch starts <i>inside the project</i> — which is
/// also what lets a launched Claude session pick up that project's <c>CLAUDE.md</c> / MCP config —
/// rather than in the shared parent.
/// <para>
/// Pure: the filesystem is injected as delegates (mirroring <see cref="Agent.TerminalCommandPlanner"/>'s
/// <c>Func exists</c>), so the match logic is unit-testable against in-memory directory sets. Every miss
/// (no field, no value, no matching directory, an unsafe value) returns <c>null</c> so the caller falls
/// back to the base dir unchanged — a strict no-op for every existing configuration. This never creates a
/// directory: a repo sub-dir is a checkout that either exists or doesn't.
/// </para>
/// </summary>
public static class RepositoryWorkingDirectory
{
    /// <summary>The custom-field name whose value names the repo sub-directory, matched
    /// case-insensitively. Hard-coded until a second convention appears (#461 decision).</summary>
    public const string FieldName = "Repository";

    /// <summary>A resolved repo sub-directory: its absolute <see cref="Directory"/> and the child
    /// <see cref="Name"/> that matched (the on-disk name, so a case-insensitive hit reports the real
    /// casing).</summary>
    public readonly record struct Match(string Directory, string Name);

    /// <summary>
    /// Resolves <paramref name="detail"/>'s <c>Repository</c> field to a direct-child sub-directory of
    /// <paramref name="baseDirectory"/>, or <c>null</c> on any miss. Prefers an exact-case child
    /// (<paramref name="directoryExists"/>), else a case-insensitive scan of the base dir's immediate
    /// children (<paramref name="childDirectoryNames"/>) so <c>my-repo</c> finds <c>My-Repo</c> on a
    /// case-sensitive filesystem. A value naming a <b>file</b> never matches (the probes are
    /// directory-only). Never recurses and never creates anything.
    /// </summary>
    public static Match? Resolve(
        TaskDetail detail,
        string baseDirectory,
        Func<string, bool> directoryExists,
        Func<string, IReadOnlyList<string>> childDirectoryNames)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(childDirectoryNames);
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return null;

        if (NormalizeSegment(RepositoryValue(detail)) is not { } candidate)
            return null;

        // Exact-case first. The candidate is a validated bare segment, so it can only ever name a direct
        // child; the containment check backstops that invariant against a normalisation slip.
        var exact = Path.Combine(baseDirectory, candidate);
        if (directoryExists(exact) && IsDirectChild(baseDirectory, exact))
            return new Match(exact, candidate);

        // Case-insensitive scan of the base dir's immediate children (Linux is case-sensitive, so
        // `my-repo` wouldn't hit `My-Repo` above). Names come from enumerating the base dir, so they are
        // inherently direct children.
        foreach (var name in childDirectoryNames(baseDirectory))
        {
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                return new Match(Path.Combine(baseDirectory, name), name);
        }

        return null;
    }

    /// <summary>
    /// Reads the raw <c>Repository</c> field value (field name matched case-insensitively) for the types
    /// a repo field plausibly uses, or <c>null</c> when the field is absent / empty / an unsupported
    /// shape. Raw (not display-formatted) — deliberately not
    /// <c>TaskDetailFormatter.CustomFieldValue</c>, which truncates and comma-joins for the terminal.
    /// <list type="bullet">
    /// <item><c>text</c>/<c>short_text</c>/<c>url</c>/<c>email</c>/<c>phone</c> — the string value.</item>
    /// <item><c>drop_down</c> — the selected option's name (resolved from the option id/orderindex).</item>
    /// <item><c>labels</c> — the single selected label's name, only when <b>exactly one</b> is selected
    /// (more than one is ambiguous ⇒ no match).</item>
    /// <item>An unknown/absent type carrying a bare string value is accepted leniently as text.</item>
    /// </list>
    /// </summary>
    public static string? RepositoryValue(TaskDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var field = detail.CustomFields
            .FirstOrDefault(f => string.Equals(f.Name, FieldName, StringComparison.OrdinalIgnoreCase));
        if (field is null || field.Value is not { } value || value.ValueKind == JsonValueKind.Null)
            return null;

        var raw = (field.Type?.ToLowerInvariant()) switch
        {
            "drop_down" => DropDownName(value, field.Options),
            "labels" => SingleLabelName(value, field.Options),
            "text" or "short_text" or "url" or "email" or "phone" => ScalarString(value),
            // Unknown/absent type: accept a bare string leniently, but never an array/object (a
            // multi-value or structured field isn't a repo name).
            _ => value.ValueKind == JsonValueKind.String ? value.GetString() : null,
        };

        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    /// <summary>
    /// Normalises a raw repository value to a single, safe directory-name segment, or <c>null</c> when it
    /// can't be one. Accepts a bare name, <c>owner/repo</c>, a full <c>https://host/owner/repo</c> URL,
    /// and a trailing <c>.git</c> — taking the last path segment and stripping <c>.git</c>. Rejects
    /// anything that could escape the base dir: <c>.</c>/<c>..</c>, rooted paths, and separators or
    /// invalid filename chars surviving in the segment. (Containment against a concrete base dir is a
    /// caller concern — see <see cref="Resolve"/>.)
    /// </summary>
    public static string? NormalizeSegment(string? rawValue)
    {
        var raw = rawValue?.Trim();
        if (string.IsNullOrEmpty(raw))
            return null;

        string candidate;
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.AbsolutePath.Length > 0)
        {
            // A URL (http(s)/ssh/…): the repo is the last non-empty segment of the path.
            candidate = LastSegment(Uri.UnescapeDataString(uri.AbsolutePath));
        }
        else
        {
            // A bare name or `owner/repo` (incl. scp-like `git@host:owner/repo`): last `/`-or-`\` segment.
            candidate = LastSegment(raw.Replace('\\', '/'));
        }

        // Strip a trailing `.git` (case-insensitive), e.g. `repo.git` → `repo`.
        if (candidate.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            candidate = candidate[..^4];

        candidate = candidate.Trim();
        if (candidate.Length == 0 || candidate is "." or "..")
            return null;
        if (Path.IsPathRooted(candidate))
            return null;
        if (candidate.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return null;
        if (candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        return candidate;
    }

    /// <summary>The last non-empty <c>/</c>-delimited segment of <paramref name="path"/> (empty when
    /// none).</summary>
    private static string LastSegment(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "" : segments[^1];
    }

    /// <summary>True when <paramref name="candidate"/> resolves to a direct child of
    /// <paramref name="baseDirectory"/> (belt-and-braces against a normalisation slip escaping the base).</summary>
    private static bool IsDirectChild(string baseDirectory, string candidate)
    {
        try
        {
            var fullBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
            var fullChild = Path.GetFullPath(candidate);
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(fullChild) ?? ""),
                fullBase,
                StringComparison.Ordinal);
        }
        catch (ArgumentException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    // A drop-down's value is the selected option's orderindex (number) or its id (string); resolve to the
    // option's name (mirrors TaskDetailFormatter.DropDownValue but returns the raw, untruncated name).
    private static string? DropDownName(JsonElement value, IReadOnlyList<CustomFieldOption> options)
    {
        CustomFieldOption? match = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var idx)
                => options.FirstOrDefault(o => o.OrderIndex is { } oi && oi == idx),
            JsonValueKind.String => options.FirstOrDefault(o => o.Id == value.GetString()),
            _ => null,
        };
        return match?.Name;
    }

    // A labels value is an array of selected option ids; a repo lives in exactly one, so more than one
    // (or zero) selected label is not a repo name.
    private static string? SingleLabelName(JsonElement value, IReadOnlyList<CustomFieldOption> options)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 1)
            return null;
        var id = value[0].ValueKind == JsonValueKind.String ? value[0].GetString() : ScalarString(value[0]);
        return options.FirstOrDefault(o => o.Id == id)?.Name;
    }

    /// <summary>A JSON scalar as a plain string (string as-is, number/bool via raw text), else null.</summary>
    private static string? ScalarString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };
}
