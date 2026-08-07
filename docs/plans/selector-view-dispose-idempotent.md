# Make `SelectorView.Dispose` idempotent (#483)

`SelectorView.Dispose(bool)` cancels and disposes its debounce
`CancellationTokenSource` with no re-entrancy guard
(`src/ClickUpTodo/Tui/SelectorView.cs`):

```csharp
protected override void Dispose(bool disposing)
{
    if (disposing)
    {
        _cts.Cancel();      // throws ObjectDisposedException if already disposed
        DisposeTimer();
        _cts.Dispose();
    }
    base.Dispose(disposing);
}
```

`CancellationTokenSource.Cancel()` throws `ObjectDisposedException` once the CTS
is disposed, so **any** caller that disposes a `SelectorView` twice — e.g. an
explicit `Dispose()` followed by a still-attached parent view re-disposing the
child through `base.Dispose(disposing)` — throws during teardown. This bit #472
concretely (a screen torn down with the mention-picker overlay still attached
double-disposed the picker); that call site was fixed there by detaching first,
but the base-class fragility remains for any future `SelectorView` consumer.

## Fix

The standard idempotent-dispose pattern — a `bool _disposed` guard so the
managed cleanup runs exactly once and a second `Dispose(true)` is a no-op:

```csharp
private bool _disposed;
protected override void Dispose(bool disposing)
{
    if (disposing && !_disposed)
    {
        _disposed = true;
        _cts.Cancel();
        DisposeTimer();
        _cts.Dispose();
    }
    base.Dispose(disposing);
}
```

No behaviour change on the single-dispose path (timer + CTS released exactly
once, as before); the only observable difference is that a second dispose no
longer throws.

## Test

`SelectorViewDisposeTests` (xUnit, `tests/ClickUpTodo.Tests/`):

- `Dispose_Twice_DoesNotThrow` — construct a real `SelectorView` and dispose it
  twice; the second call must not throw. This is the direct regression for the
  acceptance criterion. Verified test-first: it reproduced the exact
  `ObjectDisposedException` from `_cts.Cancel()` before the guard was added, and
  passes after.
- `Dispose_Once_DoesNotThrow` — the single-dispose path stays clean.

### Why this test may instantiate the view (and the rest of the suite doesn't)

CLAUDE.md keeps Terminal.Gui **UI** out of the CI unit suite because
rendering, keypress handling and driver behaviour aren't CI-testable, and the
suite never calls `Application.Init`. This test constructs a `SelectorView` but
touches **none** of that surface: construction and `Dispose` need no driver,
paint nothing and process no input — they exercise only the managed-resource
teardown (`CancellationTokenSource` + debounce timer), which is exactly what the
fix changes. Empirically the view constructs and disposes headlessly (no
`Application.Init`), so the test is deterministic and driver-free. The issue's
acceptance criteria explicitly allowed either gating a view-touching test
appropriately or covering the guard another way; this is the former, scoped to
the managed lifecycle only.

## Scope

- `src/ClickUpTodo/Tui/SelectorView.cs` — the guard.
- `tests/ClickUpTodo.Tests/SelectorViewDisposeTests.cs` — the regression tests.

No API/spec change, no generated-client regen, no TUI rendering change (single
sectioned `ListView` model untouched). Low risk, defence-in-depth.
