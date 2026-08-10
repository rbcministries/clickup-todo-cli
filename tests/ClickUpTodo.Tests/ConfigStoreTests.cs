using ClickUpTodo.Agent;
using ClickUpTodo.Configuration;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_WhenNoFile_ReturnsUnconfiguredDefault()
    {
        var store = new ConfigStore(_dir);

        var config = store.Load();

        Assert.False(store.Exists());
        Assert.False(config.IsConfigured);
        Assert.Equal(60, config.RefreshSeconds);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var store = new ConfigStore(_dir);
        var original = new AppConfig
        {
            WorkspaceId = "123",
            WorkspaceName = "Acme",
            PersonalTasksListId = "456",
            PersonalTasksListName = "Personal Tasks",
            RefreshSeconds = 30,
            PinnedTaskIds = ["abc", "def"],
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.True(store.Exists());
        Assert.True(loaded.IsConfigured);
        Assert.Equal("123", loaded.WorkspaceId);
        Assert.Equal("Personal Tasks", loaded.PersonalTasksListName);
        Assert.Equal(30, loaded.RefreshSeconds);
        Assert.Equal(["abc", "def"], loaded.PinnedTaskIds);
    }

    [Fact]
    public void Load_WhenFileMissingAgentBlock_UsesDispatchDefaults()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig { WorkspaceId = "1", PersonalTasksListId = "2" });
        // Rewrite the file without an agentDispatch key (simulates a pre-#27 config.json).
        File.WriteAllText(store.ConfigPath, "{\"workspaceId\":\"1\",\"personalTasksListId\":\"2\"}");

        var loaded = store.Load();

        Assert.NotNull(loaded.AgentDispatch);
        Assert.True(loaded.AgentDispatch.IsDefault);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAgentDispatchBlock()
    {
        var store = new ConfigStore(_dir);
        var original = new AppConfig
        {
            WorkspaceId = "1",
            PersonalTasksListId = "2",
            AgentDispatch = new AgentDispatchSettings
            {
                PreferredTerminal = PreferredTerminal.Pwsh,
                CustomTerminalCommand = "ghostty -e {}",
                LaunchLocation = LaunchLocation.NewTab,
                Providers = [new DispatchProvider { Name = "Claude", Executable = "/opt/claude", ExtraArgs = ["--model", "opus"] }],
                DefaultProviderName = "Claude",
                WorkingDirectory = AgentWorkingDirectory.Fixed,
                FixedWorkingDirectory = "/work",
                DefaultSessionMode = AgentSessionMode.OneOff,
                DefaultPostResultsToComments = true,
                PromptTemplate = "Lead: {userPrompt}\n{contextJson}",
            },
        };

        store.Save(original);
        var loaded = store.Load();

        var d = loaded.AgentDispatch;
        Assert.Equal(PreferredTerminal.Pwsh, d.PreferredTerminal);
        Assert.Equal("ghostty -e {}", d.CustomTerminalCommand);
        Assert.Equal(LaunchLocation.NewTab, d.LaunchLocation);
        Assert.Equal("Claude", d.DefaultProviderName);
        var provider = d.ResolveDefaultProvider();
        Assert.Equal("/opt/claude", provider.Executable);
        Assert.Equal(["--model", "opus"], provider.ExtraArgs);
        Assert.Equal(AgentWorkingDirectory.Fixed, d.WorkingDirectory);
        Assert.Equal("/work", d.FixedWorkingDirectory);
        Assert.Equal(AgentSessionMode.OneOff, d.DefaultSessionMode);
        Assert.True(d.DefaultPostResultsToComments);
        Assert.Equal("Lead: {userPrompt}\n{contextJson}", d.PromptTemplate);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsMultipleDispatchProviders_AndSelectsTheDefault()
    {
        // Until the editor UI (#547), a hand-edited config.json is the only way to get 2+ providers, so
        // pin the real path: two providers (incl. the kind discriminator) survive save→load→re-save→reload
        // through ConfigStore/StateJson, and defaultProviderName resolves to the second one.
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig
        {
            WorkspaceId = "1",
            PersonalTasksListId = "2",
            AgentDispatch = new AgentDispatchSettings
            {
                Providers =
                [
                    new DispatchProvider { Name = "Claude", Executable = "claude", ExtraArgs = ["--model", "opus"], Kind = DispatchProviderKind.LocalCli },
                    new DispatchProvider { Name = "Codex", Executable = "/opt/codex", ExtraArgs = ["--yolo"], Kind = DispatchProviderKind.LocalCli },
                ],
                DefaultProviderName = "Codex",
            },
        });

        var loaded = store.Load();
        Assert.Equal(ConfigMigrations.CurrentVersion, loaded.SchemaVersion); // v6 no-op: providers already present
        Assert.Equal(2, loaded.AgentDispatch.Providers.Count);
        Assert.Equal(["Claude", "Codex"], loaded.AgentDispatch.Providers.Select(p => p.Name)); // order preserved
        Assert.Equal(DispatchProviderKind.LocalCli, loaded.AgentDispatch.Providers[1].Kind);
        var resolved = loaded.AgentDispatch.ResolveDefaultProvider();
        Assert.Equal("Codex", resolved.Name);
        Assert.Equal("/opt/codex", loaded.AgentDispatch.ToLauncherOptions().ClaudeExecutable);
        Assert.Equal(["--yolo"], loaded.AgentDispatch.ToLauncherOptions().ExtraArgs);

        // Re-save the already-migrated config and reload: still stable (no second migration, nothing lost).
        store.Save(loaded);
        var reloaded = store.Load();
        Assert.Equal(2, reloaded.AgentDispatch.Providers.Count);
        Assert.Equal("Codex", reloaded.AgentDispatch.ResolveDefaultProvider().Name);
        Assert.Equal(["--model", "opus"], reloaded.AgentDispatch.Providers[0].ExtraArgs);
    }

    [Fact]
    public void Save_PersistsDispatchDefaultSessionModeAsReadableString()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig
        {
            AgentDispatch = new AgentDispatchSettings { DefaultSessionMode = AgentSessionMode.OneOff },
        });

        var json = File.ReadAllText(store.ConfigPath);
        Assert.Contains("OneOff", json);
        Assert.DoesNotContain("\"defaultSessionMode\":1", json);
    }

    [Fact]
    public void Load_WhenFileMissingDispatchDefaultKeys_DefaultsToInteractiveAndPostOff()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig { WorkspaceId = "1", PersonalTasksListId = "2" });
        // Rewrite with an agentDispatch block from before #101 — no defaultSessionMode /
        // defaultPostResultsToComments keys (simulates a pre-#101 config.json).
        File.WriteAllText(
            store.ConfigPath,
            "{\"workspaceId\":\"1\",\"personalTasksListId\":\"2\",\"agentDispatch\":{\"claudeExecutable\":\"claude\"}}");

        var loaded = store.Load();

        Assert.Equal(AgentSessionMode.Interactive, loaded.AgentDispatch.DefaultSessionMode);
        Assert.False(loaded.AgentDispatch.DefaultPostResultsToComments);
        Assert.True(loaded.AgentDispatch.IsDefault);
    }

    [Fact]
    public void Save_PersistsDispatchLaunchLocationAsReadableString()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig
        {
            AgentDispatch = new AgentDispatchSettings { LaunchLocation = LaunchLocation.NewTab },
        });

        var json = File.ReadAllText(store.ConfigPath);
        Assert.Contains("NewTab", json);
        Assert.DoesNotContain("\"launchLocation\":1", json);
    }

    [Fact]
    public void Load_WhenFileMissingLaunchLocation_DefaultsToNewWindow()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig { WorkspaceId = "1", PersonalTasksListId = "2" });
        // Rewrite with an agentDispatch block from before #255 — no launchLocation key
        // (simulates a pre-#255 config.json). The "no behavior change on upgrade" guarantee
        // rests on this deserializing to NewWindow.
        File.WriteAllText(
            store.ConfigPath,
            "{\"workspaceId\":\"1\",\"personalTasksListId\":\"2\",\"agentDispatch\":{\"claudeExecutable\":\"claude\"}}");

        var loaded = store.Load();

        Assert.Equal(LaunchLocation.NewWindow, loaded.AgentDispatch.LaunchLocation);
        Assert.True(loaded.AgentDispatch.IsDefault);
    }

    [Fact]
    public void Save_PersistsAgentEnumsAsReadableStrings()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig
        {
            AgentDispatch = new AgentDispatchSettings { PreferredTerminal = PreferredTerminal.WindowsTerminal },
        });

        var json = File.ReadAllText(store.ConfigPath);
        Assert.Contains("WindowsTerminal", json);
        Assert.DoesNotContain("\"preferredTerminal\":1", json);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsDefaultWorkingDirectory()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig
        {
            WorkspaceId = "1",
            PersonalTasksListId = "2",
            DefaultWorkingDirectory = "/home/dev/source/repos",
        });

        var loaded = store.Load();

        Assert.Equal("/home/dev/source/repos", loaded.DefaultWorkingDirectory);
    }

    [Fact]
    public void Save_PersistsDefaultWorkingDirectoryAsCamelCase()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig { DefaultWorkingDirectory = "/work" });

        var json = File.ReadAllText(store.ConfigPath);
        Assert.Contains("\"defaultWorkingDirectory\": \"/work\"", json);
    }

    [Fact]
    public void Load_WhenFileMissingWorkingDirKey_DefaultsToBlankAndResolvesToClickUpTasks()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig { WorkspaceId = "1", PersonalTasksListId = "2" });
        // Rewrite without a defaultWorkingDirectory key (simulates a pre-#92 config.json).
        File.WriteAllText(store.ConfigPath, "{\"workspaceId\":\"1\",\"personalTasksListId\":\"2\"}");

        var loaded = store.Load();

        // Absent key ⇒ blank sentinel, which resolves to the ~/ClickUp-Tasks default at read time.
        Assert.Equal("", loaded.DefaultWorkingDirectory);
        Assert.Equal(
            Path.Combine("/home/tester", SettingsForm.DefaultWorkingDirectoryFolderName),
            SettingsForm.ResolveDefaultWorkingDirectory(loaded.DefaultWorkingDirectory, "/home/tester"));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTaskWorkingDirectories()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig
        {
            WorkspaceId = "1",
            PersonalTasksListId = "2",
            TaskWorkingDirectories = { ["task-a"] = "/work/a", ["task-b"] = "/work/b" },
        });

        var loaded = store.Load();

        Assert.Equal("/work/a", loaded.TaskWorkingDirectories["task-a"]);
        Assert.Equal("/work/b", loaded.TaskWorkingDirectories["task-b"]);
    }

    [Fact]
    public void Save_PersistsTaskWorkingDirectoriesAsCamelCase()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig { TaskWorkingDirectories = { ["task-a"] = "/work/a" } });

        var json = File.ReadAllText(store.ConfigPath);
        Assert.Contains("\"taskWorkingDirectories\"", json);
        Assert.Contains("\"task-a\": \"/work/a\"", json);
    }

    [Fact]
    public void Load_WhenFileMissingTaskWorkingDirectoriesKey_DefaultsToEmptyMap()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig { WorkspaceId = "1", PersonalTasksListId = "2" });
        // Rewrite without a taskWorkingDirectories key (simulates a pre-#96 config.json).
        File.WriteAllText(store.ConfigPath, "{\"workspaceId\":\"1\",\"personalTasksListId\":\"2\"}");

        var loaded = store.Load();

        Assert.NotNull(loaded.TaskWorkingDirectories);
        Assert.Empty(loaded.TaskWorkingDirectories);
    }

    [Fact]
    public void IsConfigured_RequiresWorkspaceAndList()
    {
        Assert.False(new AppConfig { WorkspaceId = "1" }.IsConfigured);
        Assert.False(new AppConfig { PersonalTasksListId = "2" }.IsConfigured);
        Assert.True(new AppConfig { WorkspaceId = "1", PersonalTasksListId = "2" }.IsConfigured);
    }

    [Fact]
    public void Save_ThreeWayMerges_DoesNotClobberAConcurrentTabsFields()
    {
        // The concrete multi-tab clobber (#293): two tabs load the same config, each changes a
        // different field, then both save. The second save used to overwrite the first tab's field with
        // its stale startup value — the three-way merge now preserves both.
        new ConfigStore(new JsonFileStateStore(_dir)).Save(new AppConfig
        {
            WorkspaceId = "1",
            PersonalTasksListId = "2",
            RefreshSeconds = 60,
            BadgeDisplay = BadgeDisplay.Icons,
        });

        var tabA = new ConfigStore(new JsonFileStateStore(_dir));
        var tabB = new ConfigStore(new JsonFileStateStore(_dir));
        var a = tabA.Load();
        var b = tabB.Load();

        b.RefreshSeconds = 30;          // tab B changes the refresh interval
        b.PinnedTaskIds.Add("pin-b");   // ...and pins a task
        tabB.Save(b);

        a.BadgeDisplay = BadgeDisplay.Text; // tab A changes only the badge display
        tabA.Save(a);                       // must not revert refresh to 60 or drop pin-b

        var final = new ConfigStore(new JsonFileStateStore(_dir)).Load();
        Assert.Equal(30, final.RefreshSeconds);            // tab B's change preserved
        Assert.Equal(["pin-b"], final.PinnedTaskIds);      // tab B's pin preserved
        Assert.Equal(BadgeDisplay.Text, final.BadgeDisplay); // tab A's change applied
    }

    [Fact]
    public void Save_TwoTabsPinDifferentTasks_BothPinsSurvive()
    {
        // The same-field concurrent case #335 fixes, end-to-end through Save: two tabs pin *different*
        // tasks in one load->save window. Whole-field LWW would drop whichever saved first; the
        // element-level set union keeps both.
        new ConfigStore(new JsonFileStateStore(_dir)).Save(new AppConfig
        {
            WorkspaceId = "1",
            PersonalTasksListId = "2",
        });

        var tabA = new ConfigStore(new JsonFileStateStore(_dir));
        var tabB = new ConfigStore(new JsonFileStateStore(_dir));
        var a = tabA.Load();
        var b = tabB.Load();

        b.PinnedTaskIds.Add("pin-b");
        tabB.Save(b);

        a.PinnedTaskIds.Add("pin-a");
        tabA.Save(a); // must not clobber pin-b with its own [pin-a]

        var final = new ConfigStore(new JsonFileStateStore(_dir)).Load();
        Assert.Equal(["pin-a", "pin-b"], final.PinnedTaskIds.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Save_ConcurrentUnpin_Sticks_NotResurrectedByTheUnion()
    {
        // A genuine unpin must survive the set merge: tab A unpins a task while tab B leaves it alone.
        // The three-way (vs. baseline) rule honors the removal instead of resurrecting it via union.
        new ConfigStore(new JsonFileStateStore(_dir)).Save(new AppConfig
        {
            WorkspaceId = "1",
            PersonalTasksListId = "2",
            PinnedTaskIds = ["keep", "drop"],
        });

        var tabA = new ConfigStore(new JsonFileStateStore(_dir));
        var tabB = new ConfigStore(new JsonFileStateStore(_dir));
        var a = tabA.Load();
        var b = tabB.Load();

        b.RefreshSeconds = 30;            // tab B changes an unrelated field, doesn't touch pins
        tabB.Save(b);

        a.PinnedTaskIds.Remove("drop");  // tab A unpins "drop"
        tabA.Save(a);

        var final = new ConfigStore(new JsonFileStateStore(_dir)).Load();
        Assert.Equal(["keep"], final.PinnedTaskIds); // "drop" stays gone
        Assert.Equal(30, final.RefreshSeconds);      // tab B's unrelated change preserved
    }

    [Fact]
    public void Save_SwallowsWriteFailure_DoesNotThrow()
    {
        // A failed settings write (read-only/full disk, or LiteDB contention when a second tab is
        // writing #293) must never crash the UI action that triggered it (a pin toggle, an F3 change).
        var store = new ConfigStore(new ThrowOnSaveStore());

        var ex = Record.Exception(() => store.Save(new AppConfig { WorkspaceId = "1", PersonalTasksListId = "2" }));

        Assert.Null(ex);
    }

    [Fact]
    public void Save_WhenWriteFails_DoesNotAdvanceBaseline_SoTheChangeSurvivesTheNextSave()
    {
        // The baseline must advance only after a successful write. If a failed (swallowed) write
        // advanced it, the un-persisted field would look "unchanged" on the retry and get reverted to
        // whatever is on disk — silently losing the user's change (#293).
        new ConfigStore(new JsonFileStateStore(_dir)).Save(new AppConfig
        {
            WorkspaceId = "1",
            PersonalTasksListId = "2",
            RefreshSeconds = 60,
            BadgeDisplay = BadgeDisplay.Icons,
        });

        var faulty = new FaultyStore(new JsonFileStateStore(_dir));
        var store = new ConfigStore(faulty);
        var c = store.Load();               // baseline: refresh 60, badge Icons
        c.BadgeDisplay = BadgeDisplay.Text; // local change

        faulty.FailNextSave = true;
        store.Save(c);                      // write throws → swallowed; disk unchanged; baseline must hold

        // A concurrent tab changes the refresh on disk in the meantime.
        new ConfigStore(new JsonFileStateStore(_dir)).Save(new AppConfig
        {
            WorkspaceId = "1",
            PersonalTasksListId = "2",
            RefreshSeconds = 30,
            BadgeDisplay = BadgeDisplay.Icons,
        });

        store.Save(c);                      // retry: badge is still a real local change → must persist

        var final = new ConfigStore(new JsonFileStateStore(_dir)).Load();
        Assert.Equal(BadgeDisplay.Text, final.BadgeDisplay); // the change survived the failed write
        Assert.Equal(30, final.RefreshSeconds);              // and the concurrent tab's change is kept
    }

    [Fact]
    public void Save_WhenReReadThrows_DoesNotCrash()
    {
        // The merge re-read runs before the write, outside its try/catch. A contention/IO error there
        // (not just a JsonException) must not escape Save and crash the triggering UI action (#293).
        var faulty = new FaultyStore(new JsonFileStateStore(_dir));
        var store = new ConfigStore(faulty);
        store.Save(new AppConfig { WorkspaceId = "1", PersonalTasksListId = "2" }); // sets the baseline

        faulty.FailReads = true; // the next Save's merge re-read will throw

        var ex = Record.Exception(() =>
            store.Save(new AppConfig { WorkspaceId = "1", PersonalTasksListId = "2", RefreshSeconds = 45 }));

        Assert.Null(ex);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A store whose write always fails — stands in for a read-only/full disk or a LiteDB
    /// contention error, so a test can assert <see cref="ConfigStore.Save"/> swallows it.</summary>
    private sealed class ThrowOnSaveStore : IStateStore
    {
        public bool Exists(string key) => false;
        public T? Load<T>(string key) where T : class => null;
        public void Save<T>(string key, T value) where T : class => throw new IOException("disk full");
        public void Delete(string key) { }
    }

    /// <summary>Wraps a real store and can be armed to fail the next write (once) or all reads — lets a
    /// test drive the failed-write and failed-re-read paths of <see cref="ConfigStore.Save"/> (#293).</summary>
    private sealed class FaultyStore(IStateStore inner) : IStateStore
    {
        public bool FailNextSave;
        public bool FailReads;

        public bool Exists(string key) => inner.Exists(key);

        public T? Load<T>(string key) where T : class
            => FailReads ? throw new IOException("read contention") : inner.Load<T>(key);

        public void Save<T>(string key, T value) where T : class
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("disk full");
            }
            inner.Save(key, value);
        }

        public void Delete(string key) => inner.Delete(key);
    }
}
