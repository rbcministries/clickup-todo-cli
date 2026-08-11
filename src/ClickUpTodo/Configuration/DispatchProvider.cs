namespace ClickUpTodo.Configuration;

/// <summary>
/// What kind of agent a <see cref="DispatchProvider"/> is. Today there is a single kind — a local CLI
/// executable launched in a terminal — but the discriminator exists now so a future non-local kind
/// (e.g. a hosted agent API, per the Super-Agent epic #491) slots in without a config-schema change.
/// </summary>
public enum DispatchProviderKind
{
    /// <summary>A local command-line executable invoked in a terminal (today's only kind).</summary>
    LocalCli,
}

/// <summary>
/// One configured agent that a dispatch can target (#497). Generalizes the pre-#497 single hard-wired
/// <c>claude</c> executable into a named, selectable entry: a display name, the executable to invoke
/// (looked up on PATH), the extra args inserted before the prompt argument, and a
/// <see cref="DispatchProviderKind"/> discriminator. A list of these plus a chosen default lives on
/// <see cref="AgentDispatchSettings"/>; the selected provider is what
/// <see cref="AgentDispatchSettings.ToLauncherOptions"/> projects onto the launcher.
/// </summary>
public sealed class DispatchProvider
{
    /// <summary>The user-facing display name, also the selector key for the default provider.</summary>
    public string Name { get; set; } = "";

    /// <summary>The executable to invoke (looked up on PATH). Blank ⇒ <c>"claude"</c> at projection time.</summary>
    public string Executable { get; set; } = "claude";

    /// <summary>Extra arguments inserted before the prompt argument (e.g. a model flag).</summary>
    public List<string> ExtraArgs { get; set; } = [];

    /// <summary>Which kind of provider this is; <see cref="DispatchProviderKind.LocalCli"/> today.</summary>
    public DispatchProviderKind Kind { get; set; } = DispatchProviderKind.LocalCli;
}
