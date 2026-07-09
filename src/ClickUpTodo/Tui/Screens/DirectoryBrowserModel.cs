namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// The stateful directory browser backing the Dispatch pane's working-directory file-tree (issue
/// #95, D3 of the #90 epic). It holds the directory currently being browsed and its immediate
/// subdirectory listing (".." first, then the subdirectory names sorted case-insensitively) and the
/// navigation operations the Terminal.Gui <c>ListView</c> glue drives. This is the only
/// filesystem-touching piece — enumeration is resilient (an unreadable / missing / invalid directory
/// lists as just <c>[".."]</c> rather than throwing) and bounded for very large directories — so the
/// CI-untestable glue in <see cref="TaskDetailScreen"/> stays thin. Tested against scratch dirs.
/// </summary>
public sealed class DirectoryBrowserModel
{
    /// <summary>The "up one level" entry shown first in every listing.</summary>
    public const string ParentEntry = "..";

    /// <summary>Upper bound on subdirectories listed for one directory, so a huge tree stays bounded.</summary>
    public const int MaxEntries = 2000;

    // The directory the browser was rooted at, restored by Reset() each time the pane reopens.
    private readonly string _root;

    /// <summary>The absolute directory currently being browsed.</summary>
    public string CurrentDirectory { get; private set; }

    /// <summary>The display entries: <see cref="ParentEntry"/> then the immediate subdirectory names.</summary>
    public IReadOnlyList<string> Entries { get; private set; }

    public DirectoryBrowserModel(string startDirectory)
    {
        _root = Normalize(startDirectory);
        CurrentDirectory = _root;
        Entries = List(CurrentDirectory);
    }

    /// <summary>True when the entry at <paramref name="index"/> is the "up one level" ("..") entry.</summary>
    public bool IsParent(int index) => index == 0;

    /// <summary>
    /// The absolute path the entry at <paramref name="index"/> refers to: the parent directory for
    /// "..", the highlighted subdirectory otherwise. An out-of-range index resolves to the current
    /// directory (a safe no-op for the caller).
    /// </summary>
    public string PathAt(int index)
    {
        if (IsParent(index))
            return Parent(CurrentDirectory);
        if (index > 0 && index < Entries.Count)
            return Path.Combine(CurrentDirectory, Entries[index]);
        return CurrentDirectory;
    }

    /// <summary>Moves up one level (a no-op at a filesystem root) and repopulates <see cref="Entries"/>.</summary>
    public void NavigateUp() => MoveTo(Parent(CurrentDirectory));

    /// <summary>
    /// Descends into the entry at <paramref name="index"/> (or up, for ".."), repopulating the listing
    /// so the browser can go deeper. Selecting a working directory is the caller's job (it reads
    /// <see cref="PathAt"/>); this only changes what is shown.
    /// </summary>
    public void Descend(int index) => MoveTo(PathAt(index));

    /// <summary>Returns the browser to the directory it was rooted at.</summary>
    public void Reset() => MoveTo(_root);

    private void MoveTo(string directory)
    {
        CurrentDirectory = Normalize(directory);
        Entries = List(CurrentDirectory);
    }

    /// <summary>
    /// The absolute, trailing-separator-trimmed form of <paramref name="directory"/> (blank ⇒ the
    /// current working directory). A filesystem root ("/" on POSIX, "C:\" on Windows) is preserved
    /// intact so <see cref="Parent"/> can detect it.
    /// </summary>
    public static string Normalize(string? directory)
    {
        var input = string.IsNullOrWhiteSpace(directory) ? Directory.GetCurrentDirectory() : directory.Trim();
        string full;
        try
        {
            full = Path.GetFullPath(input);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // A malformed path can't be canonicalized; fall back to the raw input so navigation stays
            // resilient (List() will then surface just "..") rather than throwing into the key handler.
            return input;
        }
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Trimming a root would leave "" (POSIX "/") or a bare drive ("C:") — keep the full form there.
        if (trimmed.Length == 0 || (trimmed.Length == 2 && trimmed[1] == ':'))
            return full;
        return trimmed;
    }

    /// <summary>The absolute parent of <paramref name="directory"/>, or itself at a filesystem root.</summary>
    public static string Parent(string directory)
    {
        var normalized = Normalize(directory);
        var parent = Path.GetDirectoryName(normalized);
        // GetDirectoryName returns null/empty for a root; a root's parent is the root itself.
        return string.IsNullOrEmpty(parent) ? normalized : parent;
    }

    private static IReadOnlyList<string> List(string directory)
    {
        var names = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(name))
                    names.Add(name);
                if (names.Count >= MaxEntries)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable / missing / invalid directory: fall through to just "..", so the user can
            // always navigate back out rather than getting stuck or crashing the pane.
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        var entries = new List<string>(names.Count + 1) { ParentEntry };
        entries.AddRange(names);
        return entries;
    }
}
