using System.Text.Json;

namespace ClickUpTodo.Configuration;

/// <summary>
/// File-backed <see cref="IStateStore"/>: each key is persisted as an indented JSON file
/// (<c>{key}.json</c>) under the per-user app data directory (<c>%APPDATA%\clickup-todo</c> on
/// Windows, <c>~/.config/clickup-todo</c> elsewhere).
/// <para>
/// This is the behaviour-preserving reimplementation of the persistence that used to live directly
/// in <see cref="ConfigStore"/>: it uses the same serializer options (camelCase, indented, enums as
/// readable strings) so <c>config.json</c> is byte-for-byte identical. The LiteDB backend (#119)
/// slots in later as an alternative <see cref="IStateStore"/> with no change to call sites.
/// </para>
/// </summary>
public sealed class JsonFileStateStore : IStateStore
{
    // The serializer contract is shared with the LiteDB backend so payloads stay byte-identical.
    private static readonly JsonSerializerOptions JsonOptions = StateJson.Options;

    /// <summary>The directory holding every state file.</summary>
    public string DirectoryPath { get; }

    public JsonFileStateStore(string? directoryPath = null)
        => DirectoryPath = directoryPath ?? DefaultDirectory();

    /// <summary>The shared per-user data directory (also home to the encrypted token file).</summary>
    public static string DefaultDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(root, "clickup-todo");
    }

    /// <summary>The on-disk path a given key maps to (file-specific; for messaging and tests).</summary>
    public string PathFor(string key) => Path.Combine(DirectoryPath, key + ".json");

    public bool Exists(string key) => File.Exists(PathFor(key));

    public T? Load<T>(string key) where T : class
    {
        var path = PathFor(key);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
            : null;
    }

    public void Save<T>(string key, T value) where T : class
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(PathFor(key), JsonSerializer.Serialize(value, JsonOptions));
    }

    public void Delete(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path))
            File.Delete(path);
    }
}
