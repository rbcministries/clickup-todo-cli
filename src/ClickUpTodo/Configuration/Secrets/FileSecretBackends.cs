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
        WriteOwnerOnly(FilePath, Encode(Encoding.UTF8.GetBytes(secret)));
        return true;
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/> so that, on POSIX, only the owning
    /// user can read or write it (mode <c>0600</c>) — this hardens the disclosed plaintext fallback
    /// (#382) against other local users, and is harmless defense-in-depth on the DPAPI-encrypted file.
    /// The restrictive mode is applied <b>at creation</b> so there is never a moment where a fresh
    /// <c>token.bin</c> is world-readable, and re-applied after the write so a file left behind by an
    /// older build (created with the default umask) is tightened on the next save too. On Windows the
    /// file inherits the user-profile directory's ACL and Unix modes don't apply, so it's a plain write.
    /// </summary>
    private static void WriteOwnerOnly(string path, byte[] bytes)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, bytes);
            return;
        }

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        // Tighten a pre-existing file *before* writing the new secret into it: FileMode.Create truncates
        // the existing inode without changing its mode, so a file left looser by an older build would
        // otherwise hold the fresh token at that looser mode until the post-write chmod. UnixCreateMode
        // only applies when the file is *created*, so it can't cover this case — do it explicitly first.
        if (File.Exists(path))
            File.SetUnixFileMode(path, ownerOnly);

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = ownerOnly, // a freshly created file is 0600 from the start — no world-readable window
        };
        using (var stream = new FileStream(path, options))
            stream.Write(bytes);

        // Belt-and-braces (e.g. the file appeared between the Exists check and the open).
        File.SetUnixFileMode(path, ownerOnly);
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
