namespace ClickUpTodo;

/// <summary>
/// The product's user-facing branding, in one place so the name can't drift across the UI.
/// <para>
/// The display name is <b>"ClickUp Simple CLI"</b> (issue #20): the old "To-Do" name clashed with
/// the "to do" task status, so the word showed up twice (e.g. a <c>─ TO-DO ─</c> section header next
/// to <c>[to do]</c> status badges). Only user-facing copy uses these values.
/// </para>
/// <para>
/// Code identifiers intentionally keep the old name to avoid a breaking install/config change: the
/// CLI command and assembly <c>clickup-todo</c>, the NuGet package <c>ClickUpTodo.Cli</c>, the root
/// namespace <c>ClickUpTodo</c>, the repo <c>clickup-todo-cli</c>, and the config dir
/// <c>clickup-todo</c>. Renaming those is a separate, maintainer-gated decision.
/// </para>
/// </summary>
public static class AppBranding
{
    /// <summary>The product's user-facing display name.</summary>
    public const string DisplayName = "ClickUp Simple CLI";

    /// <summary>
    /// Label for the non-pinned task section header. Kept neutral ("TASKS") so it doesn't read like
    /// the "to do" task status, which is what made the old "TO-DO" header confusing (#20).
    /// </summary>
    public const string TasksSectionLabel = "TASKS";

    /// <summary>The main window title, e.g. <c>"ClickUp Simple CLI — Acme Workspace"</c>.</summary>
    public static string WindowTitle(string workspaceName) => $"{DisplayName} — {workspaceName}";

    /// <summary>The first-run setup banner heading.</summary>
    public static string SetupHeading => $"{DisplayName} — first-time setup";
}
