using System.Runtime.InteropServices;
using ClickUpTodo.Agent;

namespace ClickUpTodo.Configuration.Secrets;

/// <summary>Which storage backend <see cref="TokenStore"/> should use for the token at rest.</summary>
public enum SecretBackendKind
{
    /// <summary>Windows DPAPI (current-user scope), encrypted file at <c>token.bin</c>.</summary>
    Dpapi,

    /// <summary>macOS login Keychain, via the <c>security</c> CLI.</summary>
    Keychain,

    /// <summary>Linux Secret Service (GNOME Keyring / KWallet), via <c>secret-tool</c> (libsecret).</summary>
    SecretService,

    /// <summary>Plaintext file at <c>token.bin</c> — the disclosed fallback when no secret store is available.</summary>
    Plaintext,
}

/// <summary>
/// Pure, I/O-free chooser for the token-storage backend. Given the host OS and a way to probe whether
/// a CLI is on <c>PATH</c>, it picks the OS secret store when its CLI is present and degrades to the
/// plaintext fallback otherwise (headless/SSH, minimal containers). Mirrors <c>BrowserLaunchPlanner</c>:
/// all environment lookups are injected so the decision matrix is unit-tested without touching the OS.
/// </summary>
public static class SecretStorePlanner
{
    /// <summary>The <c>security</c> subcommand exe used on macOS.</summary>
    public const string MacKeychainCli = "security";

    /// <summary>The libsecret CLI used on Linux.</summary>
    public const string LinuxSecretCli = "secret-tool";

    public static SecretBackendKind Select(OSPlatformKind os, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        return os switch
        {
            OSPlatformKind.Windows => SecretBackendKind.Dpapi,
            OSPlatformKind.MacOS => exists(MacKeychainCli) ? SecretBackendKind.Keychain : SecretBackendKind.Plaintext,
            OSPlatformKind.Linux => exists(LinuxSecretCli) ? SecretBackendKind.SecretService : SecretBackendKind.Plaintext,
            _ => SecretBackendKind.Plaintext,
        };
    }

    /// <summary>Whether <paramref name="kind"/> protects the secret at rest (vs. the plaintext fallback).</summary>
    public static bool IsSecure(SecretBackendKind kind) => kind != SecretBackendKind.Plaintext;

    /// <summary>Resolves the host OS family from the runtime (mirrors the launchers' resolution).</summary>
    public static OSPlatformKind CurrentOS()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return OSPlatformKind.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return OSPlatformKind.MacOS;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return OSPlatformKind.Linux;
        return OSPlatformKind.Unknown;
    }

    /// <summary>A short install hint for getting the secure path on the plaintext fallback, or <see langword="null"/>.</summary>
    public static string? SecureStoreHint(OSPlatformKind os) => os switch
    {
        OSPlatformKind.Linux => "install libsecret (provides 'secret-tool') and run a Secret Service (e.g. gnome-keyring)",
        OSPlatformKind.MacOS => "the 'security' CLI ships with macOS — this usually means a non-standard PATH",
        _ => null,
    };
}
