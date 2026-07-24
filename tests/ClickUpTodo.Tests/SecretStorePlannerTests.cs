using ClickUpTodo.Agent;
using ClickUpTodo.Configuration.Secrets;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure <see cref="SecretStorePlanner"/> backend-selection matrix (issue #306):
/// every OS × secret-CLI-present/absent combination, plus the security classification and hints.
/// </summary>
public sealed class SecretStorePlannerTests
{
    private static Func<string, bool> Present(params string[] names)
    {
        var set = new HashSet<string>(names, StringComparer.Ordinal);
        return set.Contains;
    }

    private static readonly Func<string, bool> None = _ => false;

    [Fact]
    public void Windows_AlwaysDpapi_RegardlessOfCli()
    {
        Assert.Equal(SecretBackendKind.Dpapi, SecretStorePlanner.Select(OSPlatformKind.Windows, None));
        Assert.Equal(SecretBackendKind.Dpapi, SecretStorePlanner.Select(OSPlatformKind.Windows, Present("secret-tool", "security")));
    }

    [Fact]
    public void MacOS_UsesKeychain_WhenSecurityCliPresent()
        => Assert.Equal(SecretBackendKind.Keychain, SecretStorePlanner.Select(OSPlatformKind.MacOS, Present("security")));

    [Fact]
    public void MacOS_FallsBackToPlaintext_WhenSecurityCliAbsent()
        => Assert.Equal(SecretBackendKind.Plaintext, SecretStorePlanner.Select(OSPlatformKind.MacOS, None));

    [Fact]
    public void Linux_UsesSecretService_WhenSecretToolPresent()
        => Assert.Equal(SecretBackendKind.SecretService, SecretStorePlanner.Select(OSPlatformKind.Linux, Present("secret-tool")));

    [Fact]
    public void Linux_FallsBackToPlaintext_WhenSecretToolAbsent()
        => Assert.Equal(SecretBackendKind.Plaintext, SecretStorePlanner.Select(OSPlatformKind.Linux, None));

    [Fact]
    public void Unknown_FallsBackToPlaintext()
        => Assert.Equal(SecretBackendKind.Plaintext, SecretStorePlanner.Select(OSPlatformKind.Unknown, Present("security", "secret-tool")));

    [Theory]
    [InlineData(SecretBackendKind.Dpapi, true)]
    [InlineData(SecretBackendKind.Keychain, true)]
    [InlineData(SecretBackendKind.SecretService, true)]
    [InlineData(SecretBackendKind.Plaintext, false)]
    public void IsSecure_TrueForEverythingButPlaintext(SecretBackendKind kind, bool expected)
        => Assert.Equal(expected, SecretStorePlanner.IsSecure(kind));

    [Fact]
    public void SecureStoreHint_MentionsSecretTool_OnLinux()
        => Assert.Contains("secret-tool", SecretStorePlanner.SecureStoreHint(OSPlatformKind.Linux));

    [Theory]
    [InlineData(OSPlatformKind.Windows)]
    [InlineData(OSPlatformKind.Unknown)]
    public void SecureStoreHint_Null_WhereThereIsNoActionableTip(OSPlatformKind os)
        => Assert.Null(SecretStorePlanner.SecureStoreHint(os));

    [Fact]
    public void Select_NullProbe_Throws()
        => Assert.Throws<ArgumentNullException>(() => SecretStorePlanner.Select(OSPlatformKind.Linux, null!));
}
