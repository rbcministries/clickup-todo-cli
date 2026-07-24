namespace ClickUpTodo.Configuration.Secrets;

/// <summary>
/// One concrete way to store the ClickUp token at rest — the OS secret store (macOS Keychain, Linux
/// Secret Service), Windows DPAPI, or a plaintext file fallback. <see cref="TokenStore"/> selects one
/// per OS and delegates to it, so the rest of the app never sees the platform difference.
/// </summary>
public interface ISecretBackend
{
    /// <summary>Whether this backend protects the secret at rest (OS store / DPAPI) vs. plaintext on disk.</summary>
    bool IsSecure { get; }

    /// <summary>A short human description of where/how the secret is stored, for first-run disclosure.</summary>
    string Description { get; }

    /// <summary>Whether a secret is currently stored.</summary>
    bool Exists();

    /// <summary>The stored secret, or <see langword="null"/> when none is stored or the backend is unavailable.</summary>
    string? Load();

    /// <summary>Persists <paramref name="secret"/>. Returns <see langword="false"/> when the store was unavailable.</summary>
    bool TrySave(string secret);

    /// <summary>Removes any stored secret. A no-op when nothing is stored.</summary>
    void Delete();
}
