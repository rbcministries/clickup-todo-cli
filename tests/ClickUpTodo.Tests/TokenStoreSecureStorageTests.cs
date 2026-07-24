using System.Text;
using ClickUpTodo.Agent;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Tests for the <see cref="TokenStore"/> facade's OS-secret-store wiring (issue #306): backend
/// selection, migration of a legacy plaintext <c>token.bin</c> into the secure store, the disclosed
/// plaintext fallback when the store is unavailable, and delete/reset parity — all driven through the
/// injectable OS/PATH-probe/runner seams, no real OS store touched.
/// </summary>
public sealed class TokenStoreSecureStorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));
    private string TokenPath => Path.Combine(_dir, "token.bin");
    private const string Token = "pk_secure_ABCDEFGHIJKLMNOP";

    private static readonly Func<string, bool> CliPresent = _ => true;
    private static readonly Func<string, bool> CliAbsent = _ => false;

    private void WriteLegacyPlaintext(string token)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(TokenPath, Encoding.UTF8.GetBytes(token));
    }

    [Fact]
    public void Linux_WithSecretTool_StoresSecurely_NotOnDisk()
    {
        var cli = new FakeSecretCli();
        var store = new TokenStore(_dir, OSPlatformKind.Linux, CliPresent, cli);

        store.Save(Token);

        Assert.True(store.IsSecure);
        Assert.Equal(Token, store.Load());
        Assert.False(File.Exists(TokenPath));           // nothing written to disk
        Assert.Equal(1, cli.StoredCount);
        Assert.Contains("Secret Service", store.StorageDescription);
    }

    [Fact]
    public void MacOS_WithSecurityCli_StoresInKeychain()
    {
        var cli = new FakeSecretCli();
        var store = new TokenStore(_dir, OSPlatformKind.MacOS, CliPresent, cli);

        store.Save(Token);

        Assert.True(store.IsSecure);
        Assert.Equal(Token, store.Load());
        Assert.Contains("Keychain", store.StorageDescription);
    }

    [Fact]
    public void MigratesLegacyPlaintextFile_IntoSecureStore_ThenDeletesIt()
    {
        WriteLegacyPlaintext(Token);
        var cli = new FakeSecretCli();
        var store = new TokenStore(_dir, OSPlatformKind.Linux, CliPresent, cli);

        var loaded = store.Load();

        Assert.Equal(Token, loaded);
        Assert.Equal(1, cli.StoredCount);               // migrated into the secure store
        Assert.False(File.Exists(TokenPath));           // cleartext file removed
        Assert.True(store.IsSecure);
    }

    [Fact]
    public void Save_PurgesAnyLingeringLegacyPlaintextFile()
    {
        WriteLegacyPlaintext("pk_old_leftover");
        var cli = new FakeSecretCli();
        var store = new TokenStore(_dir, OSPlatformKind.Linux, CliPresent, cli);

        store.Save(Token);

        Assert.False(File.Exists(TokenPath));
        Assert.Equal(Token, store.Load());
    }

    [Fact]
    public void SecureStoreUnavailable_FallsBackToDisclosedPlaintextFile()
    {
        var cli = new FakeSecretCli { Unavailable = true };
        var store = new TokenStore(_dir, OSPlatformKind.Linux, CliPresent, cli);

        store.Save(Token);

        Assert.False(store.IsSecure);
        Assert.True(File.Exists(TokenPath));
        Assert.Equal(Token, File.ReadAllText(TokenPath));
        Assert.Equal(Token, store.Load());
        Assert.Contains("UNENCRYPTED", store.StorageDescription);
        Assert.NotNull(store.InsecureStorageHint);
    }

    [Fact]
    public void NoSecretCli_UsesPlaintextFallback()
    {
        var store = new TokenStore(_dir, OSPlatformKind.Linux, CliAbsent);

        store.Save(Token);

        Assert.False(store.IsSecure);
        Assert.True(File.Exists(TokenPath));
        Assert.Equal(Token, store.Load());
    }

    [Fact]
    public void Delete_ClearsSecureStoreAndAnyFile()
    {
        WriteLegacyPlaintext("pk_stray_file");   // a stray cleartext file alongside the secure item
        var cli = new FakeSecretCli();
        var store = new TokenStore(_dir, OSPlatformKind.Linux, CliPresent, cli);
        store.Save(Token);

        store.Delete();

        Assert.Equal(0, cli.StoredCount);
        Assert.False(File.Exists(TokenPath));
        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_TrimsWhitespaceBeforeStoring()
    {
        var cli = new FakeSecretCli();
        var store = new TokenStore(_dir, OSPlatformKind.Linux, CliPresent, cli);

        store.Save("  pk_padded_secure  ");

        Assert.Equal("pk_padded_secure", store.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
