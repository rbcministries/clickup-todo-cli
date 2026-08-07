namespace ClickUpTodo.Agent;

/// <summary>
/// Locates and reads the Windows Terminal <c>settings.json</c> for the "Try to use WT profiles"
/// dispatch feature (#462). There is no supported <c>wt</c> query API for profiles, so the file is
/// read directly. Its path varies by install (Store stable / Preview / Canary packages, and an
/// unpackaged install), so a fixed candidate list is probed and the first that exists is used.
/// <para>
/// Path building (<see cref="CandidatePaths"/>) is pure; the read (<see cref="Load"/>) takes injected
/// filesystem delegates so both are unit-testable. On macOS/Linux <c>%LOCALAPPDATA%</c> is unset, so
/// there are no candidates and <see cref="Load"/> returns <c>null</c> — the feature is naturally inert
/// off Windows without a platform check.
/// </para>
/// </summary>
public static class WindowsTerminalSettings
{
    // Package family names of the Store-distributed Windows Terminal builds, in preference order
    // (stable first). Each keeps its settings under LocalState\settings.json inside its package dir.
    private static readonly string[] PackageFamilies =
    [
        "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
        "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe",
        "Microsoft.WindowsTerminalCanary_8wekyb3d8bbwe",
    ];

    /// <summary>
    /// The ordered <c>settings.json</c> paths to probe, derived from <c>%LOCALAPPDATA%</c>: each Store
    /// package's <c>LocalState\settings.json</c> (stable → Preview → Canary), then the unpackaged
    /// install's <c>Microsoft\Windows Terminal\settings.json</c>. Empty when <c>%LOCALAPPDATA%</c> is
    /// unset (e.g. on macOS/Linux), so the caller finds nothing and the feature no-ops.
    /// </summary>
    public static IReadOnlyList<string> CandidatePaths(Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(getEnv);
        var localAppData = getEnv("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
            return [];

        var paths = new List<string>();
        foreach (var family in PackageFamilies)
            paths.Add(Path.Combine(localAppData, "Packages", family, "LocalState", "settings.json"));
        paths.Add(Path.Combine(localAppData, "Microsoft", "Windows Terminal", "settings.json"));
        return paths;
    }

    /// <summary>
    /// Reads the contents of the first existing <c>settings.json</c> candidate, or <c>null</c> when none
    /// exists or the read fails. Filesystem access is injected (<paramref name="fileExists"/> /
    /// <paramref name="readAllText"/>) so this is unit-testable; the real dispatch passes
    /// <see cref="File.Exists(string)"/> / <see cref="File.ReadAllText(string)"/>. A read that throws
    /// (locked / unreadable file) degrades to <c>null</c> so a filesystem hiccup never fails a dispatch.
    /// </summary>
    public static string? Load(Func<string, string?> getEnv, Func<string, bool> fileExists, Func<string, string> readAllText)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(readAllText);

        foreach (var path in CandidatePaths(getEnv))
        {
            if (!fileExists(path))
                continue;
            try
            {
                return readAllText(path);
            }
            // "Never fail a dispatch": every realistic read failure — locked/unreadable file, an ACL
            // denial, or a pathological path — degrades to "no settings" rather than throwing out of the
            // inline loader in DispatchCoordinator.Plan and aborting the whole dispatch.
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                    or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
        return null;
    }
}
