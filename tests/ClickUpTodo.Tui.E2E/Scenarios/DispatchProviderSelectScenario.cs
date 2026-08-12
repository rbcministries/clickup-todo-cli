using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tui.E2E;

/// <summary>
/// #498: seed <b>two</b> configured dispatch providers so the Ctrl+A Dispatch pane shows its per-dispatch
/// provider selector (the row only appears with 2+ providers —
/// <c>DispatchPaneModel.ProviderRowVisible</c>). <c>E2E_DISPATCH_PROVIDER_SELECT=1</c> configures a
/// Claude default plus a Codex provider; absent ⇒ the default empty provider list, so every other check
/// sees the pre-#498 pane (no provider row) and the layout stays byte-identical. The selector's render +
/// gating are asserted by <c>dispatch_provider_select_check.py</c>; the pick→launch threading is
/// unit-tested (Plan / DispatchAsync / AgentDispatchSettings).
/// </summary>
internal sealed class DispatchProviderSelectScenario : IE2EScenario
{
    public string Name => "dispatch-provider-select";
    public bool IsActive => Environment.GetEnvironmentVariable("E2E_DISPATCH_PROVIDER_SELECT") == "1";

    public void Configure(AppConfig config)
    {
        config.AgentDispatch.Providers =
        [
            new DispatchProvider { Name = "Claude", Executable = "claude" },
            new DispatchProvider { Name = "Codex", Executable = "codex", ExtraArgs = ["--yolo"] },
        ];
        config.AgentDispatch.DefaultProviderName = "Claude";
    }
}
