namespace ClickUpTodo.Configuration;

/// <summary>
/// Well-known keys for state persisted through <see cref="IStateStore"/>. Each key names one logical
/// document; the file backend maps it to <c>{key}.json</c>, a collection backend to a collection.
/// Cache-layer issues (#122 tasks, #123 feed, #125 statuses/colors) add their own keys here.
/// </summary>
public static class StateKeys
{
    /// <summary>
    /// The app's settings document — <see cref="AppConfig"/>, including the focus pins
    /// (<see cref="AppConfig.PinnedTaskIds"/>). Maps to <c>config.json</c> in the file backend.
    /// </summary>
    public const string Config = "config";
}
