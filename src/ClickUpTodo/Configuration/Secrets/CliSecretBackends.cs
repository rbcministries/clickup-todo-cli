namespace ClickUpTodo.Configuration.Secrets;

/// <summary>
/// macOS Keychain backend over the built-in <c>security</c> CLI. The token is stored as a generic
/// password keyed by (service, account); <c>-U</c> upserts. Reads strip the trailing newline the CLI
/// appends. A <c>find</c>/<c>delete</c> exit code of 44 means "no such item" (not an error).
/// </summary>
public sealed class KeychainSecretBackend : ISecretBackend
{
    private const int ItemNotFound = 44;

    private readonly ICommandRunner _runner;
    private readonly string _service;
    private readonly string _account;

    public KeychainSecretBackend(ICommandRunner runner, string service, string account)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _service = service;
        _account = account;
    }

    public bool IsSecure => true;

    public string Description => "stored in the macOS Keychain";

    public bool Exists() => Load() is not null;

    public string? Load()
    {
        var result = _runner.Run(SecretStorePlanner.MacKeychainCli,
            ["find-generic-password", "-a", _account, "-s", _service, "-w"]);
        if (result is null || result.ExitCode != 0)
            return null;
        var value = result.StdOut.TrimEnd('\r', '\n');
        return value.Length == 0 ? null : value;
    }

    public bool TrySave(string secret)
    {
        // `security add-generic-password` has no non-interactive stdin path, so the secret must go on
        // argv (`-w <secret>`) — a transient same-user/root exposure in the process list. It's the only
        // option on macOS and doesn't affect the at-rest goal (the value lands encrypted in the Keychain).
        var result = _runner.Run(SecretStorePlanner.MacKeychainCli,
            ["add-generic-password", "-a", _account, "-s", _service, "-w", secret, "-U"]);
        return result is not null && result.ExitCode == 0;
    }

    public void Delete()
    {
        var result = _runner.Run(SecretStorePlanner.MacKeychainCli,
            ["delete-generic-password", "-a", _account, "-s", _service]);
        // Success or "no such item" (44) both leave nothing stored — the desired end state.
        _ = result is null || result.ExitCode == 0 || result.ExitCode == ItemNotFound;
    }
}

/// <summary>
/// Linux Secret Service backend over <c>secret-tool</c> (libsecret; GNOME Keyring / KWallet). The
/// secret is written over <b>stdin</b> (never argv). Stored keyed by two attributes (service, account).
/// <c>lookup</c> exits non-zero / prints nothing when absent; the token is always stored trimmed, so a
/// trailing newline on read is stripped for parity with the Keychain backend.
/// </summary>
public sealed class SecretServiceSecretBackend : ISecretBackend
{
    private readonly ICommandRunner _runner;
    private readonly string _service;
    private readonly string _account;
    private readonly string _label;

    public SecretServiceSecretBackend(ICommandRunner runner, string service, string account, string label)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _service = service;
        _account = account;
        _label = label;
    }

    public bool IsSecure => true;

    public string Description => "stored in the Linux Secret Service (libsecret)";

    public bool Exists() => Load() is not null;

    public string? Load()
    {
        var result = _runner.Run(SecretStorePlanner.LinuxSecretCli,
            ["lookup", "service", _service, "account", _account]);
        if (result is null || result.ExitCode != 0)
            return null;
        var value = result.StdOut.TrimEnd('\r', '\n');
        return value.Length == 0 ? null : value;
    }

    public bool TrySave(string secret)
    {
        var result = _runner.Run(SecretStorePlanner.LinuxSecretCli,
            ["store", "--label", _label, "service", _service, "account", _account],
            stdin: secret);
        return result is not null && result.ExitCode == 0;
    }

    public void Delete()
    {
        // `clear` removes every item matching the attributes; a missing item is not an error.
        _ = _runner.Run(SecretStorePlanner.LinuxSecretCli,
            ["clear", "service", _service, "account", _account]);
    }
}
