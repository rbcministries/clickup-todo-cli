using System.Runtime.InteropServices;
using ClickUpTodo.Agent;
using ClickUpTodo.Configuration.Secrets;

namespace ClickUpTodo.Configuration;

/// <summary>
/// Stores the ClickUp personal API token at rest, using the best backend for the host OS: the macOS
/// Keychain (<c>security</c>), the Linux Secret Service (<c>secret-tool</c>/libsecret), or Windows
/// DPAPI (current-user scope). When no OS secret store is available (headless/SSH, minimal containers)
/// it falls back to a <b>plaintext file</b> — a fallback the first-run wizard discloses in plain words.
/// <para>
/// A pre-existing plaintext <c>token.bin</c> (written by an older build on macOS/Linux) is migrated
/// into the secure store on first <see cref="Load"/> and the cleartext file is removed, so upgrading
/// closes the at-rest gap without a re-login.
/// </para>
/// Backend selection is delegated to the pure <see cref="SecretStorePlanner"/>; the OS, PATH probe and
/// command runner are injectable so the whole thing is unit-testable without an OS secret store.
/// </summary>
public sealed class TokenStore
{
    // Keychain / Secret Service coordinates. The token is a single per-user item; a custom config dir
    // shares it (the OS stores are per-user, not per-directory) — acceptable for this single-account app.
    private const string Service = "clickup-todo-cli";
    private const string Account = "personal-token";
    private const string Label = "ClickUp Simple CLI token";

    private readonly OSPlatformKind _os;
    private readonly ISecretBackend _primary;
    private readonly PlaintextFileSecretBackend _plaintextFallback;
    private readonly FileSecretBackend _legacyFile;

    // Reflects where the token actually lives after the most recent Save/Load, so IsSecure /
    // StorageDescription disclose reality even when a runtime failure forced the plaintext fallback.
    private ISecretBackend _current;

    public TokenStore(
        string? directoryPath = null,
        OSPlatformKind? os = null,
        Func<string, bool>? exists = null,
        ICommandRunner? runner = null)
    {
        var tokenPath = Path.Combine(directoryPath ?? ConfigStore.DefaultDirectory(), "token.bin");
        _os = os ?? SecretStorePlanner.CurrentOS();
        var onPath = exists ?? ExecutableOnPath;
        var cli = runner ?? new ProcessCommandRunner();
        var kind = SecretStorePlanner.Select(_os, onPath);

        _plaintextFallback = new PlaintextFileSecretBackend(tokenPath);
        // The legacy on-disk format matches the OS's file backend: DPAPI on Windows, plaintext elsewhere.
        _legacyFile = _os == OSPlatformKind.Windows ? new DpapiFileSecretBackend(tokenPath) : _plaintextFallback;
        _primary = kind switch
        {
            SecretBackendKind.Dpapi => new DpapiFileSecretBackend(tokenPath),
            SecretBackendKind.Keychain => new KeychainSecretBackend(cli, Service, Account),
            SecretBackendKind.SecretService => new SecretServiceSecretBackend(cli, Service, Account, Label),
            _ => _plaintextFallback,
        };
        _current = _primary;
    }

    /// <summary>Whether the token is currently protected at rest (OS store / DPAPI) vs. plaintext on disk.</summary>
    public bool IsSecure => _current.IsSecure;

    /// <summary>A short human description of where/how the token is stored, for first-run disclosure.</summary>
    public string StorageDescription => _current.Description;

    /// <summary>An install hint for reaching the secure path when running on the plaintext fallback, else null.</summary>
    public string? InsecureStorageHint => IsSecure ? null : SecretStorePlanner.SecureStoreHint(_os);

    public bool Exists() => Load() is not null;

    public void Save(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var trimmed = token.Trim();

        if (_primary.TrySave(trimmed))
        {
            _current = _primary;
            // Once the secure store holds the token, don't leave a pre-existing cleartext copy behind.
            if (_primary is not FileSecretBackend && _legacyFile.Exists())
                _legacyFile.Delete();
            return;
        }

        // Secure store unavailable at runtime (e.g. no session bus) — degrade to the disclosed plaintext file.
        _plaintextFallback.TrySave(trimmed);
        _current = _plaintextFallback;
    }

    public string? Load()
    {
        var fromPrimary = _primary.Load();
        if (fromPrimary is not null)
        {
            _current = _primary;
            if (_primary is not FileSecretBackend && _legacyFile.Exists())
                _legacyFile.Delete();
            return fromPrimary;
        }

        // Secure store empty/unavailable, but a legacy token.bin exists → migrate it into the secure store.
        if (_primary is not FileSecretBackend && _legacyFile.Exists())
        {
            var legacy = _legacyFile.Load();
            if (legacy is not null)
            {
                if (_primary.TrySave(legacy))
                {
                    _legacyFile.Delete();
                    _current = _primary;
                }
                else
                {
                    // Couldn't reach the secure store — keep serving the file token, disclosed as insecure.
                    _current = _plaintextFallback;
                }
                return legacy;
            }
        }

        _current = _primary;
        return null;
    }

    public void Delete()
    {
        _primary.Delete();
        // Remove any legacy/fallback plaintext file too, so --reset leaves no token on disk.
        _plaintextFallback.Delete();
        _current = _primary;
    }

    /// <summary>True if <paramref name="name"/> resolves to an executable on the current PATH.</summary>
    private static bool ExecutableOnPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.Contains('/') || name.Contains('\\'))
            return File.Exists(name);

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return false;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (File.Exists(Path.Combine(dir, name)))
                return true;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".com" })
                {
                    if (File.Exists(Path.Combine(dir, name + ext)))
                        return true;
                }
            }
        }
        return false;
    }
}
