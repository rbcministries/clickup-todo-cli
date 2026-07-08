namespace ClickUpTodo.Configuration;

/// <summary>
/// Which authentication scheme the saved token in <see cref="TokenStore"/> should be sent with.
/// Persisted in <c>config.json</c> (as a string) so startup constructs the matching Kiota auth
/// provider. Defaults to <see cref="PersonalToken"/> so existing configs — and any config missing
/// the field — keep driving the raw-header personal-token path unchanged.
/// </summary>
public enum AuthMode
{
    /// <summary>A ClickUp personal API token, sent as a raw <c>Authorization</c> header (no scheme).</summary>
    PersonalToken,

    /// <summary>A ClickUp OAuth access token, sent as <c>Authorization: Bearer &lt;token&gt;</c>.</summary>
    OAuth,
}
