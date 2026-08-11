using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// The saved result of the multi-provider dispatch editor (#547): the edited provider list and the
/// chosen default provider name, ready to fold back onto <see cref="AgentDispatchSettings"/>. Null-free
/// and normalized (see <see cref="DispatchProviderListEditor.Build"/>).
/// </summary>
public sealed record DispatchProvidersResult(List<DispatchProvider> Providers, string DefaultProviderName);

/// <summary>
/// Pure logic core for the multi-provider dispatch editor (#547), factored out of the Terminal.Gui glue
/// so it is unit-testable (mirrors <see cref="SettingsForm"/> / <c>ChecklistArranger</c>): a mutable
/// working copy of the provider list plus the chosen default, with add / rename (dedup) / set-executable
/// / set-extra-args / delete / set-default operations and a normalizing <see cref="Build"/>.
/// <para>
/// The editor never presents an empty list: an empty seed (a hand-<c>new</c>'d settings object, or a
/// config with no providers) is filled with a single built-in <c>claude</c> default, mirroring
/// <see cref="AgentDispatchSettings.ResolveDefaultProvider"/>, so the screen always has a row to show and
/// <see cref="Delete"/> can re-seed rather than strand the user with nothing. Names are exact selector
/// keys — every comparison is <see cref="StringComparison.Ordinal"/>, matching
/// <see cref="AgentDispatchSettings.ResolveDefaultProvider"/> and the retired
/// <c>SettingsForm.ApplyDefaultProviderEdit</c> — and are kept unique so exactly one provider is the
/// default.
/// </para>
/// </summary>
public sealed class DispatchProviderListEditor
{
    private const string NewProviderBaseName = "New provider";
    private const string FallbackName = "Provider";

    private readonly List<DispatchProvider> _providers;
    private string _defaultName;

    /// <summary>
    /// Seeds the editor from an existing provider list + default name, working on deep copies so the
    /// caller's settings are untouched until <see cref="Build"/> is folded back. An empty
    /// <paramref name="providers"/> is seeded with the built-in <c>claude</c> default; the default name
    /// resolves to the matching provider, else the first (mirroring
    /// <see cref="AgentDispatchSettings.ResolveDefaultProvider"/>).
    /// </summary>
    public DispatchProviderListEditor(IReadOnlyList<DispatchProvider>? providers, string? defaultProviderName)
    {
        _providers = (providers ?? []).Select(Clone).ToList();
        if (_providers.Count == 0)
            _providers.Add(BuiltInDefault());

        _defaultName = _providers.Any(p => string.Equals(p.Name, defaultProviderName, StringComparison.Ordinal))
            ? defaultProviderName!
            : _providers[0].Name;
    }

    /// <summary>The working providers (live view; treat as read-only — mutate via the methods).</summary>
    public IReadOnlyList<DispatchProvider> Providers => _providers;

    /// <summary>The name of the provider currently chosen as default.</summary>
    public string DefaultProviderName => _defaultName;

    /// <summary>How many providers are in the working list (always ≥ 1).</summary>
    public int Count => _providers.Count;

    /// <summary>Whether the provider at <paramref name="index"/> is the chosen default (drives the row marker).</summary>
    public bool IsDefault(int index)
        => index >= 0 && index < _providers.Count
            && string.Equals(_providers[index].Name, _defaultName, StringComparison.Ordinal);

    /// <summary>
    /// Appends a new local-CLI provider with a unique <c>"New provider"</c> name and the built-in
    /// <c>claude</c> executable, and returns its index. The default is left unchanged.
    /// </summary>
    public int Add()
    {
        _providers.Add(new DispatchProvider
        {
            Name = UniqueName(NewProviderBaseName),
            Executable = AgentDispatchSettings.DefaultExecutable,
            ExtraArgs = [],
            Kind = DispatchProviderKind.LocalCli,
        });
        return _providers.Count - 1;
    }

    /// <summary>
    /// Renames the provider at <paramref name="index"/>: trims the input, falls back to
    /// <c>"Provider"</c> when blank, and deduplicates against the other providers with a
    /// <c>" (n)"</c> suffix. When the renamed provider was the default, the default name follows the
    /// rename so the chosen default is preserved. Out-of-range indices are a no-op.
    /// </summary>
    public void SetName(int index, string? name)
    {
        if (index < 0 || index >= _providers.Count)
            return;

        var desired = (name ?? "").Trim();
        if (desired.Length == 0)
            desired = FallbackName;

        var unique = UniqueName(desired, exceptIndex: index);
        var wasDefault = string.Equals(_providers[index].Name, _defaultName, StringComparison.Ordinal);
        _providers[index].Name = unique;
        if (wasDefault)
            _defaultName = unique;
    }

    /// <summary>
    /// Sets the executable of the provider at <paramref name="index"/> (trimmed). A blank value is kept
    /// as blank while editing and coalesced to <c>claude</c> by <see cref="Build"/>. Out-of-range is a no-op.
    /// </summary>
    public void SetExecutable(int index, string? executable)
    {
        if (index < 0 || index >= _providers.Count)
            return;
        _providers[index].Executable = (executable ?? "").Trim();
    }

    /// <summary>
    /// Sets the extra args of the provider at <paramref name="index"/>, trimming each and dropping blanks
    /// (matching <c>ToLauncherOptions</c>'s cleaning). Out-of-range is a no-op.
    /// </summary>
    public void SetExtraArgs(int index, IEnumerable<string>? args)
    {
        if (index < 0 || index >= _providers.Count)
            return;
        _providers[index].ExtraArgs = CleanArgs(args);
    }

    /// <summary>Chooses the provider at <paramref name="index"/> as the default. Out-of-range is a no-op.</summary>
    public void SetDefault(int index)
    {
        if (index < 0 || index >= _providers.Count)
            return;
        _defaultName = _providers[index].Name;
    }

    /// <summary>
    /// Deletes the provider at <paramref name="index"/>. When it was the last provider the list is
    /// re-seeded with the built-in <c>claude</c> default (the editor never goes empty). When the deleted
    /// provider was the chosen default, the default is reassigned to the first remaining provider.
    /// Out-of-range is a no-op. Returns the index to select afterwards (clamped into range).
    /// </summary>
    public int Delete(int index)
    {
        if (index < 0 || index >= _providers.Count)
            return Math.Clamp(index, 0, _providers.Count - 1);

        var wasDefault = string.Equals(_providers[index].Name, _defaultName, StringComparison.Ordinal);
        _providers.RemoveAt(index);

        if (_providers.Count == 0)
        {
            _providers.Add(BuiltInDefault());
            _defaultName = _providers[0].Name;
            return 0;
        }

        if (wasDefault)
            _defaultName = _providers[0].Name;

        return Math.Clamp(index, 0, _providers.Count - 1);
    }

    /// <summary>
    /// Produces the normalized, deep-copied <see cref="DispatchProvidersResult"/> to persist: each
    /// executable coalesced blank → <c>claude</c> and trimmed, each arg list cleaned, and the default
    /// name coerced to an existing provider (the first, if it somehow drifted). Isolated from later
    /// mutation of the editor.
    /// </summary>
    public DispatchProvidersResult Build()
    {
        var providers = _providers.Select(p => new DispatchProvider
        {
            Name = p.Name,
            Executable = string.IsNullOrWhiteSpace(p.Executable) ? AgentDispatchSettings.DefaultExecutable : p.Executable.Trim(),
            ExtraArgs = CleanArgs(p.ExtraArgs),
            Kind = p.Kind,
        }).ToList();

        var defaultName = providers.Any(p => string.Equals(p.Name, _defaultName, StringComparison.Ordinal))
            ? _defaultName
            : providers[0].Name;

        return new DispatchProvidersResult(providers, defaultName);
    }

    /// <summary>A unique name from <paramref name="desired"/>, appending <c>" (n)"</c> (n≥2) until it
    /// collides with no other provider (the one at <paramref name="exceptIndex"/> is ignored, so renaming
    /// a provider to its own name is a no-op rather than gaining a suffix).</summary>
    private string UniqueName(string desired, int exceptIndex = -1)
    {
        bool Taken(string candidate) => _providers
            .Where((_, i) => i != exceptIndex)
            .Any(p => string.Equals(p.Name, candidate, StringComparison.Ordinal));

        if (!Taken(desired))
            return desired;

        for (var n = 2; ; n++)
        {
            var candidate = $"{desired} ({n})";
            if (!Taken(candidate))
                return candidate;
        }
    }

    private static List<string> CleanArgs(IEnumerable<string>? args)
        => args is null ? [] : [.. args.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())];

    private static DispatchProvider BuiltInDefault() => new()
    {
        Name = AgentDispatchSettings.DefaultProviderDisplayName,
        Executable = AgentDispatchSettings.DefaultExecutable,
        ExtraArgs = [],
        Kind = DispatchProviderKind.LocalCli,
    };

    private static DispatchProvider Clone(DispatchProvider p) => new()
    {
        Name = p.Name,
        Executable = p.Executable,
        ExtraArgs = [.. p.ExtraArgs],
        Kind = p.Kind,
    };
}
