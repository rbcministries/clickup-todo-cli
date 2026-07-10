using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace ClickUpTodo.Configuration;

/// <summary>
/// LiteDB-backed <see cref="IStateStore"/> — the storage backend chosen in the #119 ADR, adopted in
/// #121. All state lives in a single embedded database file (<c>state.db</c>) under the per-user app
/// data directory (<c>%APPDATA%\clickup-todo</c> on Windows, <c>~/.config/clickup-todo</c> elsewhere),
/// alongside the encrypted token file.
/// <para>
/// Each key maps to one document <c>{ _id: key, json: &lt;payload&gt; }</c> in the <c>state</c>
/// collection. The payload is serialised with <see cref="StateJson.Options"/> — the <b>same</b>
/// System.Text.Json contract the file backend uses — rather than LiteDB's BSON mapper, so a value is
/// byte-for-byte identical across backends and <c>ConfigMigrations</c> / enum handling behave exactly
/// as before. This makes the JSON and LiteDB stores drop-in interchangeable.
/// </para>
/// The connection is opened in shared mode so a stray second process cannot hard-lock the file, and
/// the database handle is held for the store's lifetime (settings writes are rare; the cache work in
/// #122+ reuses the open handle). Dispose at the composition root on exit.
/// </summary>
public sealed class LiteDbStateStore : IStateStore, IDisposable
{
    private const string CollectionName = "state";

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<StateDocument> _collection;

    /// <summary>The on-disk path of the LiteDB file (for messaging and tests).</summary>
    public string DatabasePath { get; }

    /// <param name="databasePath">
    /// The LiteDB file path. Defaults to <see cref="DefaultDatabasePath"/>. The containing directory
    /// is created if needed.
    /// </param>
    public LiteDbStateStore(string? databasePath = null)
    {
        DatabasePath = databasePath ?? DefaultDatabasePath();
        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Shared connection so a second process can't hard-lock the file (LiteDB serialises access).
        _db = new LiteDatabase(new ConnectionString { Filename = DatabasePath, Connection = ConnectionType.Shared });
        _collection = _db.GetCollection<StateDocument>(CollectionName);
    }

    /// <summary>The default database path: <c>state.db</c> in the shared per-user data directory.</summary>
    public static string DefaultDatabasePath()
        => Path.Combine(JsonFileStateStore.DefaultDirectory(), "state.db");

    public bool Exists(string key) => _collection.Exists(d => d.Id == key);

    public T? Load<T>(string key) where T : class
    {
        var doc = _collection.FindById(key);
        return doc?.Json is { } json ? JsonSerializer.Deserialize<T>(json, StateJson.Options) : null;
    }

    public void Save<T>(string key, T value) where T : class
        => _collection.Upsert(new StateDocument { Id = key, Json = JsonSerializer.Serialize(value, StateJson.Options) });

    public void Delete(string key) => _collection.Delete(key);

    public void Dispose() => _db.Dispose();

    /// <summary>The stored document shape: a key and its serialised JSON payload.</summary>
    private sealed class StateDocument
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string Json { get; set; } = string.Empty;
    }
}
