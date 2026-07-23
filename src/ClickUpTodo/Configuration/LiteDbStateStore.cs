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
/// The connection is opened in shared mode so a stray second process cannot hard-lock the file (LiteDB
/// serialises access and opens/closes the underlying file per operation in this mode). The
/// <see cref="LiteDatabase"/> object is held for the store's lifetime — settings writes are rare, and
/// the cache work in #122+ reuses this store rather than reopening the database. Dispose at the
/// composition root on exit.
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
        try
        {
            _collection = _db.GetCollection<StateDocument>(CollectionName);
        }
        catch
        {
            // Don't leak the open database file/lock if collection setup fails after the DB opened.
            _db.Dispose();
            throw;
        }
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

    /// <summary>
    /// Builds the cross-process change-marker channel (#294) over this store's <b>own</b> shared
    /// <c>state.db</c> connection, so producer nudges and the key→document state ride the same file and
    /// its cross-process mutex. Exposed as a factory (rather than leaking the <see cref="LiteDatabase"/>)
    /// so the connection stays encapsulated and single-owned here.
    /// </summary>
    /// <param name="instanceId">This process's id, stamped on every marker (#295).</param>
    /// <param name="options">Table bounds; defaults to <see cref="ChangeMarkerOptions.Default"/>.</param>
    /// <param name="timeProvider">Clock for TTL aging; defaults to <see cref="TimeProvider.System"/>.</param>
    public IChangeMarkerStore CreateChangeMarkerStore(
        string instanceId, ChangeMarkerOptions? options = null, TimeProvider? timeProvider = null)
        => new LiteDbChangeMarkerStore(_db, instanceId, options, timeProvider);

    public void Dispose() => _db.Dispose();

    /// <summary>The stored document shape: a key and its serialised JSON payload.</summary>
    private sealed class StateDocument
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string Json { get; set; } = string.Empty;
    }
}
