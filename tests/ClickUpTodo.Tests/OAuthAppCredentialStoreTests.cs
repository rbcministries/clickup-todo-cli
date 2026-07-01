using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

public sealed class OAuthAppCredentialStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    /// <summary>An environment reader that knows nothing (simulates unset env vars).</summary>
    private static string? NoEnv(string _) => null;

    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
        => key => pairs.FirstOrDefault(p => p.Key == key).Value;

    [Fact]
    public void Load_ReturnsNull_WhenNoSource()
    {
        var store = new OAuthAppCredentialStore(_dir, NoEnv);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_PrefersEnvVars_OverFile()
    {
        new OAuthAppCredentialStore(_dir, NoEnv).Save(new OAuthAppCredentials("file_id", "file_secret"));
        var store = new OAuthAppCredentialStore(_dir, Env(
            (OAuthAppCredentialStore.ClientIdEnvVar, "env_id"),
            (OAuthAppCredentialStore.ClientSecretEnvVar, "env_secret")));

        var creds = store.Load();

        Assert.NotNull(creds);
        Assert.Equal("env_id", creds!.ClientId);
        Assert.Equal("env_secret", creds.ClientSecret);
    }

    [Fact]
    public void Load_TrimsEnvValues()
    {
        var store = new OAuthAppCredentialStore(_dir, Env(
            (OAuthAppCredentialStore.ClientIdEnvVar, "  env_id  "),
            (OAuthAppCredentialStore.ClientSecretEnvVar, "  env_secret  ")));

        var creds = store.Load();

        Assert.Equal("env_id", creds!.ClientId);
        Assert.Equal("env_secret", creds.ClientSecret);
    }

    [Fact]
    public void Load_ReturnsNull_WhenOnlyOneEnvVarSet_AndNoFile()
    {
        var store = new OAuthAppCredentialStore(_dir, Env(
            (OAuthAppCredentialStore.ClientIdEnvVar, "env_id")));

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_FallsBackToFile_WhenEnvAbsent()
    {
        new OAuthAppCredentialStore(_dir, NoEnv).Save(new OAuthAppCredentials("file_id", "file_secret"));
        var store = new OAuthAppCredentialStore(_dir, NoEnv);

        var creds = store.Load();

        Assert.NotNull(creds);
        Assert.Equal("file_id", creds!.ClientId);
        Assert.Equal("file_secret", creds.ClientSecret);
    }

    [Fact]
    public void Load_ReturnsNull_WhenFileMissingAField()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, OAuthAppCredentialStore.FileName), """{ "clientId": "only_id" }""");
        var store = new OAuthAppCredentialStore(_dir, NoEnv);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_ReturnsNull_WhenFileMalformed()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, OAuthAppCredentialStore.FileName), "not json {");
        var store = new OAuthAppCredentialStore(_dir, NoEnv);

        Assert.Null(store.Load());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsFile()
    {
        var store = new OAuthAppCredentialStore(_dir, NoEnv);

        store.Save(new OAuthAppCredentials("id_1", "secret_1"));
        var creds = store.Load();

        Assert.Equal("id_1", creds!.ClientId);
        Assert.Equal("secret_1", creds.ClientSecret);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
