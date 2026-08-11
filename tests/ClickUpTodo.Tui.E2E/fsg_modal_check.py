#!/usr/bin/env python3
"""#554 focusable-form native-modal A/B: Filter·Sort·Group (F3) hosted as the hand-mounted _screens
FilterSortGroupScreen (A) vs. as a native Terminal.Gui Dialog on a nested Application.Run loop
(B, CLICKUP_TODO_NATIVE_MODAL=1).

The focusable-form follow-up to native_modal_spike_check.py (#404), which measured only a
non-focusable surface (F1 Help). This one measures the two things Help could not — the axes the
#402 form-modal migration decision actually hinges on:

  1. F3 open→paint latency     — F3 until the form is visible ("Add a filter"), A vs B.
  2. Intra-modal key→paint     — typing a character into the value TextField *inside* the modal
                                 (the #3 focusable-input invariant), A vs B. This is THE new number.
  3. Result-marshalling        — add a filter + Save marshals a ViewSettings back to the host (the
                                 status line flashes a view summary); Esc discards it (no summary,
                                 the added filter does not persist). Help returned nothing; F3 does.
  4. Modal-stacking            — F1 over the open F3 modal opens Help, Esc returns to the F3 modal.
  5. Outer responsiveness      — after a full open/marshal/close cycle a Down still redraws the list
                                 promptly (the #3 single-ListView latency invariant), A vs B.

Both legs render the identical form (shared FilterSortGroupFormBuilder); leg B additionally carries
NativeModalSpike.TitleMarker in the dialog title, so the check can prove B actually took the native
path (a silently no-op'd flag would render the identical _screens form and pass the same assertions).

The check PASSES on functional correctness for both hosts; latency/byte numbers are printed for the
findings doc with a generous ceiling so it is a stable guard, not a flaky micro-benchmark.

Self-contained; sets its own env. Run:
  DLL=tests/ClickUpTodo.Tui.E2E/bin/Release/net10.0/ClickUpTodo.Tui.E2E.dll
  timeout 200 python3 -u tests/ClickUpTodo.Tui.E2E/fsg_modal_check.py $DLL
"""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

F1 = b"\x1bOP"
F3 = b"\x1bOR"
ESC = b"\x1b"
TAB = b"\t"
DOWN = b"\x1b[B"
ENTER = b"\r"
SPACE = b" "

FORM_MARKER = "Add a filter"          # a label in the FSG form, in BOTH hosts
ACTIVE_MARKER = "Active filters"      # the active-filter list header, in BOTH hosts
HELP_MARKER = "Keyboard shortcuts"    # the Help title, in BOTH hosts (used for modal-stacking)
NATIVE_TITLE_MARKER = "[native modal spike]"  # ONLY in leg B's native Dialog title
LIST_MARKER = "next section"          # a footer/list-only string present iff back on the list
FILTER_VALUE = "zq9"                  # a distinctive value typed into the modal's TextField
LATENCY_CEILING_MS = 600              # absolute sanity ceiling (manual run, not CI)


class App:
    def __init__(self, **extra_env):
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet", **extra_env)
        self.proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                                     env=env, close_fds=True, preexec_fn=os.setsid)
        os.close(slave)

    def answer(self, data):
        if b"\x1b[18t" in data:
            os.write(self.master, b"\x1b[8;%d;%dt" % (ROWS, COLS))
        if b"\x1b[6n" in data:
            os.write(self.master, b"\x1b[1;1R")
        if b"\x1b[0c" in data or b"\x1b[c" in data:
            os.write(self.master, b"\x1b[?62;22c")

    def pump(self, seconds):
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            r, _, _ = select.select([self.master], [], [], 0.05)
            if r:
                try:
                    chunk = os.read(self.master, 65536)
                except OSError:
                    break
                if not chunk:
                    break
                self.answer(chunk)
                self.stream.feed(chunk)

    def visible(self):
        return "\n".join(line.rstrip() for line in self.screen.display).rstrip()

    def boot(self, seconds=30):
        t0 = time.monotonic()
        while time.monotonic() - t0 < seconds:
            self.pump(0.5)
            if "Task 1" in self.visible():
                return True
        return False

    def send_measured(self, seq, marker, quiet=0.6, timeout=8.0):
        """Send seq; return (latency_ms_to_marker_or_None, bytes_emitted, visible_after)."""
        if isinstance(seq, str):
            seq = seq.encode()
        os.write(self.master, seq)
        t0 = time.monotonic()
        total = 0
        latency = None
        last = time.monotonic()
        mbytes = marker.encode()
        tail = b""
        while True:
            now = time.monotonic()
            if now - t0 > timeout:
                break
            if now - last > quiet and total > 0:
                break
            r, _, _ = select.select([self.master], [], [], 0.05)
            if not r:
                continue
            try:
                chunk = os.read(self.master, 65536)
            except OSError:
                break
            if not chunk:
                break
            self.answer(chunk)
            self.stream.feed(chunk)
            total += len(chunk)
            last = time.monotonic()
            if latency is None:
                tail = (tail + chunk)[-4096:]
                if mbytes in tail:
                    latency = (last - t0) * 1000.0
        return latency, total, self.visible()

    def send(self, seq, wait=0.8):
        if isinstance(seq, str):
            seq = seq.encode()
        os.write(self.master, seq)
        self.pump(wait)
        return self.visible()

    def alive(self):
        return self.proc.poll() is None

    def kill(self):
        try:
            os.killpg(os.getpgid(self.proc.pid), signal.SIGKILL)
        except Exception:
            pass


def open_form(app, label, native):
    """F3 → assert the form opened and the intended host rendered. Returns open latency (ms)."""
    open_ms, _, v = app.send_measured(F3, FORM_MARKER)
    assert FORM_MARKER in v, f"{label}: F3 did not open the form ({FORM_MARKER!r} missing):\n{v[:2000]}"
    assert ACTIVE_MARKER in v, f"{label}: form body missing ({ACTIVE_MARKER!r}):\n{v[:2000]}"
    if native:
        assert NATIVE_TITLE_MARKER in v, \
            f"{label}: native flag set but the native-Dialog title marker is missing " \
            f"(flag not honored — leg fell back to _screens?):\n{v[:2000]}"
    else:
        assert NATIVE_TITLE_MARKER not in v, \
            f"{label}: the native-Dialog title marker leaked into the _screens leg:\n{v[:2000]}"
    assert open_ms is not None, f"{label}: never saw the form paint marker after F3"
    assert open_ms <= LATENCY_CEILING_MS, \
        f"{label}: F3 open latency {open_ms:.0f}ms exceeds {LATENCY_CEILING_MS}ms ceiling"
    return open_ms


def focus_value_field(app):
    """Tab from the initial field-list focus to the value TextField (fieldList -> opList -> valueField)."""
    app.send(TAB, 0.4)
    app.send(TAB, 0.4)


def measure_intramodal_type(app, label):
    """With the form open, focus the value TextField and type into it, measuring key→paint of the
    echoed character — the #3 focusable-input invariant *inside* the modal, the number #404 could not
    take. Does not add/persist a filter (no Enter), then Esc closes the form leaving the view unchanged.
    Returns the single-key latency (ms)."""
    focus_value_field(app)
    # Prime the field, then measure the incremental paint of one more character.
    app.send(FILTER_VALUE[:-1], 0.4)              # "zq"
    key_ms, _, v = app.send_measured(FILTER_VALUE[-1:], FILTER_VALUE)   # "9" -> "zq9" visible
    assert FILTER_VALUE in v, \
        f"{label}: typing did not echo {FILTER_VALUE!r} in the modal's value field " \
        f"(focus never reached the TextField?):\n{v[:2000]}"
    assert key_ms is not None, f"{label}: never saw the typed char paint inside the modal"
    assert key_ms <= LATENCY_CEILING_MS, \
        f"{label}: intra-modal key→paint {key_ms:.0f}ms exceeds {LATENCY_CEILING_MS}ms ceiling"
    v = app.send(ESC, 1.0)                          # discard — the typed value was never added
    assert FORM_MARKER not in v, f"{label}: Esc did not close the form after typing:\n{v[:2000]}"
    assert LIST_MARKER in v, f"{label}: task list not restored after the typing leg:\n{v[:2000]}"
    return key_ms


def marshal_cancel(app, label):
    """Open the form, add a filter, Esc — the change is discarded (form gone, list restored, no filter
    persists). Proves the null-result path (Cancel marshals nothing)."""
    open_form(app, label + "/cancel", app.native)
    focus_value_field(app)
    app.send("zzz9", 0.4)
    app.send(ENTER, 0.5)                            # add a throwaway filter (in-form only)
    v = app.send(ESC, 1.0)
    assert app.alive(), f"{label}: process died cancelling the modal:\n{v[:2000]}"
    assert FORM_MARKER not in v, f"{label}: Esc did not close the modal:\n{v[:2000]}"
    assert LIST_MARKER in v, f"{label}: task list not restored after Esc:\n{v[:2000]}"


def marshal_save(app, label):
    """Open the form, add a filter, reach Save and activate it — asserting the host applied the
    marshalled ViewSettings (status-line 'View' summary) and the modal closed. Adding 'Status IS zq9'
    matches no task, so the applied filter also visibly empties the list — an unambiguous, persistent
    proof the result reached the host (vs Cancel's no-op). Kept LAST because it empties the list."""
    open_form(app, label + "/save", app.native)
    focus_value_field(app)
    app.send(FILTER_VALUE, 0.4)
    v = app.send(ENTER, 0.6)                        # add "Status IS zq9"
    assert FILTER_VALUE in v, f"{label}: Enter did not add the filter ({FILTER_VALUE!r} not listed):\n{v[:2000]}"
    # After AddFilter focus returns to the value field; Tab to the Save button (valueField -> addButton
    # -> removeButton -> filtersList -> sortList -> dirButton -> groupList -> save = 7 Tabs), then Space.
    for _ in range(7):
        app.send(TAB, 0.25)
    v = app.send(SPACE, 1.0)
    assert app.alive(), f"{label}: process died saving the modal (dispose bug #346?):\n{v[:2000]}"
    assert FORM_MARKER not in v, f"{label}: Save did not close the modal:\n{v[:2000]}"
    assert "View" in v, f"{label}: Save did not flash a view summary (result not marshalled to host):\n{v[:2000]}"
    assert LIST_MARKER in v, f"{label}: task list not restored after Save:\n{v[:2000]}"


def modal_stacking(app, label, native):
    """F1 over the open F3 modal opens Help; Esc returns to the F3 modal (not the list)."""
    open_form(app, label + "/stack", native)
    v = app.send(F1, 1.0)
    assert HELP_MARKER in v, f"{label}: F1 did not open Help over the modal:\n{v[:2000]}"
    v = app.send(ESC, 1.0)
    assert HELP_MARKER not in v, f"{label}: Esc did not close Help:\n{v[:2000]}"
    assert FORM_MARKER in v, f"{label}: Esc from Help did not return to the F3 modal:\n{v[:2000]}"
    app.send(ESC, 1.0)                              # close the modal back to the list


def run_leg(label, native, **env):
    app = App(E2E_TASKS="200", E2E_REFRESH="600", **env)
    app.native = native
    try:
        assert app.boot(), f"{label}: app did not boot (Task 1 never rendered):\n{app.visible()[:1500]}"

        # 1-2. Open + intra-modal typing (types into the value field, then Esc — no filter persisted).
        open_ms = open_form(app, label, native)
        key_ms = measure_intramodal_type(app, label)
        print(f"  [{label}] open {open_ms:.0f}ms · intra-modal key {key_ms:.0f}ms")

        # 3. Cancel discards — open, add a throwaway filter, Esc (list stays default/non-empty).
        marshal_cancel(app, label)
        print(f"  [{label}] cancel discarded")

        # 4. Modal-stacking — F1 over F3, Esc back to the modal, Esc to the list.
        modal_stacking(app, label, native)
        print(f"  [{label}] modal-stacking ok (F1 over F3)")
        assert app.alive(), f"{label}: process died after the modal cycles"

        # 5. Outer responsiveness — list nav stays snappy after the cycles (#3 invariant). Measured on
        #    the still-default (non-empty) list, BEFORE the list-emptying Save below.
        navs = []
        for _ in range(5):
            nav_ms, _, _ = app.send_measured(DOWN, "Task", timeout=5.0)
            if nav_ms is not None:
                navs.append(nav_ms)
        assert app.alive(), f"{label}: process died navigating after the modal cycles"
        assert navs, f"{label}: no list redraw after Down following the modal cycles"
        nav_median = sorted(navs)[len(navs) // 2]
        assert nav_median <= LATENCY_CEILING_MS, \
            f"{label}: post-modal list-nav median {nav_median:.0f}ms exceeds {LATENCY_CEILING_MS}ms ceiling"

        # 6. Result-marshalling — Save applies (kept LAST: the applied filter empties the list).
        marshal_save(app, label)
        print(f"  [{label}] save marshalled + applied")

        return {"open_ms": open_ms, "key_ms": key_ms, "nav_ms": nav_median}
    finally:
        app.kill()


apps_ok = True
try:
    print("── Leg A: hand-mounted _screens FilterSortGroupScreen (production default) ──")
    a = run_leg("A/_screens", native=False)

    print("── Leg B: native Terminal.Gui Dialog on a nested Application.Run (CLICKUP_TODO_NATIVE_MODAL=1) ──")
    b = run_leg("B/native", native=True, CLICKUP_TODO_NATIVE_MODAL="1")

    # The #3-invariant guard: the native nested-run modal must not leave list nav meaningfully slower
    # than the _screens baseline, and — the new axis — intra-modal typing must not degrade either.
    nav_budget = max(a["nav_ms"] * 2.5, a["nav_ms"] + 150)
    assert b["nav_ms"] <= nav_budget, (
        f"post-modal list-nav regressed: native {b['nav_ms']:.0f}ms vs _screens {a['nav_ms']:.0f}ms "
        f"(budget {nav_budget:.0f}ms) — a nested run-loop is degrading the #3 latency invariant")
    key_budget = max(a["key_ms"] * 2.5, a["key_ms"] + 150)
    assert b["key_ms"] <= key_budget, (
        f"intra-modal key→paint regressed: native {b['key_ms']:.0f}ms vs _screens {a['key_ms']:.0f}ms "
        f"(budget {key_budget:.0f}ms) — typing inside the native modal is slower than the _screens form")

    print("\n===== #554 focusable native-modal A/B grid (Filter·Sort·Group) =====")
    print(f"{'metric':<28}{'A _screens':>14}{'B native':>14}")
    print(f"{'F3 open→paint (ms)':<28}{a['open_ms']:>14.0f}{b['open_ms']:>14.0f}")
    print(f"{'intra-modal key→paint (ms)':<28}{a['key_ms']:>14.0f}{b['key_ms']:>14.0f}")
    print(f"{'post-modal list-nav (ms)':<28}{a['nav_ms']:>14.0f}{b['nav_ms']:>14.0f}")
    print(f"\nok — leg B proved native (title marker); both hosts open/type/marshal(save+cancel)/stack/"
          f"close cleanly (alive); intra-modal key {b['key_ms']:.0f}ms within budget {key_budget:.0f}ms "
          f"and post-modal nav {b['nav_ms']:.0f}ms within budget {nav_budget:.0f}ms of _screens")
except AssertionError as e:
    apps_ok = False
    print("FAIL:", e)

raise SystemExit(0 if apps_ok else 1)
