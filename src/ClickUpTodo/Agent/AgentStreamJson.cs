using System.Text.Json;

namespace ClickUpTodo.Agent;

/// <summary>
/// Pure parser for the newline-delimited JSON that <c>claude -p --output-format stream-json</c> emits
/// (#187). Each stdout line is one event object; <see cref="ParseLine"/> turns a single line into the
/// zero-or-more human-readable display lines the run screen should append, so the streaming path can be
/// unit-tested without spawning a real <c>claude</c>. It never throws — a blank, malformed, or unknown
/// line yields no display lines.
/// <para>
/// Only the content a user cares about is surfaced: assistant <c>text</c> blocks, a compact activity
/// marker for each <c>tool_use</c> (so a long tool-running stretch still shows progress rather than a
/// frozen spinner), and an error detail from a failing terminal <c>result</c> event. The <c>system</c>
/// init event, <c>user</c> tool-result echoes (too noisy), and a successful <c>result</c> (it merely
/// duplicates the final assistant text) produce nothing.
/// </para>
/// </summary>
public static class AgentStreamJson
{
    /// <summary>The prefix marking a <c>tool_use</c> activity line (e.g. <c>⚙ Bash</c>).</summary>
    public const string ToolMarkerPrefix = "⚙ ";

    /// <summary>
    /// Parses a single stream-json line into the display lines to append (in order). Returns an empty
    /// list for a blank line, a non-JSON line, an unknown/ignored event type, or an event that carries
    /// nothing worth showing. Never throws.
    /// </summary>
    public static IReadOnlyList<string> ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return [];

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // A partial/garbled line (or a non-JSON diagnostic the CLI printed) — skip it silently.
            return [];
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return [];

            return TypeOf(root) switch
            {
                "assistant" => AssistantLines(root),
                "result" => ResultLines(root),
                _ => [],
            };
        }
    }

    /// <summary>Assistant message → each content block's display line (text verbatim, tool_use as a marker).</summary>
    private static IReadOnlyList<string> AssistantLines(JsonElement root)
    {
        if (!TryGetObject(root, "message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
            return [];

        var lines = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                continue;

            switch (TypeOf(block))
            {
                case "text":
                    if (block.TryGetProperty("text", out var text) &&
                        text.ValueKind == JsonValueKind.String &&
                        text.GetString() is { Length: > 0 } value)
                        lines.Add(value);
                    break;
                case "tool_use":
                    if (block.TryGetProperty("name", out var name) &&
                        name.ValueKind == JsonValueKind.String &&
                        name.GetString() is { Length: > 0 } toolName)
                        lines.Add(ToolMarkerPrefix + toolName);
                    break;
            }
        }
        return lines;
    }

    /// <summary>Terminal result event → the error detail when it failed; nothing on success (it just
    /// echoes the final assistant text already streamed).</summary>
    private static IReadOnlyList<string> ResultLines(JsonElement root)
    {
        if (!(root.TryGetProperty("is_error", out var isError) &&
              isError.ValueKind == JsonValueKind.True))
            return [];

        if (root.TryGetProperty("result", out var result) &&
            result.ValueKind == JsonValueKind.String &&
            result.GetString() is { Length: > 0 } text)
            return [text];

        return [];
    }

    private static string? TypeOf(JsonElement obj) =>
        obj.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    private static bool TryGetObject(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object)
            return true;
        value = default;
        return false;
    }
}
