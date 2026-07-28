#!/usr/bin/env python3
"""Drives the Task Tree tab in single-task launch mode (#374) end-to-end. Boots the harness with
E2E_SINGLE_TASK=t0 + E2E_TREE=1 — the equivalent of `clickup-todo --task t0` against the fixed
ancestry/child tree the tree scenario serves (see Program.cs TreeTaskJson):

    tanc (Ancestor epic ANCESTOR)
      └─ t0   (Release task ROOT)          ← the launch task
           ├─ t0c1  (Subtask one CHILDONE)
           │    └─ t0c1a (Nested subtask GRANDKID)
           └─ t0c2  (Subtask two CHILDTWO)

Asserts the wiring #374 adds to SingleTaskApp:
  1. The launched detail now carries a Task Tree tab; cycling to it renders ancestry + the task +
     its descendants (all five nodes) — proving loadTaskTreeAsync/currentUserId are wired in
     single-task mode, not just the dashboard.
  2. F6 on the tab cycles the badge display through all three modes (icons/text/hidden) — parity
     with the dashboard tree tab (#415). Robust to the persisted starting mode: a 3-press cycle
     visits every mode regardless of where it starts.
  3. Enter on a non-current row STACKS that task's detail over the launch task; a single Esc walks
     back to the launch task (it does NOT quit the tab), the walkable-back model (#401/#298).
  4. Double-clicking a tree row navigates the same way (mouse equivalent, via the shared RowHitTester).
  5. Esc at the launch-task ROOT (nothing stacked) hands off to the #299 exit confirmation — there is
     no main list to fall back to, so Back-at-root is a guarded quit. Y answers it and the tab exits.

Mouse is injected as SGR-1006 sequences, exactly like double_click_check.py / tree_tab_check.py.
The Enter leg (§3) selects the target row with a single click before pressing Enter, rather than
walking to it with ↑/↓: arrow-key selection inside the detail's Tabs-hosted ListView is not
exercisable under this headless PTY (the sibling dashboard tree_tab_check hits the same limit), so
the row is selected deterministically by click and Enter then drives the real keyboard activation
path (OnKey -> NavigateTreeSelection -> OpenTaskRequested)."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", E2E_SINGLE_TASK="t0", E2E_TREE="1")
proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                        env=env, close_fds=True, preexec_fn=os.setsid)
os.close(slave)


def answer(data):
    if b"\x1b[18t" in data:
        os.write(master, b"\x1b[8;%d;%dt" % (ROWS, COLS))
    if b"\x1b[6n" in data:
        os.write(master, b"\x1b[1;1R")


def pump(seconds):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        r, _, _ = select.select([master], [], [], 0.03)
        if r:
            try:
                chunk = os.read(master, 65536)
            except OSError:
                break
            if not chunk:
                break
            answer(chunk)
            stream.feed(chunk)


def visible():
    return "\n".join(screen.display[y].rstrip() for y in range(ROWS))


def send(data):
    os.write(master, data)


def _sgr(col0, row0, press):
    return b"\x1b[<0;%d;%d%s" % (col0 + 1, row0 + 1, b"M" if press else b"m")


def click(col0, row0):
    send(_sgr(col0, row0, True)); send(_sgr(col0, row0, False))


def double_click(col0, row0, gap=0.08):
    send(_sgr(col0, row0, True)); send(_sgr(col0, row0, False))
    time.sleep(gap)
    send(_sgr(col0, row0, True)); send(_sgr(col0, row0, False))


def row_of(substr):
    for y in range(ROWS):
        if substr in screen.display[y]:
            return y
    return -1


def rows_with(substr):
    """How many screen rows contain substr. The detail HEADER always shows the current task's
    "○ in progress" status once, independent of the tree's badge mode; the tree ROWS add one each
    only in text mode — so this count separates the two (1 = header only, >1 = header + text badges)."""
    return sum(1 for y in range(ROWS) if substr in screen.display[y])


def badge_mode():
    """Which mode the tree tab is rendering: the "(IP)" abbreviation chip marks icons, a >1
    "in progress" count marks text badges on the rows, neither marks hidden."""
    v = visible()
    if "(IP)" in v:
        return "icons"
    return "text" if rows_with("in progress") > 1 else "hidden"


CTRL_RIGHT = b"\x1b[1;5C"
ENTER = b"\r"
ESC = b"\x1b"
F6 = b"\x1b[17~"


def cycle_to_tree_tab():
    """From a freshly-shown detail (default Stream tab): cycle to the 5th Task Tree tab and let the
    lazy tree load land."""
    for _ in range(4):   # Stream -> Description -> Comments -> Other -> Task Tree
        send(CTRL_RIGHT)
        pump(0.4)
    pump(3.0)


try:
    # ── Boot: straight into the launch task's detail (not the dashboard list) ──────────────────
    pump(8.0)
    boot = visible()
    assert "Description" in boot, "detail screen did not render on --task launch:\n" + boot
    assert "Release task" in boot, "launch task (t0) name not shown:\n" + boot
    assert "follow up on the" not in boot, "dashboard list rows rendered in single-task mode:\n" + boot

    # ── 1) The Task Tree tab is now present and renders the tree (the #374 wiring) ──────────────
    cycle_to_tree_tab()
    v = visible()
    for token in ("ANCESTOR", "ROOT", "CHILDONE", "GRANDKID", "CHILDTWO"):
        assert token in v, f"Task Tree tab missing {token} in single-task mode:\n{v}"

    # ── 2) F6 cycles the badge display through all three modes (dashboard parity, #415) ─────────
    seen = {badge_mode()}
    for _ in range(3):
        send(F6); pump(1.0)
        seen.add(badge_mode())
        assert "ROOT" in visible(), "cycling badges dropped the tree rows:\n" + visible()
    assert seen == {"icons", "text", "hidden"}, \
        "F6 did not cycle the tree badges through all three modes; saw " + str(seen)

    # ── 3) Enter on a non-current row stacks its detail; a single Esc WALKS BACK to the launch task
    # Select CHILDTWO by clicking it (arrow-key selection isn't exercisable here — see the header),
    # then Enter drives the keyboard activation path.
    y = row_of("CHILDTWO")
    assert y >= 0, "CHILDTWO row not found to select:\n" + visible()
    click(12, y)
    pump(1.0)
    send(ENTER)          # open CHILDTWO's detail stacked over t0's
    pump(3.0)
    v = visible()
    assert "CHILDTWO" in v, "Enter did not navigate to the child task in single-task mode:\n" + v
    assert "GRANDKID" not in v, "expected the new detail on its Stream tab, not the tree:\n" + v
    assert "Release task" not in v, "still showing the launch task we navigated from:\n" + v

    send(ESC)            # Back: walk one task back to the launch task (NOT a quit)
    pump(2.0)
    v = visible()
    assert "Release task" in v, "Esc did not walk back to the launch task's detail:\n" + v
    assert proc.poll() is None, "Esc from a stacked child quit the single-task tab instead of walking back"
    assert "Are you sure you want to exit?" not in v, "Esc from a stacked child raised the exit prompt:\n" + v

    # ── 4) Double-clicking a tree row navigates the same way (stacked) ──────────────────────────
    # We are back on the launch task's Task Tree tab; double-click CHILDONE.
    y = row_of("CHILDONE")
    assert y >= 0, "CHILDONE row not found for double-click:\n" + visible()
    double_click(12, y)
    pump(3.0)
    v = visible()
    assert "CHILDONE" in v, "double-click did not navigate to the child task:\n" + v
    assert "GRANDKID" not in v, "double-click did not land on the new detail's Stream tab:\n" + v
    assert "Release task" not in v, "double-click did not stack over the launch task:\n" + v
    send(ESC)            # back to the launch task
    pump(2.0)
    assert "Release task" in visible(), "Esc did not walk back to the launch task after double-click:\n" + visible()

    # ── 5) Esc at the launch-task ROOT asks to confirm the exit (#299), then Y quits ────────────
    send(ESC)
    pump(2.0)
    v = visible()
    assert "Are you sure you want to exit?" in v, \
        "Esc at the launch-task root did not hand off to the exit confirmation:\n" + v
    assert proc.poll() is None, "Esc at the root quit the single-task tab without confirming"

    send(b"Y")
    end = time.monotonic() + 5.0
    while time.monotonic() < end and proc.poll() is None:
        pump(0.3)
    assert proc.poll() is not None, "Y at the confirmation did not quit the single-task tab"

    print("ok")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
