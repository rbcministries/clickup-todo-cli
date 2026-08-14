namespace ClickUpTodo.Tui.E2E;

using Handler = FakeClickUp.RouteHandler;

/// <summary>
/// #587 §3: when <c>E2E_CUSTOM_FIELD_LOG</c> is set, record the Other-tab custom-field writes so a check can
/// assert the value edit reached the backend keyed to the right field. A <c>POST /task/{id}/field/{fieldId}</c>
/// (set) logs <c>{fieldId}\tSET\t{body}</c>; a <c>DELETE /task/{id}/field/{fieldId}</c> (clear) logs
/// <c>{fieldId}\tCLEAR</c> — one per line. If <c>E2E_CUSTOM_FIELD_FORBID</c> names a field id, that id's write
/// answers 403 instead (the permission-error shape), so a check can drive the optimistic-revert leg. Pairs
/// with <see cref="DetailCustomFieldsScenario"/>, which seeds the fields the writes edit; both are active
/// together for the §3 edit check.
/// </summary>
internal sealed class CustomFieldWriteLogScenario : IE2EScenario
{
    public string Name => "custom-field-write-log";
    public bool IsActive => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("E2E_CUSTOM_FIELD_LOG"));

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend)
    {
        _ = backend;
        return
        [
            new(HttpMethod.Post, "task/{id}/field/{fieldId}", async (req, path, _, ct) =>
            {
                var fieldId = FakeClickUp.LastSegment(path);
                var body = req.Content is { } content ? await content.ReadAsStringAsync(ct) : "";
                Log($"{fieldId}\tSET\t{body.Replace('\n', ' ').Replace('\r', ' ')}");
                return Forbid(fieldId) ?? FakeClickUp.Ok("{}");
            }, 1),
            new(HttpMethod.Delete, "task/{id}/field/{fieldId}", (req, path, _, _) =>
            {
                _ = req;
                var fieldId = FakeClickUp.LastSegment(path);
                Log($"{fieldId}\tCLEAR");
                return Task.FromResult(Forbid(fieldId) ?? FakeClickUp.Ok("{}"));
            }, 1),
        ];
    }

    private static void Log(string line)
    {
        if (Environment.GetEnvironmentVariable("E2E_CUSTOM_FIELD_LOG") is { Length: > 0 } log)
            try { File.AppendAllText(log, line + "\n"); }
            catch { /* best-effort capture */ }
    }

    private static HttpResponseMessage? Forbid(string fieldId)
        => Environment.GetEnvironmentVariable("E2E_CUSTOM_FIELD_FORBID") is { Length: > 0 } forbidden
            && string.Equals(forbidden, fieldId, StringComparison.Ordinal)
            ? FakeClickUp.Forbidden("""{"err":"You do not have permission to edit this field","ECODE":"OAUTH_027"}""")
            : null;
}
