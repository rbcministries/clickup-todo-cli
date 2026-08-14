#!/usr/bin/env python3
"""Drives task/subtask Delete on the Task Detail Task Tree tab (F, #594) end-to-end. Boots the harness
in single-task launch mode (E2E_SINGLE_TASK=t0 + E2E_TREE=1, i.e. `clickup-todo --task t0`) against the
fixed ancestry/child tree the tree scenario serves (see Program.cs TreeTaskJson):

    tanc  (Ancestor epic ANCESTOR)              depth 0  (ancestor)
      └─ t0    (Release task ROOT)              depth 1  ← the launch/current task
           ├─ t0c1  (Subtask one CHILDONE)      depth 2
           │    └─ t0c1a (Nested subtask GRANDKID) depth 3
           └─ t0c2  (Subtask two CHILDTWO)      depth 2

The DELETE /task/{id} write is captured by TaskDeleteLogScenario (E2E_TASK_DELETE_LOG), so the write is
asserted from a file fact — not guessed from the screen — mirroring comment_delete_check.py. The confirm
is the flag-off inline armed confirm (CLICKUP_TODO_NATIVE_MODAL is off under this ANSI harness), answered
with Enter/Esc.

Rows are selected by click, not ↑/↓: arrow-key selection inside the detail's Tabs-hosted ListView isn't
exercisable under this headless PTY (single_task_tree_check.py hits the same limit).

Legs (each its own boot):
  A. Ancestor is inert (downward-only): Delete on ANCESTOR flashes a guard, arms nothing, writes nothing.
     Then Delete on CHILDTWO (a leaf subtask) arms the confirm; Enter deletes → the row disappears in
     place, the view (ROOT + the rest of the tree) is intact, and t0c2 is recorded.
  B. Revert: with E2E_TASK_DELETE_FORBID=t0c2 the DELETE answers 403; the optimistic removal reverts
     (CHILDTWO reappears) and a "Could not delete" flash shows — the write still fired (t0c2 recorded).
  C. Current-task delete: Delete on the current ROOT row arms the confirm; Enter deletes t0 → the --task
     launch root has no subject left, so the tab quits directly (no exit prompt) and t0 is recorded.
"""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess, tempfile
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

DELETE = b"\x1b[3~"
CTRL_RIGHT = b"\x1b[1;5C"
ENTER = b"\r"
ESC = b"\x1b"


class Session:
    def __init__(self, extra_env):
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        env = dict(os.environ, TERM="xterm-256color", E2E_SINGLE_TASK="t0", E2E_TREE="1", **extra_env)
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
            r, _, _ = select.select([self.master], [], [], 0.03)
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
        return "\n".join(self.screen.display[y].rstrip() for y in range(ROWS))

    def send(self, data):
        os.write(self.master, data)

    def row_of(self, substr):
        for y in range(ROWS):
            if substr in self.screen.display[y]:
                return y
        return -1

    def click(self, col0, row0):
        self.send(b"\x1b[<0;%d;%dM" % (col0 + 1, row0 + 1))
        self.send(b"\x1b[<0;%d;%dm" % (col0 + 1, row0 + 1))

    def select_row(self, token):
        y = self.row_of(token)
        assert y >= 0, f"{token} row not found to select:\n{self.visible()}"
        self.click(12, y)
        self.pump(0.8)

    def cycle_to_tree_tab(self):
        # Stream -> Description -> Comments -> Other -> Checklists -> Task Tree (6th tab), then let the
        # lazy tree load land.
        for _ in range(5):
            self.send(CTRL_RIGHT)
            self.pump(0.4)
        self.pump(3.0)

    def kill(self):
        try:
            os.killpg(os.getpgid(self.proc.pid), signal.SIGKILL)
        except Exception:
            pass


def read_log(path):
    try:
        with open(path) as f:
            return [line.strip() for line in f if line.strip()]
    except FileNotFoundError:
        return []


def boot_to_tree(extra_env):
    s = Session(extra_env)
    s.pump(8.0)
    boot = s.visible()
    assert "Description" in boot, "detail did not render on --task launch:\n" + boot
    assert "Release task" in boot, "launch task (t0) name not shown:\n" + boot
    s.cycle_to_tree_tab()
    v = s.visible()
    for token in ("ANCESTOR", "ROOT", "CHILDONE", "GRANDKID", "CHILDTWO"):
        assert token in v, f"Task Tree tab missing {token}:\n{v}"
    return s


def leg_a():
    log = tempfile.mktemp(prefix="task_delete_a_")
    s = boot_to_tree({"E2E_TASK_DELETE_LOG": log})
    try:
        # Ancestor is inert (downward-only): Delete on ANCESTOR arms nothing and writes nothing. (No Enter
        # here — with nothing armed, Enter on the tree tab navigates to the selected row, not a delete.)
        s.select_row("ANCESTOR")
        s.send(DELETE)
        s.pump(0.8)
        assert "ANCESTOR" in s.visible(), "ancestor row vanished on Delete:\n" + s.visible()
        assert "Delete task" not in s.visible() and "Delete subtask" not in s.visible(), \
            "Delete on an ancestor armed a confirm:\n" + s.visible()
        assert read_log(log) == [], "Delete on an ancestor wrote a DELETE: " + str(read_log(log))

        # Delete a leaf subtask in place.
        s.select_row("CHILDTWO")
        s.send(DELETE)
        s.pump(0.8)
        assert "Delete subtask" in s.visible(), "subtask delete confirm not shown:\n" + s.visible()
        s.send(ENTER)
        s.pump(2.0)
        v = s.visible()
        assert "CHILDTWO" not in v, "subtask row still present after delete:\n" + v
        assert "ROOT" in v and "CHILDONE" in v and "GRANDKID" in v, \
            "deleting a subtask disturbed the rest of the tree / navigated away:\n" + v
        recorded = read_log(log)
        assert recorded == ["t0c2"], "expected only t0c2 deleted, got: " + str(recorded)
        print("ok — leg A: ancestor inert; subtask CHILDTWO deleted in place (t0c2), view intact")
    finally:
        s.kill()


def leg_b():
    log = tempfile.mktemp(prefix="task_delete_b_")
    s = boot_to_tree({"E2E_TASK_DELETE_LOG": log, "E2E_TASK_DELETE_FORBID": "t0c2"})
    try:
        s.select_row("CHILDTWO")
        s.send(DELETE)
        s.pump(0.8)
        assert "Delete subtask" in s.visible(), "subtask delete confirm not shown:\n" + s.visible()
        s.send(ENTER)
        s.pump(2.5)
        v = s.visible()
        assert "CHILDTWO" in v, "forbidden subtask delete did not revert (CHILDTWO missing):\n" + v
        assert "Could not delete" in v, "no failure flash on a forbidden delete:\n" + v
        recorded = read_log(log)
        assert "t0c2" in recorded, "the forbidden DELETE never fired: " + str(recorded)
        print("ok — leg B: forbidden subtask delete reverts (CHILDTWO back) + flashes; the write fired (t0c2)")
    finally:
        s.kill()


def leg_c():
    log = tempfile.mktemp(prefix="task_delete_c_")
    s = boot_to_tree({"E2E_TASK_DELETE_LOG": log})
    try:
        # The current task is ROOT (t0). Delete it → confirm → Enter → the --task root quits directly.
        s.select_row("ROOT")
        s.send(DELETE)
        s.pump(0.8)
        assert "Delete task" in s.visible(), "current-task delete confirm not shown:\n" + s.visible()
        assert "Are you sure you want to exit" not in s.visible(), "delete confirm collided with exit prompt"
        s.send(ENTER)
        end = time.monotonic() + 6.0
        while time.monotonic() < end and s.proc.poll() is None:
            s.pump(0.3)
        assert s.proc.poll() is not None, "deleting the --task launch root did not quit the tab"
        recorded = read_log(log)
        assert recorded == ["t0"], "expected only t0 (the current task) deleted, got: " + str(recorded)
        print("ok — leg C: deleting the current ROOT task quits the --task tab directly (t0)")
    finally:
        s.kill()


def leg_d():
    # Regression: a pending delete confirm must NOT survive a tab switch. On the Task Tree tab Enter also
    # navigates (#291), so a lingering arm would be re-triggered by the very key used to move around — a
    # silent destructive delete. Arm on the current ROOT row, switch away and back, then press Enter: nothing
    # is deleted (the arm was cleared on the tab switch) and the view is intact.
    log = tempfile.mktemp(prefix="task_delete_d_")
    s = boot_to_tree({"E2E_TASK_DELETE_LOG": log})
    try:
        s.select_row("ROOT")
        s.send(DELETE)
        s.pump(0.8)
        assert "Delete task" in s.visible(), "current-task delete confirm not shown:\n" + s.visible()
        s.send(CTRL_RIGHT)   # switch to Stream (wraps), clearing the arm
        s.pump(0.6)
        s.send(b"\x1b[1;5D")  # Ctrl+Left back to the Task Tree tab
        s.pump(1.2)
        assert "ROOT" in s.visible(), "did not return to the Task Tree tab:\n" + s.visible()
        s.send(ENTER)        # with the arm cleared, Enter navigates (current row = no-op), never deletes
        s.pump(1.5)
        assert s.proc.poll() is None, "a stale armed delete fired on Enter after a tab switch (tab quit)"
        assert read_log(log) == [], "a stale armed delete wrote a DELETE after a tab switch: " + str(read_log(log))
        assert "ROOT" in s.visible(), "the current task vanished — a stale delete fired:\n" + s.visible()
        print("ok — leg D: a pending delete is cleared on a tab switch; a later Enter navigates, never deletes")
    finally:
        s.kill()


leg_a()
leg_b()
leg_c()
leg_d()
print("TASK DELETE E2E: PASS")
