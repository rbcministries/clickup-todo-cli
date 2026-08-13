#!/usr/bin/env python3
"""Boots the TUI under a PTY and exercises the Task Tree tab F2 rename (contextual chords H, #545) —
the tree half of the slice whose main-list half `rename_check.py` covers.

With E2E_TREE=1 the fake backend serves a fixed ancestry/child tree hung off the opened task t0
(same fixture as tree_tab_check.py):

    tanc (Ancestor epic ANCESTOR)
      └─ t0   (Release task ROOT)          ← the opened task (the tree's default-highlighted row)
           ├─ t0c1  (Subtask one CHILDONE)
           │    └─ t0c1a (Nested subtask GRANDKID)
           └─ t0c2  (Subtask two CHILDTWO)

Three legs, each its own boot (the fake's task Name is mutable — PUT /task/{id} applies a {"name":…}
body — so a fresh process is a fresh fixture):

  Leg A (current-task rename): on the Task Tree tab the cursor lands on the current task's row (ROOT).
    F2 opens the "Rename task" overlay pre-filled with its title; clear + type + Enter renames it, and
    BOTH the tree row AND the detail HEADER reflect the new title (the header follows the current task),
    with the old title gone.
  Leg B (child rename leaves the header): move the selection to a non-current child row (CHILDTWO) and
    rename it; the tree ROW updates but the header (still t0/ROOT) does NOT — a non-current node's rename
    touches only its row.
  Leg C (Esc cancels): F2 → type a marker → Esc closes the overlay and nothing is written.

Asserts each step on the pyte screen. The write round-trips through the default backend's PUT /task/{id}
(the same mutable-Name applier rename_check.py uses), so only E2E_TREE is needed."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

F2 = b"\x1bOQ"           # rename the highlighted node (Task Tree tab, #545)
CTRL_RIGHT = b"\x1b[1;5C"
DOWN = b"\x1b[B"
ENTER = b"\r"
ESC = b"\x1b"
BACKSPACE = b"\x7f"
DELETE = b"\x1b[3~"      # forward-delete (paired with BACKSPACE to clear a field caret-agnostically)

ROOT_NAME = "Release task ROOT"           # t0, the current task
CHILDTWO_NAME = "Subtask two CHILDTWO"     # a non-current descendant
RENAME_ROOT = "TREE RENAMED ROOT E2E"
RENAME_CHILD = "TREE RENAMED CHILD E2E"
CANCEL_MARKER = "TREE CANCELLED MARKER"


class Session:
    def __init__(self):
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        # A high refresh interval keeps the background poll out of the way so the rename's optimistic +
        # confirmed state is what the check observes (the tree isn't re-fetched mid-check).
        env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
                   E2E_TREE="1", E2E_TASKS="6", E2E_REFRESH="600")
        self.proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                                     env=env, close_fds=True, preexec_fn=os.setsid)
        os.close(slave)

    def answer(self, data):
        if b"\x1b[18t" in data:
            os.write(self.master, b"\x1b[8;%d;%dt" % (ROWS, COLS))
        if b"\x1b[6n" in data:
            os.write(self.master, b"\x1b[1;1R")

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

    def rows_with(self, substr):
        return sum(1 for y in range(ROWS) if substr in self.screen.display[y])

    def send(self, seq, wait=1.2):
        os.write(self.master, seq)
        self.pump(wait)

    def close(self):
        try:
            os.killpg(os.getpgid(self.proc.pid), signal.SIGKILL)
        except Exception:
            pass


def boot(s):
    # Poll until the list renders rather than a fixed wait: three sequential app boots in one check leave
    # the last one racing under accumulated load, and a fixed 8s occasionally lost that race.
    end = time.monotonic() + 18.0
    while time.monotonic() < end:
        s.pump(1.0)
        if "Task 0" in s.visible():
            s.pump(1.0)
            return
    raise AssertionError("app never rendered the list:\n" + s.visible()[-1500:])


def open_tree_tab(s):
    """From the main list: open t0's detail and cycle to the Task Tree tab (6th); let the tree load."""
    s.send(ENTER, 3.0)   # open the focused task (t0) in Task Detail
    for _ in range(5):   # Stream -> Description -> Comments -> Other -> Checklists -> Task Tree
        s.send(CTRL_RIGHT, 0.4)
    s.pump(3.0)          # let the lazy tree load land
    v = s.visible()
    for token in ("ANCESTOR", "ROOT", "CHILDONE", "GRANDKID", "CHILDTWO"):
        assert token in v, f"tree tab did not render {token}:\n{v}"


def open_overlay(s):
    s.send(F2, 1.8)
    v = s.visible()
    assert "New title:" in v, f"F2 did not open the rename overlay on the tree tab:\n{v}"
    assert "Rename task" in v, f"rename overlay title missing:\n{v}"


def type_new_title(s, title):
    # Clear the prefilled title (caret-agnostic: backspaces then forward-deletes) and type a new one.
    s.send(BACKSPACE * 90 + DELETE * 90, 0.6)
    s.send(title.encode(), 1.2)
    assert title in s.visible(), f"typed new title not shown in the overlay:\n{s.visible()}"


def save_and_wait_close(s):
    """Enter saves; poll until the overlay closes (optimistic close + PUT-echo repaint), robust to load."""
    os.write(s.master, ENTER)
    end = time.monotonic() + 6.0
    while time.monotonic() < end:
        s.pump(0.4)
        if "New title:" not in s.visible():
            s.pump(0.8)   # let the confirmed (PUT-echo) repaint land after the close
            return
    raise AssertionError(f"Enter did not close the overlay:\n{s.visible()}")


def run_current_task_rename():
    """Leg A: F2 on the default-highlighted current row (ROOT) renames it; header AND tree row update."""
    s = Session()
    try:
        boot(s)
        open_tree_tab(s)
        # The current task's title is on the header AND on its (default-selected) tree row — two places.
        assert s.rows_with(ROOT_NAME) >= 2, \
            f"expected the current task's title on both the header and its tree row:\n{s.visible()}"

        open_overlay(s)
        # The overlay is pre-filled with the current task's title (proves it targets the highlighted node).
        assert ROOT_NAME in s.visible(), f"overlay not pre-filled with the node's title:\n{s.visible()}"
        type_new_title(s, RENAME_ROOT)

        save_and_wait_close(s)   # save: optimistic, then confirmed by the PUT echo
        v = s.visible()
        # Both the tree row and the detail header now carry the new title; the old title is gone from both.
        assert s.rows_with(RENAME_ROOT) >= 2, \
            f"the new title is not on both the header and the tree row after save:\n{v}"
        assert ROOT_NAME not in v, f"the old title still shows after renaming the current node:\n{v}"
        print("ok — Task Tree F2 renames the current node: header + tree row both update")
    finally:
        s.close()


def run_child_rename_keeps_header():
    """Leg B: renaming a non-current child row updates only its row, not the header."""
    s = Session()
    try:
        boot(s)
        open_tree_tab(s)
        # Move to the bottom row (the ListView clamps there) — deterministically CHILDTWO, a non-current
        # node — regardless of the initial cursor, exactly as tree_tab_check.py does.
        for _ in range(4):
            s.send(DOWN, 0.2)
        assert CHILDTWO_NAME in s.visible(), f"CHILDTWO row not present to rename:\n{s.visible()}"

        open_overlay(s)
        assert CHILDTWO_NAME in s.visible(), f"overlay not pre-filled with the child's title:\n{s.visible()}"
        type_new_title(s, RENAME_CHILD)

        save_and_wait_close(s)
        v = s.visible()
        assert RENAME_CHILD in v, f"the child tree row did not update to the new title:\n{v}"
        assert CHILDTWO_NAME not in v, f"the child's old title still shows after rename:\n{v}"
        # The header follows the CURRENT task (ROOT), which we did not rename — it must be untouched.
        assert ROOT_NAME in v, f"renaming a non-current node wrongly changed the header:\n{v}"
        print("ok — renaming a non-current tree node updates its row only, header unchanged")
    finally:
        s.close()


def run_esc_cancels():
    """Leg C: Esc in the overlay writes nothing."""
    s = Session()
    try:
        boot(s)
        open_tree_tab(s)
        open_overlay(s)
        type_new_title(s, CANCEL_MARKER)

        s.send(ESC, 1.8)
        v = s.visible()
        assert "New title:" not in v, f"Esc did not close the rename overlay:\n{v}"
        assert CANCEL_MARKER not in v, f"Esc still applied the rename (marker leaked to the tree):\n{v}"
        assert ROOT_NAME in v, f"Esc cancel unexpectedly changed the current node's title:\n{v}"
        print("ok — Esc cancels the tree rename: overlay closes and nothing is written")
    finally:
        s.close()


run_current_task_rename()
run_child_rename_keeps_header()
run_esc_cancels()
print("TREE RENAME E2E: PASS")
