using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace ClickUpTodo.Configuration.Secrets;

/// <summary>
/// Reads/writes the raw token bytes at a <c>token.bin</c> path. Shared plumbing for the two file-backed
/// backends (DPAPI-encrypted on Windows, plaintext elsewhere); the only difference is the transform
/// applied to the bytes before they hit disk.
/// </summary>
public abstract class FileSecretBackend : ISecretBackend
{
    protected FileSecretBackend(string filePath) => FilePath = filePath;

    /// <summary>The on-disk <c>token.bin</c> path.</summary>
    public string FilePath { get; }

    public abstract bool IsSecure { get; }

    public abstract string Description { get; }

    public bool Exists() => File.Exists(FilePath);

    public string? Load()
    {
        if (!File.Exists(FilePath))
            return null;
        var stored = File.ReadAllBytes(FilePath);
        var plaintext = Decode(stored);
        return plaintext is null ? null : Encoding.UTF8.GetString(plaintext);
    }

    public bool TrySave(string secret)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, Encode(Encoding.UTF8.GetBytes(secret)));
        return true;
    }

    public void Delete()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }

    /// <summary>Transforms the UTF-8 token bytes into what is written to disk.</summary>
    protected abstract byte[] Encode(byte[] plaintext);

    /// <summary>Reverses <see cref="Encode"/>; <see langword="null"/> when the stored bytes can't be read back.</summary>
    protected abstract byte[]? Decode(byte[] stored);
}

/// <summary>
/// Windows DPAPI (current-user scope) file backend: the token is encrypted so it can only be decrypted
/// by the same Windows user on the same machine. This is the unchanged Windows behaviour, now expressed
/// as an <see cref="ISecretBackend"/>.
/// </summary>
public sealed class DpapiFileSecretBackend : FileSecretBackend
{
    // Extra entropy mixed into DPAPI so the blob is bound to this app, not just the user.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("clickup-todo-cli/v1");

    public DpapiFileSecretBackend(string filePath) : base(filePath) { }

    public override bool IsSecure => true;

    public override string Description => $"encrypted with Windows DPAPI at {FilePath}";

    [SupportedOSPlatform("windows")]
    protected override byte[] Encode(byte[] plaintext)
        => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    protected override byte[]? Decode(byte[] stored)
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

/// <summary>
/// Plaintext file backend — the token bytes are written verbatim. This is the <b>disclosed fallback</b>
/// used when no OS secret store is available (headless/SSH, minimal containers). It provides no
/// at-rest protection; callers surface that to the user (see <see cref="TokenStore"/> / the setup wizard).
/// </summary>
public sealed class PlaintextFileSecretBackend : FileSecretBackend
{
    public PlaintextFileSecretBackend(string filePath) : base(filePath) { }

    public override bool IsSecure => false;

    public override string Description => $"UNENCRYPTED in a plaintext file at {FilePath}";

    protected override byte[] Encode(byte[] plaintext) => plaintext;

    protected override byte[]? Decode(byte[] stored) => stored;
}
