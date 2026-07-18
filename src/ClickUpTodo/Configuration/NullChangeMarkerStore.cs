namespace ClickUpTodo.Configuration;

/// <summary>
/// A no-op <see cref="IChangeMarkerStore"/> — the safe default when the nudge channel isn't available
/// (the file-backed <see cref="JsonFileStateStore"/> has no cross-process channel) or is disabled, so a
/// caller can always <see cref="Record"/> unconditionally without a null check. Records nothing and
/// always reads back empty.
/// </summary>
public sealed class NullChangeMarkerStore : IChangeMarkerStore
{
    /// <summary>The shared instance.</summary>
    public static readonly NullChangeMarkerStore Instance = new();

    private NullChangeMarkerStore() { }

    /// <inheritdoc/>
    public string InstanceId => string.Empty;

    /// <inheritdoc/>
    public void Record(string taskId, long? serverDateUpdatedMs, IReadOnlyList<string> changedFields) { }

    /// <inheritdoc/>
    public IReadOnlyList<ChangeMarker> ReadAll() => [];
}
