#!/usr/bin/env python3
"""#376 (item 1): two-instance cross-tab nudge propagation, end-to-end under PTYs.

Boots TWO real TodoApp processes against the fake backend, both wired to a shared on-disk
LiteDbChangeMarkerStore (E2E_MARKER_DB — LiteDB shared mode is the cross-process mutex) and a shared
task-status overlay (E2E_SHARED_STATE), with distinct marker instance ids. Then:

  1. asserts both instances rendered the list and instance B's t0 row shows the seeded status chip;
  2. commits a Status change on t0 in instance A (Quick Updates: Down -> Enter, per
     foreign_quickupdates_check.py) and confirms A's own row reflects it;
  3. asserts instance B's t0 list row reconciles to the committed status WITHIN the marker-poll window
     (~4s) — the nudge-then-fetch (#294/#295) crossing the process boundary via a per-task fetch, with
     no self-echo and no full resync (E2E_REFRESH is high, so the delta poll can't be the cause).

Exits nonzero, dumping both screens, on any failure.
"""
import fcntl
import os
import pty
import select
import shutil
import signal
import struct
import subprocess
import sys
import tempfile
import termios
import time

import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

# The status chips the list row renders for the two statuses this check drives (abbreviation in parens).
SEED_CHIP = "(TD)"   # "to do"        — every task's seed for t0
NEW_CHIP = "(IP)"    # "in progress"  — what A commits t0 to

# Shared cross-process state both app processes point at.
_tmpdir = tempfile.mkdtemp(prefix="clickup-nudge-e2e-")
MARKER_DB = os.path.join(_tmpdir, "state.db")
SHARED_STATE = os.path.join(_tmpdir, "shared_state.json")

CTRL_U = b"\x15"
DOWN = b"\x1b[B"
ENTER = b"\r"
ESC = b"\x1b"


class App:
    """One PTY-driven TodoApp process with its own pyte screen."""

    def __init__(self, name, instance_id):
        self.name = name
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        env = dict(
            os.environ,
            TERM="xterm-256color",
            DOTNET_ROOT="/usr/local/dotnet",
            E2E_NUDGE="1",
            E2E_TASKS="3",          # t0,t1,t2 — a flat list (no k%4==3 subtask), so t0 is a plain top row
            E2E_REFRESH="600",      # keep the delta poll out of the window: only the nudge can move the row
            E2E_MARKER_DB=MARKER_DB,
            E2E_SHARED_STATE=SHARED_STATE,
            E2E_INSTANCE_ID=instance_id,
        )
        self.proc = subprocess.Popen(
            ["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
            env=env, close_fds=True, preexec_fn=os.setsid)
        os.close(slave)

    def _answer(self, data):
        if b"\x1b[18t" in data:
            os.write(self.master, b"\x1b[8;%d;%dt" % (ROWS, COLS))
        if b"\x1b[6n" in data:
            os.write(self.master, b"\x1b[1;1R")

    def read_once(self, timeout):
        r, _, _ = select.select([self.master], [], [], timeout)
        if not r:
            return
        try:
            chunk = os.read(self.master, 65536)
        except OSError:
            return
        if chunk:
            self._answer(chunk)
            self.stream.feed(chunk)

    def visible(self):
        return "\n".join(line.rstrip() for line in self.screen.display)

    def lines(self):
        return [line.rstrip() for line in self.screen.display]

    def row_of(self, needle):
        return next((ln for ln in self.lines() if needle in ln), None)

    def send(self, seq):
        os.write(self.master, seq)

    def kill(self):
        try:
            os.killpg(os.getpgid(self.proc.pid), signal.SIGKILL)
        except Exception:
            pass


APPS = []


def pump(seconds):
    """Drive every app's output for `seconds` so both screens stay current."""
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        for a in APPS:
            a.read_once(0.02)


def fail(msg):
    print("FAIL:", msg)
    for a in APPS:
        print(f"----- instance {a.name} -----")
        print(a.visible())
    for a in APPS:
        a.kill()
    sys.exit(1)


def open_qu_on_t0(app):
    """Open Quick Updates on t0 in `app`; Down + retry if the cursor lands on another row.
    While Quick Updates is open the task's title is the screen's heading, so "Task 0" in view is a
    reliable oracle for "on t0"."""
    for _ in range(6):
        app.send(CTRL_U)
        pump(2.5)
        v = app.visible()
        if "Quick Updates" in v and "Task 0" in v:
            return True
        if "Quick Updates" in v:
            app.send(ESC)
            pump(1.2)
        app.send(DOWN)
        pump(0.6)
    return False


A = App("A", "inst-A")
B = App("B", "inst-B")
APPS.extend([A, B])

try:
    pump(9.0)
    for a in APPS:
        if "Task 0" not in a.visible():
            fail(f"instance {a.name} never rendered the seeded list")

    # Control: B's t0 row carries the seeded status chip BEFORE any edit — so the later flip is the nudge,
    # not a pre-existing state.
    b_seed = B.row_of("Task 0")
    if b_seed is None or SEED_CHIP not in b_seed:
        fail(f"instance B's t0 row is not seeded {SEED_CHIP!r} (got: {b_seed!r})")
    print(f"BOOT ok — both instances rendered; B's t0 seeded: {b_seed.strip()!r}")

    # Commit a Status change on t0 in instance A: Status pane preselects 'to do' (row 0);
    # Down -> 'in progress' (row 1); Enter commits (apply-on-Enter, #207). Then Esc back to the list.
    if not open_qu_on_t0(A):
        fail("could not open Quick Updates on t0 in instance A")
    A.send(DOWN)
    pump(0.8)
    A.send(ENTER)
    pump(2.5)
    A.send(ESC)
    pump(1.5)
    a_row = A.row_of("Task 0")
    if a_row is None or NEW_CHIP not in a_row:
        fail(f"instance A's own t0 row did not reflect the committed status {NEW_CHIP!r} (got: {a_row!r})")
    print(f"COMMIT ok — A committed 'in progress' on t0; A's row now: {a_row.strip()!r}")

    # THE PROPAGATION PROOF: instance B, which made no edit, must reconcile t0's row to the committed
    # status within the marker-poll window (~4s). Poll generously (well past the window) so a slow machine
    # doesn't false-fail; the assertion is that it happens at all, cross-process.
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        pump(1.0)
        b_row = B.row_of("Task 0")
        if b_row and NEW_CHIP in b_row:
            print(f"NUDGE ok — B's t0 row reconciled cross-process to: {b_row.strip()!r}")
            break
    else:
        fail(f"instance B's t0 row never reflected A's change "
             f"(still {B.row_of('Task 0')!r} after 30s)")

    # Full-fidelity check (#376 item 2): the wholesale per-task replace must not strip the fields the row
    # already carried — the priority flag (⚑, t0 is high) and its list — so the reconciled row differs from
    # the seed only in the status chip, not by losing data.
    b_row = B.row_of("Task 0")
    if "⚑" not in b_row or "Personal Tasks" not in b_row:
        fail(f"instance B's reconciled t0 row lost fidelity (priority flag / list): {b_row!r}")
    print("FIDELITY ok — the wholesale reconcile kept t0's priority flag and list")
    print("TWO-INSTANCE NUDGE E2E: PASS")
finally:
    for a in APPS:
        a.kill()
    try:
        shutil.rmtree(_tmpdir)
    except Exception:
        pass
