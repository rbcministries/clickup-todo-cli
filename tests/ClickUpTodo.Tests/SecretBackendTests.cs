using System.Runtime.InteropServices;
using ClickUpTodo.Configuration.Secrets;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the individual <see cref="ISecretBackend"/> implementations (issue #306). The CLI
/// backends are exercised through <see cref="FakeSecretCli"/> — a stateful stand-in that parses the
/// same argv/stdin as the real tools — so both the command contract and the round-trip are covered.
/// </summary>
public sealed class SecretBackendTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));
    private string TokenPath => Path.Combine(_dir, "token.bin");

    private const string Service = "clickup-todo-cli";
    private const string Account = "personal-token";
    private const string Label = "ClickUp Simple CLI token";
    private const string Token = "pk_12345_ABCDEFGHIJKLMNOP";

    // ── Keychain (macOS `security`) ───────────────────────────────────────────

    [Fact]
    public void Keychain_SaveThenLoad_RoundTrips_AndUsesUpsertFlags()
    {
        var cli = new FakeSecretCli();
        var backend = new KeychainSecretBackend(cli, Service, Account);

        Assert.True(backend.TrySave(Token));
        Assert.Equal(Token, backend.Load());
        Assert.True(backend.Exists());
        Assert.True(backend.IsSecure);

        var add = cli.Calls.First(c => c.Args[0] == "add-generic-password");
        Assert.Equal(SecretStorePlanner.MacKeychainCli, add.File);
        Assert.Contains("-U", add.Args);            // upsert
        Assert.Contains(Service, add.Args);
        Assert.Contains(Account, add.Args);
        Assert.Contains(Token, add.Args);           // secret via -w
    }

    [Fact]
    public void Keychain_Load_WhenAbsent_ReturnsNull()
    {
        var backend = new KeychainSecretBackend(new FakeSecretCli(), Service, Account);
        Assert.Null(backend.Load());
        Assert.False(backend.Exists());
    }

    [Fact]
    public void Keychain_Load_StripsTrailingNewlineTheCliAppends()
    {
        var cli = new FakeSecretCli();
        var backend = new KeychainSecretBackend(cli, Service, Account);
        backend.TrySave(Token);

        // The fake mirrors the real CLI, which appends "\n" to the printed password.
        Assert.Equal(Token, backend.Load());
    }

    [Fact]
    public void Keychain_Delete_RemovesItem()
    {
        var cli = new FakeSecretCli();
        var backend = new KeychainSecretBackend(cli, Service, Account);
        backend.TrySave(Token);

        backend.Delete();

        Assert.Null(backend.Load());
        Assert.Equal(0, cli.StoredCount);
    }

    [Fact]
    public void Keychain_TrySave_FalseWhenStoreUnavailable()
    {
        var cli = new FakeSecretCli { Unavailable = true };
        var backend = new KeychainSecretBackend(cli, Service, Account);
        Assert.False(backend.TrySave(Token));
    }

    // ── Secret Service (Linux `secret-tool`) ──────────────────────────────────

    [Fact]
    public void SecretService_SaveThenLoad_RoundTrips_AndFeedsSecretOverStdin()
    {
        var cli = new FakeSecretCli();
        var backend = new SecretServiceSecretBackend(cli, Service, Account, Label);

        Assert.True(backend.TrySave(Token));
        Assert.Equal(Token, backend.Load());
        Assert.True(backend.IsSecure);

        var store = cli.Calls.First(c => c.Args[0] == "store");
        Assert.Equal(SecretStorePlanner.LinuxSecretCli, store.File);
        Assert.Equal(Token, store.Stdin);                // secret on stdin, never argv…
        Assert.DoesNotContain(Token, store.Args);        // …so it can't leak to the process list
        Assert.Contains(Service, store.Args);
        Assert.Contains(Account, store.Args);
    }

    [Fact]
    public void SecretService_Load_WhenAbsent_ReturnsNull()
    {
        var backend = new SecretServiceSecretBackend(new FakeSecretCli(), Service, Account, Label);
        Assert.Null(backend.Load());
        Assert.False(backend.Exists());
    }

    [Fact]
    public void SecretService_Clear_RemovesItem()
    {
        var cli = new FakeSecretCli();
        var backend = new SecretServiceSecretBackend(cli, Service, Account, Label);
        backend.TrySave(Token);

        backend.Delete();

        Assert.Null(backend.Load());
        Assert.Contains(cli.Calls, c => c.Args[0] == "clear");
    }

    [Fact]
    public void SecretService_TrySave_FalseWhenStoreUnavailable()
    {
        var cli = new FakeSecretCli { Unavailable = true };
        var backend = new SecretServiceSecretBackend(cli, Service, Account, Label);
        Assert.False(backend.TrySave(Token));
    }

    // ── Plaintext file fallback ───────────────────────────────────────────────

    [Fact]
    public void Plaintext_RoundTrips_AndReportsInsecure()
    {
        var backend = new PlaintextFileSecretBackend(TokenPath);

        Assert.True(backend.TrySave(Token));
        Assert.True(backend.Exists());
        Assert.Equal(Token, backend.Load());
        Assert.False(backend.IsSecure);
        Assert.Contains("UNENCRYPTED", backend.Description);
    }

    [Fact]
    public void Plaintext_WritesTheRawTokenBytes()
    {
        var backend = new PlaintextFileSecretBackend(TokenPath);
        backend.TrySave(Token);

        // The whole point of #306: on the fallback the token really is cleartext on disk.
        Assert.Equal(Token, File.ReadAllText(TokenPath));
    }

    [Fact]
    public void Plaintext_Delete_RemovesFile()
    {
        var backend = new PlaintextFileSecretBackend(TokenPath);
        backend.TrySave(Token);

        backend.Delete();

        Assert.False(backend.Exists());
        Assert.Null(backend.Load());
    }

    [Fact]
    public void Plaintext_Load_WhenNoFile_ReturnsNull()
        => Assert.Null(new PlaintextFileSecretBackend(TokenPath).Load());

    // ── DPAPI (Windows only) ──────────────────────────────────────────────────

    [SkippableFact]
    public void Dpapi_RoundTrips_OnWindows()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "DPAPI is Windows-only.");
        var backend = new DpapiFileSecretBackend(TokenPath);

        Assert.True(backend.TrySave(Token));
        Assert.Equal(Token, backend.Load());
        Assert.True(backend.IsSecure);
        // Encrypted at rest: the bytes on disk are not the plaintext token.
        Assert.NotEqual(Token, File.ReadAllText(TokenPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
