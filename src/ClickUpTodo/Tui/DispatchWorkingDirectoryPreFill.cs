using ClickUpTodo.Agent;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tui;

/// <summary>
/// The <b>single place</b> a task-derived Dispatch working directory is decided (#533). It computes the
/// value the Ctrl+A Dispatch pane's working-dir field opens with — so the derivation the launch used to
/// do silently (<see cref="RepositoryWorkingDirectory"/>'s <c>{base}/{Repository}</c> match #461, and
/// the per-task <c>{base}/{custom-id}</c> directory #98) is now visible, editable, and honoured
/// (clearing the field rejects it). <see cref="Tui.DispatchCoordinator.Plan"/> is left pure and no
/// longer derives anything.
/// <para>
/// Pure: the filesystem is injected as delegates (real-FS defaults), mirroring
/// <see cref="RepositoryWorkingDirectory"/>, so the precedence logic is unit-testable against in-memory
/// directory sets. Both hosts (<see cref="TodoApp"/>, <see cref="SingleTaskApp"/>) call it through the
/// existing <c>workingDirectoryPreFill</c> delegate so the two can't drift.
/// </para>
/// </summary>
public static class DispatchWorkingDirectoryPreFill
{
    /// <summary>
    /// The value the Dispatch pane's working-dir field opens with for <paramref name="taskId"/>, applying
    /// the settled #533 precedence in <b>task-derived</b> mode: the #96 per-task cache (the last explicit
    /// dir dispatched from this task) → a <c>{base}/{Repository}</c> checkout match (#461) → the per-task
    /// <c>{base}/{custom-id}</c> directory (#98). In <see cref="AgentWorkingDirectory.Home"/> /
    /// <see cref="AgentWorkingDirectory.Fixed"/> mode there is no derivation pre-fill — the field opens on
    /// the #96 cache when present, else blank ("use my configured mode", decision 4). A blank return
    /// launches in the plain base dir.
    /// </summary>
    public static string PreFill(
        IReadOnlyDictionary<string, string> cache,
        string taskId,
        TaskDetail detail,
        AgentDispatchSettings settings,
        string baseDirectory,
        Func<string, bool>? directoryExists = null,
        Func<string, IReadOnlyList<string>>? childDirectoryNames = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(settings);

        // #96 first, in every mode: the cache is mode-independent, so this preserves today's Home/Fixed
        // pre-fill behaviour exactly (decision 4 — Home/Fixed are otherwise untouched).
        var cached = DispatchWorkingDirectoryCache.PreFill(cache, taskId);
        if (cached.Length != 0)
            return cached;

        // Derivation is task-derived-only: Home/Fixed open blank so a blank field still means "use my
        // configured mode" rather than being overridden by a derived path.
        if (settings.WorkingDirectory != AgentWorkingDirectory.TaskDerived)
            return "";

        return TaskDerivedDefault(detail, baseDirectory, directoryExists, childDirectoryNames);
    }

    /// <summary>
    /// The directory an <b>accepted-unchanged</b> pre-fill resolves to — the #96 cache-reconciliation
    /// baseline (<see cref="Configuration.DispatchWorkingDirectoryCache.Update"/>'s <c>resolvedDefault</c>).
    /// Deliberately <b>excludes</b> the #96 cache (including it would be circular — accepting the cached
    /// value would always look like "reverted to default" and clear itself). In task-derived mode this is
    /// the repo match else <c>{base}/{custom-id}</c> — the same value <see cref="PreFill"/> would produce
    /// with no cache entry, so accepting the pre-filled value writes no cache entry and a stored entry
    /// equal to it is cleared. In Home/Fixed mode it is the configured directory (<c>~</c>-expanded),
    /// byte-identical to the pre-#533 <c>resolvedDefault</c>.
    /// </summary>
    public static string? AutoDerivedDefault(
        TaskDetail detail,
        AgentDispatchSettings settings,
        string baseDirectory,
        string home,
        Func<string, bool>? directoryExists = null,
        Func<string, IReadOnlyList<string>>? childDirectoryNames = null)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.WorkingDirectory == AgentWorkingDirectory.TaskDerived)
            return TaskDerivedDefault(detail, baseDirectory, directoryExists, childDirectoryNames);

        // Home/Fixed: the configured dir, ~-expanded, exactly as Plan resolved it before #533.
        var raw = settings.ResolveWorkingDirectory(taskDerivedDirectory: baseDirectory, homeDirectory: home);
        return raw is null ? null : SettingsForm.ExpandHomePath(raw, home);
    }

    /// <summary>
    /// The task-derived, <b>cache-independent</b> default: the <c>{base}/{Repository}</c> checkout when a
    /// <c>Repository</c> field matches a direct child of <paramref name="baseDirectory"/> (#461), else the
    /// per-task <c>{base}/{custom-id}</c> directory (#98). Never blank —
    /// <see cref="AgentPromptComposer.OutputSubdirectoryToken"/> always yields at least <c>"task"</c>.
    /// </summary>
    private static string TaskDerivedDefault(
        TaskDetail detail,
        string baseDirectory,
        Func<string, bool>? directoryExists,
        Func<string, IReadOnlyList<string>>? childDirectoryNames)
    {
        directoryExists ??= Directory.Exists;
        childDirectoryNames ??= SystemChildDirectoryNames;

        if (RepositoryWorkingDirectory.Resolve(detail, baseDirectory, directoryExists, childDirectoryNames) is { } match)
            return match.Directory;

        return Path.Combine(baseDirectory, AgentPromptComposer.OutputSubdirectoryToken(detail));
    }

    /// <summary>The immediate child <b>directory</b> names of <paramref name="dir"/> (the real-filesystem
    /// default for the #461 case-insensitive repo scan); empty when the dir is missing or unreadable, so a
    /// filesystem hiccup degrades to "no match", never a thrown pre-fill.</summary>
    internal static IReadOnlyList<string> SystemChildDirectoryNames(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                return [];
            var names = new List<string>();
            foreach (var path in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(name))
                    names.Add(name);
            }
            return names;
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
