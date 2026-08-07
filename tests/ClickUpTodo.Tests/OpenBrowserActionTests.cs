using ClickUpTodo.Configuration;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure Ctrl+B open-in-browser decision (#518) — the one place the
/// close-vs-stay setting and the "a root view never exits" invariant compose. The Terminal.Gui host
/// wiring that launches the browser and (in close mode) tears the view down is verified by build +
/// reasoning + tui-validate per CLAUDE.md.
/// </summary>
public sealed class OpenBrowserActionTests
{
    // ── the invariant: a root view never closes, under either setting ─────────

    [Theory]
    [InlineData(OpenBrowserBehavior.KeepOpen)]
    [InlineData(OpenBrowserBehavior.CloseView)]
    public void RootView_NeverCloses_WhateverTheSetting(OpenBrowserBehavior setting)
        => Assert.False(OpenBrowserAction.ShouldCloseView(setting, isRoot: true));

    // ── the setting governs the non-root case ─────────────────────────────────

    [Fact]
    public void NonRoot_KeepOpen_StaysOpen()
        => Assert.False(OpenBrowserAction.ShouldCloseView(OpenBrowserBehavior.KeepOpen, isRoot: false));

    [Fact]
    public void NonRoot_CloseView_Closes()
        => Assert.True(OpenBrowserAction.ShouldCloseView(OpenBrowserBehavior.CloseView, isRoot: false));

    // ── the only path that closes is non-root + CloseView ─────────────────────

    [Theory]
    [InlineData(OpenBrowserBehavior.KeepOpen, false, false)]
    [InlineData(OpenBrowserBehavior.KeepOpen, true, false)]
    [InlineData(OpenBrowserBehavior.CloseView, true, false)]
    [InlineData(OpenBrowserBehavior.CloseView, false, true)]
    public void ShouldCloseView_TruthTable(OpenBrowserBehavior setting, bool isRoot, bool expected)
        => Assert.Equal(expected, OpenBrowserAction.ShouldCloseView(setting, isRoot));
}
