using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tui.E2E;

/// <summary>
/// Dispatch pane working-dir browser seeding (#559 → PTY coverage #564). Stands up a real base
/// working directory on disk with a small, distinctively-named tree and points
/// <see cref="AppConfig.DefaultWorkingDirectory"/> at it, so the Ctrl+A Dispatch pane's directory
/// browser roots there and <see cref="Tui.DispatchWorkingDirectoryPreFill"/> derives against it.
/// <list type="bullet">
/// <item><c>E2E_DISPATCH_SEED=1</c> — <b>seeded</b> mode: the #96 per-task cache
/// (<see cref="AppConfig.TaskWorkingDirectories"/>) is seeded to an <b>existing nested</b> target
/// (<c>{base}/WTPROJECTS/SEEDTARGET</c>), so opening the pane seeds the browser to the target's
/// parent (<c>WTPROJECTS</c>) with <c>SEEDTARGET</c> highlighted. A nested target makes the
/// parent-of-target listing observably distinct from the base-root listing on the rendered screen.</item>
/// <item><c>E2E_DISPATCH_SEED_DEGRADE=1</c> — same base tree, but the cache is <b>not</b> seeded, so
/// the task-derived pre-fill is <c>{base}/{taskId}</c> which does not exist on disk: the browser
/// degrades to the base root (today's behaviour), field preserved.</item>
/// </list>
/// The #96 cached-dir source is one of the three <see cref="Tui.DispatchWorkingDirectoryPreFill"/>
/// honours (#564 sanctions "a <c>{base}/{Repository}</c> checkout match, or the #96 cached dir"); it
/// is used here because it lets the existing target be nested. Self-contained: adding it edited nothing
/// else (the E2E harness epic #484/#489 goal).
/// </summary>
internal sealed class DispatchSeedScenario : IE2EScenario
{
    /// <summary>The nested existing target the seeded browser highlights, relative to the base dir.</summary>
    private const string TargetProjectsDir = "WTPROJECTS";
    private const string TargetLeafDir = "SEEDTARGET";

    // The base dir + tree are created once, in Configure, before the app boots.
    private string? _baseDir;

    public string Name => "dispatch-seed";
    public bool IsActive => Environment.GetEnvironmentVariable("E2E_DISPATCH_SEED") == "1";

    private static bool Degrade => Environment.GetEnvironmentVariable("E2E_DISPATCH_SEED_DEGRADE") == "1";

    public void Configure(AppConfig config)
    {
        var baseDir = EnsureTree();
        config.DefaultWorkingDirectory = baseDir;

        if (Degrade)
            return;

        // Seed the #96 per-task working-dir cache to the existing nested target for a generous range of
        // default task ids, so whichever task the first list row resolves to derives the existing target
        // (no dependence on row ordering). PreFill reads the cache first, in every mode.
        var target = Path.Combine(baseDir, TargetProjectsDir, TargetLeafDir);
        for (var i = 0; i < 300; i++)
            config.TaskWorkingDirectories[$"t{i}"] = target;
    }

    /// <summary>
    /// Creates (once) the on-disk base working directory and its tree, and returns the base path:
    /// <code>
    /// {base}/AAAROOTKID
    /// {base}/WTPROJECTS/{SEEDTARGET, SIBLINGONE, SIBLINGTWO}
    /// {base}/ZZZROOTKID
    /// </code>
    /// The names are distinctive uppercase tokens so they are unambiguous on the pyte screen. A unique
    /// per-process root under the temp dir keeps concurrent harness runs from colliding.
    /// </summary>
    private string EnsureTree()
    {
        if (_baseDir is not null)
            return _baseDir;

        // A short base path (not the usual long temp/guid) so the full pre-fill — including the distinctive
        // leaf token — fits the pane's working-dir field width and stays visible on the pyte screen rather
        // than scrolling off the left. Eight hex of entropy keeps concurrent harness runs from colliding.
        var baseDir = Path.Combine(
            Path.GetTempPath(), "cutd-" + Guid.NewGuid().ToString("N")[..8], "base");
        Directory.CreateDirectory(Path.Combine(baseDir, "AAAROOTKID"));
        Directory.CreateDirectory(Path.Combine(baseDir, "ZZZROOTKID"));
        Directory.CreateDirectory(Path.Combine(baseDir, TargetProjectsDir, TargetLeafDir));
        Directory.CreateDirectory(Path.Combine(baseDir, TargetProjectsDir, "SIBLINGONE"));
        Directory.CreateDirectory(Path.Combine(baseDir, TargetProjectsDir, "SIBLINGTWO"));
        return _baseDir = baseDir;
    }
}
