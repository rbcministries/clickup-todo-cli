namespace ClickUpTodo.Configuration;

/// <summary>
/// User overrides for the two app-wide <b>launch</b> gestures (#506, split-pane epic #502): the
/// <see cref="NewTab"/> chord (default <c>Ctrl+Enter</c>, <c>OpenInNewTab</c>) and the
/// <see cref="SplitPane"/> chord (default <c>Ctrl+Alt+Enter</c>, <c>OpenInSplitPane</c>), persisted in
/// <c>config.json</c> under <c>launchChords</c>. This is the store behind
/// <see cref="Tui.Screens.LaunchChordOverrides"/> — the override layer over the central
/// <see cref="Tui.Screens.Keybindings"/> table; only these two gestures are rebindable (a general
/// keybinding editor is a separate decision, #506 non-goal).
/// <para>
/// Both are <b>nullable</b>: <c>null</c>/blank (the default, and an absent <c>launchChords</c> key) means
/// "use the shipped default", so the feature is zero-config and existing configs need no migration. A
/// non-null value is the parseable key token (what <c>Key.TryParse</c> accepts, e.g. <c>"Alt+Enter"</c>).
/// It is <em>validated at read time</em> — an invalid token (unparseable, a bare type-ahead letter, or one
/// colliding with another binding) is dropped back to the default by
/// <see cref="Tui.Screens.LaunchChordOverrides.FromConfig"/> rather than crashing the app (the #506
/// load-time-defense requirement), and rejected at save time by the same rule in
/// <see cref="Tui.Screens.SettingsForm.ValidateLaunchChord"/>.
/// </para>
/// The primary use is the one #502 documents: a user who unbinds Windows Terminal's
/// <c>Terminal.ToggleFullscreen</c> on <c>Alt+Enter</c> can point the split-pane gesture there.
/// </summary>
public sealed class LaunchChordSettings
{
    /// <summary>Override for the new-terminal-tab gesture (<c>OpenInNewTab</c>); <c>null</c> ⇒ the shipped
    /// <c>Ctrl+Enter</c> default.</summary>
    public string? NewTab { get; set; }

    /// <summary>Override for the split-pane gesture (<c>OpenInSplitPane</c>); <c>null</c> ⇒ the shipped
    /// <c>Ctrl+Alt+Enter</c> default.</summary>
    public string? SplitPane { get; set; }

    /// <summary>True when neither gesture is overridden (both at their shipped defaults).</summary>
    public bool IsDefault => string.IsNullOrWhiteSpace(NewTab) && string.IsNullOrWhiteSpace(SplitPane);
}
