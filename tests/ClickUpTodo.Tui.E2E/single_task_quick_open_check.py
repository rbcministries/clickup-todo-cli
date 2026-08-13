#!/usr/bin/env python3
"""Drives Ctrl+O quick-open in single-task launch mode (C, #616) end-to-end. Boots the harness with
E2E_SINGLE_TASK=t0 + E2E_TREE=1 — the equivalent of `clickup-todo --task t0` against the fixed
ancestry/child tree the tree scenario serves (see Program.cs TreeTaskJson):

    tanc (Ancestor epic ANCESTOR)
      └─ t0   (Release task ROOT)          ← the launch task
           ├─ t0c1  (Subtask one CHILDONE)
           │    └─ t0c1a (Nested subtask GRANDKID)
           └─ t0c2  (Subtask two CHILDTWO)

Asserts the wiring #616 adds to SingleTaskApp (dashboard parity — TodoApp already had it):
  1. Ctrl+O now opens the quick-open entry surface (subscription is LIVE; it was a silent no-op before).
     The surface renders its prompt and, from B (#615), the New tab / Split pane buttons — in this host too.
  2. Esc cancels the surface with no navigation — back on the launch task, tab intact, tab not quit.
  3. OpenHere: typing another task's id (t0c2) + Enter navigates the detail to it, STACKED over the launch
     task, so a single Esc walks back to the launch task (the recorded #616 decision — single-task mode's
     realisation of the #402 Back contract, uniform with the tree/link detail->detail navigation #374/#318).
  4. NewTab: with t0c2 typed, activating the driver-robust "New tab" button (Ctrl+Enter folds into a bare
     newline on some drivers — the epic's own note — so the button is the reachable path) reaches the LAUNCH
     terminus, not OpenHere: a launch flash names t0c2 and the view stays on the launch task (never opens
     t0c2's detail in place). Under the headless harness the launch has no emulator to target, so the flash
     is the copy-command / "couldn't open" fallback — proving the gesture reached LaunchAppForTask rather
     than being a dead key.

To discriminate "on the launch task" from "stacked over it", key on the tree-only rows
(ANCESTOR / CHILDONE / GRANDKID): they appear only on the launch task's Task Tree tab, never in the #418
window title (which always carries "Release task ROOT") and never on a child detail opened on its Stream
tab — exactly the discriminator single_task_tree_check.py uses."""
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


TREE_ONLY = ("ANCESTOR", "CHILDONE", "GRANDKID")

CTRL_RIGHT = b"\x1b[1;5C"
CTRL_O = b"\x0f"
ENTER = b"\r"
ESC = b"\x1b"
TAB = b"\t"


def on_launch_task():
    """True when the launch task's Task Tree tab is front-most (all its tree-only rows visible)."""
    v = visible()
    return all(tok in v for tok in TREE_ONLY)


def cycle_to_tree_tab():
    """From a freshly-shown detail (default Stream tab): cycle to the 6th Task Tree tab and let the lazy
    tree load land — so the tree-only rows are on screen as the "on the launch task" discriminator."""
    for _ in range(5):   # Stream -> Description -> Comments -> Other -> Checklists -> Task Tree
        send(CTRL_RIGHT)
        pump(0.4)
    pump(3.0)


try:
    # ── Boot: straight into the launch task's detail (not the dashboard list) ──────────────────────
    pump(8.0)
    boot = visible()
    assert "Description" in boot, "detail screen did not render on --task launch:\n" + boot
    assert "Release task" in boot, "launch task (t0) name not shown:\n" + boot

    cycle_to_tree_tab()
    assert on_launch_task(), "did not land on the launch task's Task Tree tab at boot:\n" + visible()

    # ── 1) Ctrl+O opens the quick-open surface (the subscription is now LIVE in single-task mode) ───
    send(CTRL_O)
    pump(2.0)
    v = visible()
    assert "Task id, custom id, or URL" in v, \
        "Ctrl+O did not open the quick-open surface in single-task mode (subscription not wired?):\n" + v
    # B's launch-mode buttons render in this host too (the driver-robust path for the two chords).
    for btn in ("New tab", "Split pane"):
        assert btn in v, f"quick-open surface missing the '{btn}' button:\n{v}"
    assert not on_launch_task(), "the surface did not cover the launch-task detail:\n" + v

    # ── 2) Esc cancels the surface — back on the launch task, nothing navigated, tab not quit ───────
    send(ESC)
    pump(1.5)
    assert on_launch_task(), "Esc did not close the surface back to the launch task:\n" + visible()
    assert proc.poll() is None, "Esc on the quick-open surface quit the single-task tab"
    assert "Are you sure you want to exit?" not in visible(), \
        "Esc on the quick-open surface raised the exit prompt:\n" + visible()

    # ── 3) OpenHere: type another task's id + Enter navigates in place (stacked); Esc walks back ─────
    send(CTRL_O)
    pump(1.5)
    send(b"t0c2")           # a real child task the fake serves (GET /task/t0c2)
    pump(0.6)
    send(ENTER)             # Enter → Open (OpenHere): resolve + open t0c2's detail, stacked over t0
    pump(3.5)
    v = visible()
    assert "Subtask two CHILDTWO" in v, \
        "OpenHere (Enter) did not navigate to the typed task in single-task mode:\n" + v
    for tok in TREE_ONLY:
        assert tok not in v, \
            f"OpenHere did not stack over the launch task / opened on its tree ({tok} still visible):\n{v}"

    send(ESC)               # Back: walk one task back to the launch task (NOT a quit)
    pump(2.0)
    assert on_launch_task(), "Esc did not walk back to the launch task after an OpenHere navigation:\n" + visible()
    assert proc.poll() is None, "Esc from the quick-opened detail quit the single-task tab instead of walking back"
    assert "Are you sure you want to exit?" not in visible(), \
        "Esc from the quick-opened detail raised the exit prompt:\n" + visible()

    # ── 4) NewTab: the "New tab" button reaches the LAUNCH terminus (not OpenHere) ───────────────────
    # Ctrl+Enter folds into a bare newline on some drivers (the epic's note), so drive the driver-robust
    # button: Tab past "Open" to "New tab", then Enter. It must launch (a flash naming t0c2) and leave the
    # view on the launch task — never open t0c2 in place.
    send(CTRL_O)
    pump(1.5)
    send(b"t0c2")
    pump(0.6)
    send(TAB)               # focus "Open"
    pump(0.4)
    send(TAB)               # focus "New tab"
    pump(0.4)
    send(ENTER)             # activate "New tab" → NewTab intent → LaunchAppForTask
    pump(3.5)
    v = visible()
    # "tab where supported" is unique to the NewTab launch flash (Opening / Opened / the no-emulator
    # Fallback all carry it) and never appears in a tree row — so it proves the gesture reached
    # LaunchAppForTask. (Keying on the bare id "t0c2" would false-pass: it is also the t0c2 tree row's id.)
    assert "tab where supported" in v, \
        "the New tab button did not reach the launch path (no NewTab launch flash):\n" + v
    # Still on the launch task — a NewTab launch must NOT navigate in place (OpenHere would have opened
    # t0c2's detail on its Stream tab, dropping the launch task's tree-only rows).
    assert on_launch_task(), "the New tab launch navigated in place / left the launch task's detail:\n" + v
    assert proc.poll() is None, "the New tab launch quit the single-task tab"

    print("ok — Ctrl+O opens the surface in single-task mode; Esc cancels; OpenHere navigates + Esc walks "
          "back; the New tab button reaches the launch terminus")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
