using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClickUpTodo.Configuration;

/// <summary>A user-registered ClickUp OAuth app's <c>client_id</c> / <c>client_secret</c> pair.</summary>
public sealed record OAuthAppCredentials(string ClientId, string ClientSecret);

/// <summary>
/// Resolves the <b>user-supplied</b> ClickUp OAuth app credentials — never anything shipped in the
/// repo. Each user registers their own ClickUp OAuth app (option 1 in issue #1) and provides:
/// <list type="number">
///   <item>environment variables <c>CLICKUP_OAUTH_CLIENT_ID</c> / <c>CLICKUP_OAUTH_CLIENT_SECRET</c>
///   (checked first), or</item>
///   <item>a local, <b>gitignored</b> <c>oauth-app.json</c> in the config directory:
///   <c>{ "clientId": "…", "clientSecret": "…" }</c>.</item>
/// </list>
/// Returns <see langword="null"/> when neither source yields a complete pair, so callers can fall
/// back to the personal-token path.
/// </summary>
public sealed class OAuthAppCredentialStore
{
    public const string ClientIdEnvVar = "CLICKUP_OAUTH_CLIENT_ID";
    public const string ClientSecretEnvVar = "CLICKUP_OAUTH_CLIENT_SECRET";
    public const string FileName = "oauth-app.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly Func<string, string?> _readEnv;

    public OAuthAppCredentialStore(string? directoryPath = null, Func<string, string?>? readEnvironmentVariable = null)
    {
        _filePath = Path.Combine(directoryPath ?? ConfigStore.DefaultDirectory(), FileName);
        _readEnv = readEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>The resolved <c>oauth-app.json</c> path (for messaging / tests).</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// Resolves credentials from env vars (both must be present) then <c>oauth-app.json</c>, or
    /// <see langword="null"/> when neither provides a complete pair.
    /// </summary>
    public OAuthAppCredentials? Load()
    {
        var envId = _readEnv(ClientIdEnvVar);
        var envSecret = _readEnv(ClientSecretEnvVar);
        if (!string.IsNullOrWhiteSpace(envId) && !string.IsNullOrWhiteSpace(envSecret))
            return new OAuthAppCredentials(envId.Trim(), envSecret.Trim());

        if (!File.Exists(_filePath))
            return null;

        try
        {
            var dto = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_filePath), JsonOptions);
            if (dto is null || string.IsNullOrWhiteSpace(dto.ClientId) || string.IsNullOrWhiteSpace(dto.ClientSecret))
                return null;
            return new OAuthAppCredentials(dto.ClientId.Trim(), dto.ClientSecret.Trim());
        }
        catch (JsonException)
        {
            // Malformed file — treat as "no credentials" rather than crashing sign-in.
            return null;
        }
    }

    /// <summary>Writes an <c>oauth-app.json</c> template/credentials file (used by tooling and tests).</summary>
    public void Save(OAuthAppCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var json = JsonSerializer.Serialize(new FileModel(credentials.ClientId, credentials.ClientSecret), JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    private sealed record FileModel(
        [property: JsonPropertyName("clientId")] string? ClientId,
        [property: JsonPropertyName("clientSecret")] string? ClientSecret);
}
