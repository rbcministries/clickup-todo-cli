using System.Runtime.InteropServices;
using ClickUpTodo.Configuration.Secrets;

namespace ClickUpTodo.Tests;

/// <summary>
/// Integration tests that hit the <b>real</b> OS secret store via <see cref="ProcessCommandRunner"/>
/// (macOS Keychain / Linux Secret Service). They self-skip (SkippableFact) unless
/// <c>CLICKUP_SECRET_ITEST=1</c> is set <i>and</i> the platform's CLI is on PATH — so CI (and any
/// developer without a session keyring) stays green, and a locked/headless keyring never blocks a run.
///
/// <para>They use a throwaway, test-only service/account (never the app's real
/// <c>clickup-todo-cli/personal-token</c> item) and clean up in a <c>finally</c>, so running them can't
/// disturb a real saved token.</para>
/// </summary>
public sealed class SecretStoreIntegrationTests
{
    private static bool OptedIn => Environment.GetEnvironmentVariable("CLICKUP_SECRET_ITEST") == "1";

    private static bool OnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return false;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (File.Exists(Path.Combine(dir, exe)))
                return true;
        }
        return false;
    }

    private static ISecretBackend? RealBackendForThisOS(string service, string account)
    {
        var runner = new ProcessCommandRunner();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && OnPath(SecretStorePlanner.MacKeychainCli))
            return new KeychainSecretBackend(runner, service, account);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && OnPath(SecretStorePlanner.LinuxSecretCli))
            return new SecretServiceSecretBackend(runner, service, account, "clickup-todo itest");
        return null;
    }

    [SkippableFact]
    public void RealSecretStore_RoundTripsAndDeletes()
    {
        Skip.IfNot(OptedIn, "Set CLICKUP_SECRET_ITEST=1 (with a working keyring) to run the real secret-store test.");

        var service = "clickup-todo-cli-itest";
        var account = "itest-" + Guid.NewGuid().ToString("N");
        var backend = RealBackendForThisOS(service, account);
        Skip.If(backend is null, "No OS secret-store CLI on PATH for this platform.");

        const string token = "pk_itest_ABCDEFGHIJKLMNOP";
        try
        {
            Assert.True(backend!.TrySave(token), "TrySave should succeed against a working secret store.");
            Assert.Equal(token, backend.Load());
            Assert.True(backend.Exists());
        }
        finally
        {
            backend!.Delete();
        }

        Assert.Null(backend.Load());
    }
}
