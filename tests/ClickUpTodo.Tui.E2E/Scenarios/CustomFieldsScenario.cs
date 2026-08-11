namespace ClickUpTodo.Tui.E2E;

using Handler = FakeClickUp.RouteHandler;

/// <summary>
/// Fillable Custom Field definitions on the New Task screen (#249/#395/#446). Overrides
/// <c>GET /list/{id}/field</c> so the custom-field page renders and the required-block + drop-down paths are
/// assertable:
/// <list type="bullet">
/// <item><c>E2E_CUSTOM_FIELDS=1</c> — the small seeded set.</item>
/// <item><c>E2E_CUSTOM_FIELDS_MANY=1</c> — a <em>tall</em> set (nine text fields + a required tenth) so the
/// page's widget stack overflows a short terminal, exercising content-scroll. Takes precedence when both are
/// set.</item>
/// </list>
/// Off (neither set) leaves the default empty field set, so the New Task screen creates directly as every
/// other check expects. The create-time body capture is a separate concern — see
/// <see cref="CaptureFileScenario"/>.
/// </summary>
internal sealed class CustomFieldsScenario : IE2EScenario
{
    private static bool Many => Environment.GetEnvironmentVariable("E2E_CUSTOM_FIELDS_MANY") == "1";
    private static bool Small => Environment.GetEnvironmentVariable("E2E_CUSTOM_FIELDS") == "1";

    public string Name => "custom-fields";
    public bool IsActive => Many || Small;

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend) =>
    [
        new(HttpMethod.Get, "list/{id}/field", (_, _, _, _) =>
            FakeClickUp.OkAsync(FakeClickUp.Fixture(Many ? "custom_fields_many" : "custom_fields")), 1),
    ];
}
