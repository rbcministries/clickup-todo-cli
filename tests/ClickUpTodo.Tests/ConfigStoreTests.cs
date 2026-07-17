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
                LaunchLocation = LaunchLocation.NewTab,
                ClaudeExecutable = "/opt/claude",
                ExtraArgs = ["--model", "opus"],
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
        Assert.Equal(LaunchLocation.NewTab, d.LaunchLocation);
        Assert.Equal("/opt/claude", d.ClaudeExecutable);
        Assert.Equal(["--model", "opus"], d.ExtraArgs);
        Assert.Equal(AgentWorkingDirectory.Fixed, d.WorkingDirectory);
        Assert.Equal("/work", d.FixedWorkingDirectory);
        Assert.Equal(AgentSessionMode.OneOff, d.DefaultSessionMode);
        Assert.True(d.DefaultPostResultsToComments);
        Assert.Equal("Lead: {userPrompt}\n{contextJson}", d.PromptTemplate);
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

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
