#!/usr/bin/env python3
"""#618 quick-open native-modal A/B: the Ctrl+O quick-open surface hosted as the hand-mounted _screens
QuickOpenScreen (A) vs. as a native Terminal.Gui Dialog on a nested Application.Run loop
(B, CLICKUP_TODO_NATIVE_MODAL=1) — the #402 transient-modal migration pilot, the focusable-form sibling
of fsg_modal_check.py (#554).

Both legs render the identical form (shared QuickOpenFormBuilder); leg B additionally carries
NativeModalSpike.TitleMarker in the dialog title, so the check can prove B actually took the native path
(a silently no-op'd flag would render the identical _screens form and pass the same assertions).

Measures / asserts, per host:

  1. Ctrl+O open→paint latency   — Ctrl+O until the form is visible ("Open a task" + prompt), A vs B.
  2. Intra-modal key→paint       — typing a character into the input TextField *inside* the modal
                                    (the #3 focusable-input invariant), A vs B.
  3. OpenHere marshal + navigate — a cached id + Enter marshals OpenHere and navigates to Task Detail.
                                   THIS is the native leg's headline: the nested run returns after
                                   teardown, so the resolve runs with NO AddTimeout(1ms) deferral and the
                                   detail still mounts ("Description" appears, not a stuck "Loading…").
  4. Cancel marshals nothing     — Esc on the surface closes it with no navigation (back on the list).
  5. Modal-stacking              — F1 over the open surface opens Help; Esc returns to the surface.
  6. Outer responsiveness        — after a full open/marshal/close cycle a Down still redraws the list.

The check PASSES on functional correctness for both hosts; latency/byte numbers are printed for the
findings doc with a generous ceiling so it is a stable guard, not a flaky micro-benchmark.

Self-contained; sets its own env. Run:
  DLL=tests/ClickUpTodo.Tui.E2E/bin/Release/net10.0/ClickUpTodo.Tui.E2E.dll
  timeout 200 python3 -u tests/ClickUpTodo.Tui.E2E/quick_open_modal_check.py $DLL
"""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

F1 = b"\x1bOP"
CTRL_O = b"\x0f"
ESC = b"\x1b"
ENTER = b"\r"
DOWN = b"\x1b[B"

FORM_TITLE = "Open a task"                      # the surface title, in BOTH hosts
FORM_PROMPT = "custom id"                       # a slice of the prompt label, in BOTH hosts
NATIVE_TITLE_MARKER = "[native modal spike]"    # ONLY in leg B's native Dialog title
HELP_MARKER = "Keyboard shortcuts"              # the Help title, in BOTH hosts (modal-stacking)
LIST_MARKER = "next section"                    # a footer/list-only string present iff back on the list
DETAIL_MARKER = "Description"                   # a Task Detail tab label, present iff a task opened
CACHED_ID = "t5"                                # a cached task (working set is t0..t19)
LATENCY_CEILING_MS = 600                        # absolute sanity ceiling (manual run, not CI)


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


def open_surface(app, label):
    """Ctrl+O → assert the form opened and the intended host rendered. Returns open latency (ms)."""
    open_ms, _, v = app.send_measured(CTRL_O, FORM_TITLE)
    assert FORM_TITLE in v, f"{label}: Ctrl+O did not open the surface ({FORM_TITLE!r} missing):\n{v[:2000]}"
    assert FORM_PROMPT in v, f"{label}: surface prompt missing ({FORM_PROMPT!r}):\n{v[:2000]}"
    if app.native:
        assert NATIVE_TITLE_MARKER in v, \
            f"{label}: native flag set but the native-Dialog title marker is missing " \
            f"(flag not honored — leg fell back to _screens?):\n{v[:2000]}"
    else:
        assert NATIVE_TITLE_MARKER not in v, \
            f"{label}: the native-Dialog title marker leaked into the _screens leg:\n{v[:2000]}"
    assert open_ms is not None, f"{label}: never saw the form paint marker after Ctrl+O"
    assert open_ms <= LATENCY_CEILING_MS, \
        f"{label}: Ctrl+O open latency {open_ms:.0f}ms exceeds {LATENCY_CEILING_MS}ms ceiling"
    return open_ms


def type_and_open_here(app, label):
    """With the surface open, type a cached id into the field (measuring intra-modal key→paint on the
    incremental char), then Enter — OpenHere marshals and NAVIGATES to Task Detail. On the native leg this
    proves the AddTimeout-free resolve: the nested run returned after teardown, so the detail mounts.
    Returns the single-key latency (ms). Leaves the app on Task Detail."""
    # Prime with all but the last char, then measure the incremental paint of the final char in the field.
    app.send(CACHED_ID[:-1], 0.4)                                  # "t"
    key_ms, _, v = app.send_measured(CACHED_ID[-1:], CACHED_ID)    # "5" -> "t5" visible in the field
    assert CACHED_ID in v, \
        f"{label}: typing did not echo {CACHED_ID!r} in the field (focus never reached the TextField?):\n{v[:2000]}"
    assert key_ms is not None, f"{label}: never saw the typed char paint inside the modal"
    assert key_ms <= LATENCY_CEILING_MS, \
        f"{label}: intra-modal key→paint {key_ms:.0f}ms exceeds {LATENCY_CEILING_MS}ms ceiling"
    # Enter = OpenHere → resolve the cached id and navigate to its Task Detail.
    v = app.send(ENTER, 2.5)
    assert app.alive(), f"{label}: process died marshalling OpenHere (dispose bug #346?):\n{v[:2000]}"
    assert FORM_TITLE not in v, f"{label}: Enter did not close the surface:\n{v[:2000]}"
    assert DETAIL_MARKER in v, \
        f"{label}: OpenHere did not navigate to Task Detail ({DETAIL_MARKER!r} missing — a stuck " \
        f"'Loading details…' means the resolve fired while the surface was still mounted):\n{v[:2000]}"
    return key_ms


def blank_submit_stays_open(app, label):
    """With the surface open and the field EMPTY, Enter must NOT dismiss or navigate — the builder's
    Submit else-branch flashes a hint and keeps the surface open (for every intent). Verifies the one bit
    of builder-owned branching on both hosts, and on the native leg that the flash routes through
    TodoApp.Flash onto the status line beneath the dialog. Leaves the surface open."""
    v = app.send(ENTER, 1.0)                        # Enter on the empty field
    assert app.alive(), f"{label}: process died on a blank submit:\n{v[:2000]}"
    assert FORM_TITLE in v, f"{label}: a blank submit dismissed the surface (must stay open):\n{v[:2000]}"
    assert DETAIL_MARKER not in v, f"{label}: a blank submit navigated to a detail (must be inert):\n{v[:2000]}"
    assert "ClickUp task URL" in v, \
        f"{label}: a blank submit did not flash the hint ('ClickUp task URL' missing):\n{v[:2000]}"


def cancel_no_navigation(app, label):
    """Open the surface, type a throwaway id, Esc — the surface closes with no navigation (back on the
    list, no Task Detail). 'Cancel marshals nothing'."""
    open_surface(app, label + "/cancel")
    app.send("zzz9", 0.4)
    v = app.send(ESC, 1.0)
    assert app.alive(), f"{label}: process died cancelling the surface:\n{v[:2000]}"
    assert FORM_TITLE not in v, f"{label}: Esc did not close the surface:\n{v[:2000]}"
    assert DETAIL_MARKER not in v, f"{label}: Esc on the surface navigated to a detail — Cancel must marshal nothing:\n{v[:2000]}"
    assert LIST_MARKER in v, f"{label}: task list not restored after Esc:\n{v[:2000]}"
    assert "Task 1" in v, f"{label}: list not intact after cancelling the surface:\n{v[:2000]}"


def modal_stacking(app, label):
    """F1 over the open surface opens Help; Esc returns to the surface (not the list)."""
    open_surface(app, label + "/stack")
    v = app.send(F1, 1.0)
    assert HELP_MARKER in v, f"{label}: F1 did not open Help over the surface:\n{v[:2000]}"
    v = app.send(ESC, 1.0)
    assert HELP_MARKER not in v, f"{label}: Esc did not close Help:\n{v[:2000]}"
    assert FORM_TITLE in v, f"{label}: Esc from Help did not return to the quick-open surface:\n{v[:2000]}"
    app.send(ESC, 1.0)                              # close the surface back to the list


def run_leg(label, native, **env):
    app = App(E2E_TASKS="20", E2E_REFRESH="600", **env)
    app.native = native
    try:
        assert app.boot(), f"{label}: app did not boot (Task 1 never rendered):\n{app.visible()[:1500]}"

        # 1-3. Open + blank-submit guard + intra-modal typing + OpenHere marshal & navigate.
        open_ms = open_surface(app, label)
        blank_submit_stays_open(app, label)          # blank Enter flashes + stays open (both legs)
        key_ms = type_and_open_here(app, label)
        print(f"  [{label}] open {open_ms:.0f}ms · intra-modal key {key_ms:.0f}ms · OpenHere → Task Detail")
        v = app.send(ESC, 1.2)                       # detail → back to the list
        assert LIST_MARKER in v, f"{label}: Esc from Task Detail did not return to the list:\n{v[:2000]}"

        # 4. Cancel marshals nothing — open, type a throwaway id, Esc (no navigation, list intact).
        cancel_no_navigation(app, label)
        print(f"  [{label}] cancel marshalled nothing")

        # 5. Modal-stacking — F1 over the surface, Esc back to the surface, Esc to the list.
        modal_stacking(app, label)
        print(f"  [{label}] modal-stacking ok (F1 over the surface)")
        assert app.alive(), f"{label}: process died after the modal cycles"

        # 6. Outer responsiveness — list nav stays snappy after the cycles (#3 invariant).
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

        return {"open_ms": open_ms, "key_ms": key_ms, "nav_ms": nav_median}
    finally:
        app.kill()


apps_ok = True
try:
    print("── Leg A: hand-mounted _screens QuickOpenScreen (production default) ──")
    a = run_leg("A/_screens", native=False)

    print("── Leg B: native Terminal.Gui Dialog on a nested Application.Run (CLICKUP_TODO_NATIVE_MODAL=1) ──")
    b = run_leg("B/native", native=True, CLICKUP_TODO_NATIVE_MODAL="1")

    # The #3-invariant guard: the native nested-run modal must not leave list nav or intra-modal typing
    # meaningfully slower than the _screens baseline.
    nav_budget = max(a["nav_ms"] * 2.5, a["nav_ms"] + 150)
    assert b["nav_ms"] <= nav_budget, (
        f"post-modal list-nav regressed: native {b['nav_ms']:.0f}ms vs _screens {a['nav_ms']:.0f}ms "
        f"(budget {nav_budget:.0f}ms) — a nested run-loop is degrading the #3 latency invariant")
    key_budget = max(a["key_ms"] * 2.5, a["key_ms"] + 150)
    assert b["key_ms"] <= key_budget, (
        f"intra-modal key→paint regressed: native {b['key_ms']:.0f}ms vs _screens {a['key_ms']:.0f}ms "
        f"(budget {key_budget:.0f}ms) — typing inside the native modal is slower than the _screens form")

    print("\n===== #618 quick-open native-modal A/B grid =====")
    print(f"{'metric':<28}{'A _screens':>14}{'B native':>14}")
    print(f"{'Ctrl+O open→paint (ms)':<28}{a['open_ms']:>14.0f}{b['open_ms']:>14.0f}")
    print(f"{'intra-modal key→paint (ms)':<28}{a['key_ms']:>14.0f}{b['key_ms']:>14.0f}")
    print(f"{'post-modal list-nav (ms)':<28}{a['nav_ms']:>14.0f}{b['nav_ms']:>14.0f}")
    print(f"\nok — leg B proved native (title marker); both hosts open/type/marshal(OpenHere+cancel)/stack/"
          f"close cleanly (alive); OpenHere navigates to Task Detail on both (native without AddTimeout); "
          f"intra-modal key {b['key_ms']:.0f}ms within budget {key_budget:.0f}ms and post-modal nav "
          f"{b['nav_ms']:.0f}ms within budget {nav_budget:.0f}ms of _screens")
except AssertionError as e:
    apps_ok = False
    print("FAIL:", e)

sys.exit(0 if apps_ok else 1)
