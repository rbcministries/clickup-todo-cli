using System.Text.Json.Serialization;

namespace ClickUpTodo.Configuration;

/// <summary>
/// Non-secret, user-facing settings persisted to <c>config.json</c>. The API token is stored
/// separately and encrypted (see <see cref="TokenStore"/>).
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// One-shot config migration version (0 = pre-migrations). Bumped by <see cref="ConfigMigrations"/>
    /// on load so seeded defaults are applied exactly once and a user's later edits aren't re-seeded.
    /// </summary>
    public int SchemaVersion { get; set; }

    /// <summary>Selected ClickUp workspace (team) id.</summary>
    public string WorkspaceId { get; set; } = "";

    public string WorkspaceName { get; set; } = "";

    /// <summary>
    /// Which auth scheme the saved token uses (#52). Persisted as a string; absent ⇒
    /// <see cref="AuthMode.PersonalToken"/>, so existing personal-token users are unaffected and
    /// startup picks the right provider. OAuth is opt-in and set only by the OAuth sign-in flow.
    /// </summary>
    public AuthMode AuthMode { get; set; } = AuthMode.PersonalToken;

    /// <summary>The list the user treats as their "Personal Tasks" list.</summary>
    public string PersonalTasksListId { get; set; } = "";

    public string PersonalTasksListName { get; set; } = "";

    /// <summary>How often the task list polls ClickUp, in seconds.</summary>
    public int RefreshSeconds { get; set; } = 60;

    /// <summary>
    /// How often the mentions/comments feed (#109) polls ClickUp, in seconds — deliberately separate
    /// from (and, by default, longer than) <see cref="RefreshSeconds"/> because assembling the feed
    /// fans a comment fetch out across every assigned task, which is far heavier than a task-list poll
    /// (#123). An absent key loads this default, so existing configs need no migration.
    /// </summary>
    public int FeedRefreshSeconds { get; set; } = 300;

    /// <summary>
    /// Whether the mentions &amp; comments feed (Ctrl+E) includes activity from completed
    /// (closed-type) tasks — the feed's own <c>F12</c> toggle. Off by default:
    /// <see cref="Services.FeedService.LoadFeedAsync"/> gathers comments only from the user's
    /// <b>open</b> assigned tasks, so a comment on a ticket that has since closed doesn't surface. On:
    /// the feed's task fetch flips <c>IncludeClosed</c> so closed tasks (and their comments) are pulled
    /// in too. Deliberately separate from <see cref="ViewSettings.Completed"/> (the main list's
    /// F12) so the feed and list toggle independently, and kept on <see cref="AppConfig"/> like
    /// <see cref="RefreshSeconds"/>/<see cref="BadgeDisplay"/> so it's independent of the F3 view. A new
    /// bool defaults to false for existing configs, so no migration is needed.
    /// </summary>
    public bool FeedShowCompleted { get; set; }

    /// <summary>
    /// Whether the mentions &amp; comments feed (Ctrl+E) also shows a recent-activity source (#117) —
    /// recently-updated assigned tasks, newest-first, merged into the feed — the feed's own <c>F6</c>
    /// "show/hide activity" display state. Off by default: the feed shows comments only until the user
    /// turns it on. Unlike <see cref="FeedShowCompleted"/> (F12), this changes <b>only what is
    /// displayed</b>, not what is fetched — the activity is projected from the same assigned tasks the
    /// feed already loads for comments — so it is deliberately <b>excluded</b> from
    /// <see cref="Services.FeedCache.KeyFor"/> and toggling it is a client-side re-render, not a
    /// re-fetch. Named apart from the main list's F6 (<see cref="BadgeDisplay"/>), which it is unrelated
    /// to. A new bool defaults to false for existing configs, so no migration is needed.
    /// </summary>
    public bool FeedShowActivity { get; set; }

    /// <summary>
    /// Optional look-back window, in days, that narrows the feed's assigned-task fetch server-side via
    /// <c>date_updated_gt</c> (#244). <c>0</c> (the default) disables it — the feed fetches the full
    /// assigned set exactly as before, so existing configs need no migration. When &gt; 0, the feed's
    /// single <see cref="Services.FeedService.LoadFeedAsync"/> fetch is limited to tasks updated within
    /// the last N days, shrinking the payload on a busy workspace. Because that one fetch feeds
    /// <b>both</b> the recent-activity projection and the comment fan-out, the window also bounds the
    /// comments feed — but posting a comment bumps a task's <c>date_updated</c>, so tasks with recent
    /// comments stay in-window; the window only drops tasks with no activity at all in the period. The
    /// semantic is therefore "activity and comments from tasks touched in the last N days".
    /// </summary>
    public int FeedActivityLookbackDays { get; set; }

    /// <summary>
    /// How the task list renders each row's Status/Priority badges (F6 cycles Icons → Text → Hidden).
    /// A cosmetic display preference, deliberately kept out of <see cref="ViewSettings"/> so it's
    /// independent of the F3 filter/sort/group view (and its <see cref="ViewSettings.IsDefault"/>).
    /// </summary>
    public BadgeDisplay BadgeDisplay { get; set; } = BadgeDisplay.Icons;

    /// <summary>
    /// Legacy status-exclusion setting, retained only as a <b>deserialize-only migration shim</b>
    /// (#69). Status exclusion is now expressed as ordinary F3 <c>Status IS NOT</c> filter rules;
    /// <see cref="ConfigMigrations"/> reads any saved <c>excludedStatuses</c> array on load, converts
    /// each entry into a rule, then nulls this out so it's never written again (the
    /// <see cref="JsonIgnoreCondition.WhenWritingNull"/> ignore drops it from <c>config.json</c>).
    /// <para>
    /// Null means the key was <b>absent</b> (a fresh install, or an already-migrated config) — the
    /// migration then seeds the default exclusions. An empty list means the user deliberately cleared
    /// their exclusions — the migration seeds nothing. This absent-vs-empty distinction is why it's a
    /// nullable shim rather than a list with a non-null default.
    /// </para>
    /// </summary>
    [JsonPropertyName("excludedStatuses")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyExcludedStatuses { get; set; }

    /// <summary>Task ids pinned to the "Current Focus" pane, so focus survives restarts.</summary>
    public List<string> PinnedTaskIds { get; set; } = [];

    /// <summary>The active filter/sort/group view (F3), persisted so it survives restarts.</summary>
    public ViewSettings View { get; set; } = new();

    /// <summary>
    /// Task detail-view preferences (#108): default tab, Stream sort order, and auto-scroll position.
    /// Kept separate from <see cref="View"/> (like <see cref="BadgeDisplay"/>) so it's independent of
    /// the F3 view and its <see cref="ViewSettings.IsDefault"/>. An absent key loads all defaults.
    /// </summary>
    public DetailViewSettings DetailView { get; set; } = new();

    /// <summary>Configuration for dispatching an interactive <c>claude</c> session (#23); all optional.</summary>
    public AgentDispatchSettings AgentDispatch { get; set; } = new();

    /// <summary>
    /// The base working directory (#92) — the local root where most of the user's ClickUp-tracked
    /// work lives. It's the root the Dispatch file-tree browser (#95) hangs off and the parent a
    /// task-derived launch (#98) starts in. Blank/absent is the sentinel for the default
    /// <c>~/ClickUp-Tasks</c>, resolved at read time via
    /// <see cref="Tui.Screens.SettingsForm.ResolveDefaultWorkingDirectory"/> (so old configs stay
    /// backward-compatible with no migration). This is a <b>root</b>, distinct from
    /// <see cref="AgentDispatchSettings.FixedWorkingDirectory"/>, which is an explicit "always start
    /// in exactly this dir" override mode.
    /// </summary>
    public string DefaultWorkingDirectory { get; set; } = "";

    /// <summary>
    /// Per-task remembered Dispatch working directories (#96), keyed by task id
    /// (<see cref="ClickUp.TaskDetail.Id"/> — always present, unlike <c>custom_id</c>). When the user
    /// picks an explicit, non-default working directory in the Dispatch pane (#95), it's stored here
    /// so the next dispatch from that same task pre-fills the field — including across relaunches.
    /// Only explicit non-default picks are stored; reverting to the default (a blank field, or a pick
    /// equal to the resolved default dir) clears the entry (see
    /// <see cref="DispatchWorkingDirectoryCache"/>). Absent key ⇒ empty map, so old configs load
    /// unchanged. Saved via <see cref="ConfigStore"/> to <c>config.json</c> (camelCase
    /// <c>taskWorkingDirectories</c>), the same persistence path as <see cref="PinnedTaskIds"/> and
    /// <see cref="View"/>.
    /// </summary>
    public Dictionary<string, string> TaskWorkingDirectories { get; set; } = [];

    /// <summary>True once the setup wizard has completed at least once.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(WorkspaceId) && !string.IsNullOrWhiteSpace(PersonalTasksListId);
}
