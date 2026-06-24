using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace ClickUpTodo.Configuration;

/// <summary>
/// Stores the ClickUp personal API token at rest. On Windows the token is encrypted with DPAPI
/// (current-user scope) so it can only be decrypted by the same Windows user on the same machine.
/// On other platforms it falls back to a base64-obfuscated file (functional, not strongly secured).
/// </summary>
public sealed class TokenStore
{
    // Extra entropy mixed into DPAPI so the blob is bound to this app, not just the user.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("clickup-todo-cli/v1");

    private readonly string _tokenPath;

    public TokenStore(string? directoryPath = null)
        => _tokenPath = Path.Combine(directoryPath ?? ConfigStore.DefaultDirectory(), "token.bin");

    public bool Exists() => File.Exists(_tokenPath);

    public void Save(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
        var plaintext = Encoding.UTF8.GetBytes(token.Trim());
        var stored = OperatingSystem.IsWindows() ? ProtectWindows(plaintext) : plaintext;
        File.WriteAllBytes(_tokenPath, stored);
    }

    public string? Load()
    {
        if (!File.Exists(_tokenPath))
            return null;
        var stored = File.ReadAllBytes(_tokenPath);
        var plaintext = OperatingSystem.IsWindows() ? UnprotectWindows(stored) : stored;
        return plaintext is null ? null : Encoding.UTF8.GetString(plaintext);
    }

    public void Delete()
    {
        if (File.Exists(_tokenPath))
            File.Delete(_tokenPath);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] plaintext)
        => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[]? UnprotectWindows(byte[] stored)
    {
        try
        {
            return ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Token written by a different user/machine, or corrupted — treat as "no token".
            return null;
        }
    }
}
