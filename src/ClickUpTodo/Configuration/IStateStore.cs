namespace ClickUpTodo.Configuration;

/// <summary>
/// The single persistence seam for the app's on-disk state — settings, focus pins (which ride in
/// the settings document today), and, in future, cached payloads (tasks, feed, statuses/colors —
/// Epic #118). Call sites persist through this interface instead of touching the on-disk format
/// directly, so the backend can change (the #119 verdict is LiteDB) without churning them.
/// <para>
/// It is deliberately a small, document-oriented surface — a named <c>key</c> maps to one
/// serialised value — because that is what both a file backend (<see cref="JsonFileStateStore"/>,
/// key ⇒ <c>{key}.json</c>) and a collection backend (LiteDB, key ⇒ collection/document) satisfy
/// cleanly. Well-known keys live in <see cref="StateKeys"/>.
/// </para>
/// Token storage (<see cref="TokenStore"/>) stays separate and is not routed through here.
/// <para>
/// Implementations are <b>not</b> required to be thread-safe: today's only writer is the
/// single-threaded config save. When cache payloads (#122/#123/#125) arrive and are written from a
/// background refresh thread, that caller must serialise concurrent access to a key (last-writer-wins
/// / partial-read is otherwise possible), or a thread-safe implementation must be introduced.
/// </para>
/// </summary>
public interface IStateStore
{
    /// <summary>Whether a value has been persisted under <paramref name="key"/>.</summary>
    bool Exists(string key);

    /// <summary>Load the value stored under <paramref name="key"/>, or <see langword="null"/> when absent.</summary>
    T? Load<T>(string key) where T : class;

    /// <summary>Persist <paramref name="value"/> under <paramref name="key"/>, replacing any prior value.</summary>
    void Save<T>(string key, T value) where T : class;

    /// <summary>Remove any value stored under <paramref name="key"/>. A no-op when nothing is stored.</summary>
    void Delete(string key);
}
