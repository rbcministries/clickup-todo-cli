using ClickUpTodo.Agent;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="AgentDispatcher"/> (issue #26, S3): the compose->launch seam and the
/// status-line formatting. A fake <see cref="ITerminalLauncher"/> captures what the launcher is
/// handed, so no real process is spawned; the prompt file is written to a scratch directory.
/// </summary>
public sealed class AgentDispatcherTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { /* best-effort scratch cleanup */ }
    }

    private sealed class FakeLauncher : ITerminalLauncher
    {
        public string? PromptFilePath { get; private set; }
        public string? WorkingDir { get; private set; }
        public TerminalLauncherOptions? Options { get; private set; }
        public LaunchResult Result { get; init; } = LaunchResult.Ok("Windows Terminal");

        public Task<LaunchResult> LaunchAsync(
            string promptFilePath, string? workingDir, TerminalLauncherOptions options, CancellationToken ct = default)
        {
            PromptFilePath = promptFilePath;
            WorkingDir = workingDir;
            Options = options;
            return Task.FromResult(Result);
        }
    }

    private static TaskDetail Detail(string id = "abc123", string name = "Ship the Q3 report") =>
        new() { Id = id, Name = name };

    private static IReadOnlyList<CommentItem> Comments() =>
        [new CommentItem("c1", "Alice", 1_700_000_000_000, "Looks good", false)];

    [Fact]
    public async Task DispatchAsync_WritesPromptFile_AndHandsPathWorkingDirAndOptionsToLauncher()
    {
        var launcher = new FakeLauncher();
        var options = new TerminalLauncherOptions();
        var dispatcher = new AgentDispatcher(launcher, options, _dir);
        var task = Detail();
        var comments = Comments();

        var result = await dispatcher.DispatchAsync(task, comments, "please triage this", workingDir: "/work");

        // The launcher received the exact file the dispatcher wrote, plus the working dir + options.
        Assert.NotNull(launcher.PromptFilePath);
        Assert.True(File.Exists(launcher.PromptFilePath));
        Assert.StartsWith(_dir, launcher.PromptFilePath);
        Assert.Equal("/work", launcher.WorkingDir);
        Assert.Same(options, launcher.Options);

        // The file content is exactly what the composer produces (prompt + preamble + JSON).
        var expected = AgentPromptComposer.Compose(task, comments, "please triage this");
        Assert.Equal(expected, File.ReadAllText(launcher.PromptFilePath!));

        Assert.True(result.Success);
        Assert.Equal(launcher.PromptFilePath, result.PromptFilePath);
        Assert.Equal("Launched Claude (Windows Terminal) for 'Ship the Q3 report'.", result.StatusMessage);
    }

    [Fact]
    public async Task DispatchAsync_LauncherFailure_ReportsError_ButStillWroteTheFile()
    {
        var launcher = new FakeLauncher { Result = LaunchResult.Fail("No terminal emulator found to launch Claude.") };
        var dispatcher = new AgentDispatcher(launcher, promptDirectory: _dir);

        var result = await dispatcher.DispatchAsync(Detail(), Comments(), "go");

        Assert.False(result.Success);
        Assert.Equal("Could not launch Claude: No terminal emulator found to launch Claude.", result.StatusMessage);
        // The file is written before the launch is attempted, so it exists even on failure.
        Assert.True(File.Exists(result.PromptFilePath));
    }

    [Fact]
    public async Task DispatchAsync_PassesConfiguredOptionsThrough()
    {
        var launcher = new FakeLauncher();
        var options = new TerminalLauncherOptions { ClaudeExecutable = "claude-dev", ExtraArgs = ["--model", "opus"] };
        var dispatcher = new AgentDispatcher(launcher, options, _dir);

        await dispatcher.DispatchAsync(Detail(), Comments(), "go");

        Assert.Same(options, launcher.Options);
        Assert.Equal("claude-dev", launcher.Options!.ClaudeExecutable);
    }

    [Fact]
    public async Task DispatchAsync_NullComments_TreatedAsEmpty()
    {
        var launcher = new FakeLauncher();
        var dispatcher = new AgentDispatcher(launcher, promptDirectory: _dir);
        var task = Detail();

        var result = await dispatcher.DispatchAsync(task, comments: null!, "go");

        var expected = AgentPromptComposer.Compose(task, [], "go");
        Assert.Equal(expected, File.ReadAllText(result.PromptFilePath));
    }

    [Fact]
    public async Task DispatchAsync_DefaultWorkingDir_IsNull()
    {
        var launcher = new FakeLauncher();
        var dispatcher = new AgentDispatcher(launcher, promptDirectory: _dir);

        await dispatcher.DispatchAsync(Detail(), Comments(), "go");

        Assert.Null(launcher.WorkingDir);
    }

    [Fact]
    public void FormatStatus_Success_NamesTerminalAndTask()
    {
        var message = AgentDispatcher.FormatStatus("Ship it", LaunchResult.Ok("PowerShell (pwsh)"));
        Assert.Equal("Launched Claude (PowerShell (pwsh)) for 'Ship it'.", message);
    }

    [Fact]
    public void FormatStatus_Success_WithNote_AppendsNote()
    {
        var note = "'claude' was not found on PATH — it must be available in the new terminal.";
        var message = AgentDispatcher.FormatStatus("Ship it", LaunchResult.Ok("Windows Terminal", note));
        Assert.Equal($"Launched Claude (Windows Terminal) for 'Ship it'. {note}", message);
    }

    [Fact]
    public void FormatStatus_Failure_ShowsError()
    {
        var message = AgentDispatcher.FormatStatus("Ship it", LaunchResult.Fail("boom"));
        Assert.Equal("Could not launch Claude: boom", message);
    }

    [Fact]
    public void Ctor_NullLauncher_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new AgentDispatcher(null!));
}
