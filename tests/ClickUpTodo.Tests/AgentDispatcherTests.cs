using ClickUpTodo.Agent;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

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
        public bool OneOff { get; private set; }
        public LaunchResult Result { get; init; } = LaunchResult.Ok("Windows Terminal");

        public Task<LaunchResult> LaunchAsync(
            string promptFilePath, string? workingDir, TerminalLauncherOptions options, bool oneOff = false, CancellationToken ct = default)
        {
            PromptFilePath = promptFilePath;
            WorkingDir = workingDir;
            Options = options;
            OneOff = oneOff;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeBackgroundRunner : IBackgroundAgentRunner
    {
        public string? PromptFilePath { get; private set; }
        public string? PromptContent { get; private set; }
        public bool PromptFileExistedDuringRun { get; private set; }
        public string? WorkingDir { get; private set; }
        public TerminalLauncherOptions? Options { get; private set; }
        public IProgress<string>? Progress { get; private set; }
        public BackgroundRunResult Result { get; init; } = BackgroundRunResult.Exited(0, "done", null);
        public Func<CancellationToken, Task>? OnRun { get; init; }

        /// <summary>Scripted display chunks the runner reports to <paramref name="progress"/> before
        /// returning, mirroring how the real runner streams parsed stream-json lines (#187).</summary>
        public IReadOnlyList<string> Chunks { get; init; } = [];

        public async Task<BackgroundRunResult> RunAsync(
            string promptFilePath, string? workingDir, TerminalLauncherOptions options,
            IProgress<string>? progress = null, CancellationToken ct = default)
        {
            PromptFilePath = promptFilePath;
            WorkingDir = workingDir;
            Options = options;
            Progress = progress;
            PromptFileExistedDuringRun = File.Exists(promptFilePath);
            if (File.Exists(promptFilePath))
                PromptContent = File.ReadAllText(promptFilePath);
            foreach (var chunk in Chunks)
                progress?.Report(chunk);
            if (OnRun is not null)
                await OnRun(ct).ConfigureAwait(false);
            return Result;
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

        // The file content is exactly what the composer produces (default template render).
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
    public async Task DispatchAsync_PassesTemplateThrough_ToComposedFile()
    {
        const string template = "CUSTOM LEAD: {userPrompt}\n---\n{taskJson}";
        var launcher = new FakeLauncher();
        var dispatcher = new AgentDispatcher(launcher, promptDirectory: _dir);
        var task = Detail();
        var comments = Comments();

        var result = await dispatcher.DispatchAsync(task, comments, "go", template: template);

        // The custom template renders the composed file, replacing the default layout/preamble.
        var expected = AgentPromptComposer.Compose(task, comments, "go", template);
        Assert.Equal(expected, File.ReadAllText(result.PromptFilePath));
        Assert.Contains("CUSTOM LEAD:", File.ReadAllText(result.PromptFilePath));
        Assert.DoesNotContain(AgentPromptComposer.Preamble, File.ReadAllText(result.PromptFilePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DispatchAsync_BlankTemplate_UsesDefault(string? template)
    {
        var launcher = new FakeLauncher();
        var dispatcher = new AgentDispatcher(launcher, promptDirectory: _dir);
        var task = Detail();
        var comments = Comments();

        var result = await dispatcher.DispatchAsync(task, comments, "go", template: template);

        // A blank template keeps the default — byte-for-byte the default-template compose.
        Assert.Equal(AgentPromptComposer.Compose(task, comments, "go"), File.ReadAllText(result.PromptFilePath));
    }

    // ── output subdirectory (task-derived working dir, #98) ─────────────────────────

    [Fact]
    public async Task DispatchAsync_PassesOutputSubdirectory_ToComposedFile()
    {
        var launcher = new FakeLauncher();
        var dispatcher = new AgentDispatcher(launcher, promptDirectory: _dir);
        var task = Detail();
        var comments = Comments();

        var result = await dispatcher.DispatchAsync(task, comments, "go", outputSubdirectory: "TEAM-42");

        Assert.Equal(
            AgentPromptComposer.Compose(task, comments, "go", outputSubdirectory: "TEAM-42"),
            File.ReadAllText(result.PromptFilePath));
        Assert.Contains("Write any output files to the subdirectory ./TEAM-42 (create it if needed).",
            File.ReadAllText(result.PromptFilePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DispatchAsync_BlankOutputSubdirectory_MatchesDefault(string? subdir)
    {
        var launcher = new FakeLauncher();
        var dispatcher = new AgentDispatcher(launcher, promptDirectory: _dir);
        var task = Detail();
        var comments = Comments();

        var result = await dispatcher.DispatchAsync(task, comments, "go", outputSubdirectory: subdir);

        Assert.Equal(AgentPromptComposer.Compose(task, comments, "go"), File.ReadAllText(result.PromptFilePath));
    }

    [Fact]
    public async Task Dispatcher_TaskDerivedGlue_LaunchesInBaseDir_AndInjectsSubdirInstruction()
    {
        // Mirrors the TodoApp task-derived glue at the unit-testable seam: the base working dir (#92)
        // becomes the launch cwd, and the task's output-subdir token seeds the prompt instruction.
        var settings = new AgentDispatchSettings(); // default ⇒ TaskDerived
        var launcher = new FakeLauncher();
        var dispatcher = new AgentDispatcher(launcher, settings.ToLauncherOptions(), _dir);
        var task = Detail(id: "abc123");
        var comments = Comments();

        var baseDir = "/home/me/ClickUp-Tasks";
        var workingDir = settings.ResolveWorkingDirectory(taskDerivedDirectory: baseDir, homeDirectory: "/home/me");
        var subdir = AgentPromptComposer.OutputSubdirectoryToken(task);

        var result = await dispatcher.DispatchAsync(task, comments, "go", workingDir, settings.PromptTemplate, subdir);

        Assert.Equal(baseDir, launcher.WorkingDir);
        Assert.Equal(
            AgentPromptComposer.Compose(task, comments, "go", outputSubdirectory: "abc123"),
            File.ReadAllText(result.PromptFilePath));
    }

    // ── post-results-to-Comments (#97) ──────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_PassesPostToComments_ToComposedFile()
    {
        var launcher = new FakeLauncher();
        var dispatcher = new AgentDispatcher(launcher, promptDirectory: _dir);
        var task = Detail(id: "abc123");
        var comments = Comments();

        var result = await dispatcher.DispatchAsync(task, comments, "go", postToComments: true);

        Assert.Equal(
            AgentPromptComposer.Compose(task, comments, "go", postToComments: true),
            File.ReadAllText(result.PromptFilePath));
        Assert.Contains("post a brief summary comment of your work to ClickUp task abc123",
            File.ReadAllText(result.PromptFilePath));
    }

    [Fact]
    public async Task DispatchAsync_DefaultsPostToCommentsOff()
    {
        var launcher = new FakeLauncher();
        var dispatcher = new AgentDispatcher(launcher, promptDirectory: _dir);
        var task = Detail();
        var comments = Comments();

        var result = await dispatcher.DispatchAsync(task, comments, "go");

        // Omitting the flag is byte-identical to a plain dispatch (no comment instruction).
        Assert.Equal(AgentPromptComposer.Compose(task, comments, "go"), File.ReadAllText(result.PromptFilePath));
        Assert.DoesNotContain("summary comment", File.ReadAllText(result.PromptFilePath));
    }

    // ── session mode (one-off vs interactive, #94) ──────────────────────────────────

    [Fact]
    public async Task DispatchAsync_DefaultsToInteractive_NotOneOff()
    {
        var launcher = new FakeLauncher();
        var dispatcher = new AgentDispatcher(launcher, promptDirectory: _dir);

        await dispatcher.DispatchAsync(Detail(), Comments(), "go");

        Assert.False(launcher.OneOff); // omitting the flag preserves the interactive session
    }

    [Fact]
    public async Task DispatchAsync_ThreadsOneOff_ToLauncher()
    {
        var launcher = new FakeLauncher();
        var dispatcher = new AgentDispatcher(launcher, promptDirectory: _dir);

        await dispatcher.DispatchAsync(Detail(), Comments(), "go", oneOff: true);

        Assert.True(launcher.OneOff);
    }

    // ── per-dispatch launch location override (#275) ─────────────────────────────────

    [Fact]
    public async Task DispatchAsync_ThreadsLaunchLocationOverride_ToLauncherOptions()
    {
        var launcher = new FakeLauncher();
        // Constructor options default LaunchLocation to NewWindow; the per-dispatch override (#275)
        // flips just this launch to NewTab.
        var options = new TerminalLauncherOptions { ClaudeExecutable = "claude-dev", ExtraArgs = ["--model", "opus"] };
        var dispatcher = new AgentDispatcher(launcher, options, _dir);

        await dispatcher.DispatchAsync(Detail(), Comments(), "go", launchLocation: LaunchLocation.NewTab);

        Assert.Equal(LaunchLocation.NewTab, launcher.Options!.LaunchLocation);
        // Only the launch location is overridden — the rest of the settings-derived options survive.
        Assert.Equal("claude-dev", launcher.Options!.ClaudeExecutable);
        Assert.Equal(["--model", "opus"], launcher.Options!.ExtraArgs);
    }

    [Fact]
    public async Task DispatchAsync_NoLaunchLocationOverride_KeepsConfiguredOptionsInstance()
    {
        var launcher = new FakeLauncher();
        var options = new TerminalLauncherOptions { LaunchLocation = LaunchLocation.NewTab };
        var dispatcher = new AgentDispatcher(launcher, options, _dir);

        // Omitting the override leaves the settings-derived options untouched — same instance, so the
        // configured LaunchLocation (here NewTab) still applies.
        await dispatcher.DispatchAsync(Detail(), Comments(), "go");

        Assert.Same(options, launcher.Options);
        Assert.Equal(LaunchLocation.NewTab, launcher.Options!.LaunchLocation);
    }

    [Fact]
    public async Task DispatchAsync_LaunchLocationOverride_WinsOverConfiguredDefault()
    {
        var launcher = new FakeLauncher();
        var options = new TerminalLauncherOptions { LaunchLocation = LaunchLocation.NewTab };
        var dispatcher = new AgentDispatcher(launcher, options, _dir);

        // An explicit NewWindow override beats a NewTab settings default for this one launch.
        await dispatcher.DispatchAsync(Detail(), Comments(), "go", launchLocation: LaunchLocation.NewWindow);

        Assert.Equal(LaunchLocation.NewWindow, launcher.Options!.LaunchLocation);
    }

    // ── Settings → dispatcher wiring (#91, #100) ─────────────────────────────────────
    // These mirror the glue TodoApp performs (options from ToLauncherOptions, working dir from
    // ResolveWorkingDirectory, template from settings) at the unit-testable dispatcher seam, since
    // the TodoApp wiring itself is Terminal.Gui and not testable in CI.

    [Fact]
    public async Task Dispatcher_BuiltFromSettings_ForwardsOptionsWorkingDirAndTemplate()
    {
        const string template = "CUSTOM LEAD: {userPrompt}\n---\n{contextJson}";
        var settings = new AgentDispatchSettings
        {
            ClaudeExecutable = "claude-dev",
            ExtraArgs = ["--model", "opus"],
            PreferredTerminal = PreferredTerminal.Pwsh,
            WorkingDirectory = AgentWorkingDirectory.Fixed,
            FixedWorkingDirectory = "/projects/foo",
            PromptTemplate = template,
        };
        var launcher = new FakeLauncher();
        var dispatcher = new AgentDispatcher(launcher, settings.ToLauncherOptions(), _dir);
        var workingDir = settings.ResolveWorkingDirectory(taskDerivedDirectory: null, homeDirectory: "/home/me");
        var task = Detail();
        var comments = Comments();

        var result = await dispatcher.DispatchAsync(task, comments, "go", workingDir, settings.PromptTemplate);

        Assert.Equal("claude-dev", launcher.Options!.ClaudeExecutable);
        Assert.Equal(["--model", "opus"], launcher.Options!.ExtraArgs);
        Assert.Equal(PreferredTerminal.Pwsh, launcher.Options!.Preferred);
        Assert.Equal("/projects/foo", launcher.WorkingDir);
        Assert.Equal(AgentPromptComposer.Compose(task, comments, "go", template),
            File.ReadAllText(result.PromptFilePath));
    }

    [Fact]
    public async Task Dispatcher_BuiltFromDefaultSettings_MatchesZeroConfigBehaviour()
    {
        var settings = new AgentDispatchSettings(); // all defaults ⇒ zero-config
        var launcher = new FakeLauncher();
        var dispatcher = new AgentDispatcher(launcher, settings.ToLauncherOptions(), _dir);
        var workingDir = settings.ResolveWorkingDirectory(taskDerivedDirectory: null, homeDirectory: "/home/me");
        var task = Detail();
        var comments = Comments();

        var result = await dispatcher.DispatchAsync(task, comments, "go", workingDir, settings.PromptTemplate);

        Assert.Equal("claude", launcher.Options!.ClaudeExecutable);
        Assert.Empty(launcher.Options!.ExtraArgs);
        Assert.Equal(PreferredTerminal.Auto, launcher.Options!.Preferred);
        Assert.Null(launcher.WorkingDir); // TaskDerived + null candidate ⇒ inherit
        Assert.Equal(AgentPromptComposer.Compose(task, comments, "go"), File.ReadAllText(result.PromptFilePath));
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

    // ── background one-off run (#99) ─────────────────────────────────────────────────
    // These mirror the DispatchAsync tests at the background seam: a fake runner captures what it was
    // handed, so no real `claude` is spawned; the composed prompt file lives in a scratch directory and
    // is deleted by the background path once the run finishes.

    [Fact]
    public async Task DispatchBackgroundAsync_ComposesPromptFile_AndHandsContentWorkingDirAndOptionsToRunner()
    {
        var runner = new FakeBackgroundRunner();
        var options = new TerminalLauncherOptions();
        var dispatcher = new AgentDispatcher(new FakeLauncher(), options, _dir, runner);
        var task = Detail();
        var comments = Comments();

        var result = await dispatcher.DispatchBackgroundAsync(task, comments, "please triage this", workingDir: "/work");

        // The runner saw the exact composed prompt (default template render), plus the working dir + options.
        Assert.True(runner.PromptFileExistedDuringRun);
        Assert.StartsWith(_dir, runner.PromptFilePath);
        Assert.Equal("/work", runner.WorkingDir);
        Assert.Same(options, runner.Options);
        Assert.Equal(AgentPromptComposer.Compose(task, comments, "please triage this"), runner.PromptContent);

        Assert.True(result.Success);
        Assert.Equal("done", result.Output);
    }

    [Fact]
    public async Task DispatchBackgroundAsync_DeletesPromptFile_AfterTheRun()
    {
        var runner = new FakeBackgroundRunner();
        var dispatcher = new AgentDispatcher(new FakeLauncher(), promptDirectory: _dir, backgroundRunner: runner);

        await dispatcher.DispatchBackgroundAsync(Detail(), Comments(), "go");

        // The file existed while the runner ran, but the background path cleans it up afterwards.
        Assert.True(runner.PromptFileExistedDuringRun);
        Assert.NotNull(runner.PromptFilePath);
        Assert.False(File.Exists(runner.PromptFilePath!));
    }

    [Fact]
    public async Task DispatchBackgroundAsync_ThreadsCompositionOptions_ToComposedPrompt()
    {
        var runner = new FakeBackgroundRunner();
        var dispatcher = new AgentDispatcher(new FakeLauncher(), promptDirectory: _dir, backgroundRunner: runner);
        var task = Detail(id: "abc123");
        var comments = Comments();

        await dispatcher.DispatchBackgroundAsync(
            task, comments, "go", template: null, outputSubdirectory: "TEAM-42", postToComments: true);

        var expected = AgentPromptComposer.Compose(task, comments, "go", outputSubdirectory: "TEAM-42", postToComments: true);
        Assert.Equal(expected, runner.PromptContent);
    }

    [Fact]
    public async Task DispatchBackgroundAsync_ForwardsProgress_ToRunner_AndStreamsChunks()
    {
        var chunks = new List<string>();
        var progress = new SynchronousProgress(chunks.Add);
        var runner = new FakeBackgroundRunner { Chunks = ["⚙ Bash\n", "Done.\n"] };
        var dispatcher = new AgentDispatcher(new FakeLauncher(), promptDirectory: _dir, backgroundRunner: runner);

        await dispatcher.DispatchBackgroundAsync(Detail(), Comments(), "go", progress: progress);

        Assert.Same(progress, runner.Progress);
        // The runner forwarded each scripted chunk verbatim (incl. the trailing newline the real runner adds).
        Assert.Equal(["⚙ Bash\n", "Done.\n"], chunks);
    }

    /// <summary>An <see cref="IProgress{T}"/> that invokes the sink inline (no SynchronizationContext
    /// hop), so a unit test observes reported chunks deterministically.</summary>
    private sealed class SynchronousProgress(Action<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink(value);
    }

    [Fact]
    public async Task DispatchBackgroundAsync_SurfacesNonZeroExit_AsFailure()
    {
        var runner = new FakeBackgroundRunner { Result = BackgroundRunResult.Exited(2, "partial", "boom") };
        var dispatcher = new AgentDispatcher(new FakeLauncher(), promptDirectory: _dir, backgroundRunner: runner);

        var result = await dispatcher.DispatchBackgroundAsync(Detail(), Comments(), "go");

        Assert.False(result.Success);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("boom", result.Error);
    }

    [Fact]
    public async Task DispatchBackgroundAsync_Cancellation_PropagatesAndStillDeletesPromptFile()
    {
        var runner = new FakeBackgroundRunner
        {
            OnRun = ct => Task.FromCanceled(new CancellationToken(canceled: true)),
        };
        var dispatcher = new AgentDispatcher(new FakeLauncher(), promptDirectory: _dir, backgroundRunner: runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dispatcher.DispatchBackgroundAsync(Detail(), Comments(), "go"));

        // The finally-block cleanup runs even when the run is cancelled.
        Assert.NotNull(runner.PromptFilePath);
        Assert.False(File.Exists(runner.PromptFilePath!));
    }

    [Fact]
    public void BackgroundRunResult_Success_OnlyWhenStartedAndExitZero()
    {
        Assert.True(BackgroundRunResult.Exited(0, "ok", null).Success);
        Assert.False(BackgroundRunResult.Exited(1, "", "err").Success);
        Assert.False(BackgroundRunResult.NotStarted("no claude").Success);
    }

    [Fact]
    public void BuildArguments_IsStreamJsonPrintMode_ThenExtraArgs_DroppingBlanks()
    {
        var options = new TerminalLauncherOptions { ExtraArgs = ["--model", "  ", "opus"] };
        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--verbose", "--model", "opus"],
            BackgroundAgentRunner.BuildArguments(options));
    }

    [Fact]
    public void BuildArguments_NoExtraArgs_IsStreamJsonPrintMode() =>
        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--verbose"],
            BackgroundAgentRunner.BuildArguments(new TerminalLauncherOptions()));

    [Fact]
    public void Ctor_NullLauncher_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new AgentDispatcher(null!));
}
