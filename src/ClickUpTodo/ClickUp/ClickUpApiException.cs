namespace ClickUpTodo.ClickUp;

/// <summary>
/// Wraps a failed ClickUp API call (a Kiota <c>ApiException</c>) in an app-specific type so callers
/// don't depend on Kiota internals.
/// </summary>
public sealed class ClickUpApiException(int statusCode, string operation, Exception inner)
    : Exception($"ClickUp API operation '{operation}' failed with HTTP {statusCode}.", inner)
{
    public int StatusCode { get; } = statusCode;

    /// <summary>True when the token was rejected (bad/expired personal token).</summary>
    public bool IsAuthFailure => StatusCode is 401 or 403;
}
