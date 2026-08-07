using System.Text.Json;

namespace ClickUpTodo.Agent;

/// <summary>
/// Pure matcher for the "Try to use WT profiles" dispatch feature (#462): given the raw contents of a
/// Windows Terminal <c>settings.json</c> and the directory a dispatch resolved for a task, it returns
/// the <b>first</b> configured profile whose <c>startingDirectory</c> matches that directory — the
/// value to hand to <c>wt … -p &lt;profile&gt;</c> so the launched session inherits that profile's
/// appearance / environment / tab title while still running Dispatch's own command.
/// <para>
/// I/O-free: the caller supplies the already-read JSON text and an environment-variable expander
/// (injected so the <c>%USERPROFILE%</c>-style <c>startingDirectory</c> values are testable off a real
/// machine). Every miss — no <c>settings.json</c>, no matching profile, a malformed file — returns
/// <c>null</c> so the dispatch falls back to today's launch unchanged. A broken <c>settings.json</c>
/// must never fail a dispatch, so a parse error is a silent <c>null</c>, not a throw.
/// </para>
/// </summary>
public static class WindowsTerminalProfileMatcher
{
    private static readonly JsonDocumentOptions JsoncOptions = new()
    {
        // Windows Terminal ships settings.json as JSONC — `//` comments and trailing commas — which
        // stock System.Text.Json rejects without these.
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The identifier (a profile <c>guid</c> when present, else its <c>name</c>) of the first profile in
    /// <paramref name="settingsJson"/> whose normalised <c>startingDirectory</c> equals
    /// <paramref name="targetDirectory"/>, or <c>null</c> on any miss. Matching is case-insensitive
    /// (a Windows-only feature) after expanding environment variables, unifying <c>/</c> and <c>\</c>,
    /// and trimming trailing separators. <c>profiles.defaults</c> is never a candidate (its inherited
    /// <c>startingDirectory</c> would match everything); hidden profiles and profiles with no
    /// <c>startingDirectory</c> (they inherit ⇒ never a deliberate match) are skipped.
    /// </summary>
    public static string? Match(string? settingsJson, string? targetDirectory, Func<string, string> expandEnv)
    {
        ArgumentNullException.ThrowIfNull(expandEnv);
        if (string.IsNullOrWhiteSpace(settingsJson) || string.IsNullOrWhiteSpace(targetDirectory))
            return null;

        var target = Normalize(targetDirectory, expandEnv);
        if (target.Length == 0)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(settingsJson, JsoncOptions);
            if (ProfileList(doc.RootElement) is not { } list)
                return null;

            foreach (var profile in list.EnumerateArray())
            {
                if (profile.ValueKind != JsonValueKind.Object)
                    continue;
                if (profile.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True)
                    continue;
                if (!profile.TryGetProperty("startingDirectory", out var sd)
                    || sd.ValueKind != JsonValueKind.String
                    || sd.GetString() is not { } sdValue
                    || string.IsNullOrWhiteSpace(sdValue))
                    continue;

                if (!string.Equals(Normalize(sdValue, expandEnv), target, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Prefer the guid (stable, unique) — names aren't unique and are user-editable — but a
                // name is a valid `wt -p` argument too. A profile with neither is unusable; skip it.
                if (Identifier(profile) is { } id)
                    return id;
            }
        }
        catch (JsonException)
        {
            // A malformed settings.json degrades to "no match", never a thrown dispatch.
            return null;
        }

        return null;
    }

    /// <summary>
    /// The array of profile objects: <c>profiles.list</c> in the current object shape, or a bare
    /// <c>profiles</c> array in the older shape. <c>null</c> when neither is present (or
    /// <c>profiles</c> is some other kind), so the caller returns no match.
    /// </summary>
    private static JsonElement? ProfileList(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("profiles", out var profiles))
            return null;

        return profiles.ValueKind switch
        {
            JsonValueKind.Array => profiles,
            JsonValueKind.Object when profiles.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array => list,
            _ => null,
        };
    }

    /// <summary>The <c>guid</c> string if present and non-blank, else the <c>name</c> string, else null.</summary>
    private static string? Identifier(JsonElement profile)
    {
        if (profile.TryGetProperty("guid", out var guid) && guid.ValueKind == JsonValueKind.String
            && guid.GetString() is { } g && !string.IsNullOrWhiteSpace(g))
            return g;
        if (profile.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            && name.GetString() is { } n && !string.IsNullOrWhiteSpace(n))
            return n;
        return null;
    }

    /// <summary>
    /// Canonicalises a directory for comparison: expand <c>%ENV%</c> variables, unify <c>/</c> to
    /// <c>\</c>, then trim trailing separators and surrounding whitespace. Case is folded by the
    /// caller's ordinal-ignore-case compare.
    /// </summary>
    private static string Normalize(string path, Func<string, string> expandEnv)
        => expandEnv(path.Trim()).Replace('/', '\\').TrimEnd('\\', ' ');
}
