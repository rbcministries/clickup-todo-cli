namespace ClickUpTodo.Services;

/// <summary>
/// A persisted cache payload plus the moment it was captured (#124). The <see cref="CapturedAt"/>
/// lets a caller surface a staleness marker ("cached from 3m ago · refreshing…") on the instant paint
/// while the live refresh runs. Returned by <see cref="TaskCache.LoadSnapshot"/> /
/// <see cref="FeedCache.LoadSnapshot"/>; the plain <c>Load</c> overloads unwrap it to just the items.
/// </summary>
/// <typeparam name="T">The cached element type (a task or a feed comment).</typeparam>
/// <param name="Items">The cached payload. May be empty (an empty set was genuinely cached).</param>
/// <param name="CapturedAt">When the payload was persisted (UTC).</param>
public sealed record CachedSnapshot<T>(IReadOnlyList<T> Items, DateTimeOffset CapturedAt);
