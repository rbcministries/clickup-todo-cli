#!/usr/bin/env python3
"""Quick Updates in SINGLE-TASK launch mode, and its Task Tree tab targeting (#297 follow-up).

Single-task mode (`clickup-todo --task <id>`) used to answer Ctrl+U with a deferral flash — "Quick
Updates isn't available in single-task mode yet (tracked on #297)" — because the status/priority/assignee
write path was entangled with the dashboard's working-set snapshot (`_all`). #297 decoupled it
(IQuickUpdateTarget / SingleTaskUpdateTarget) and the host wiring now drives the same shared
QuickUpdatesCoordinator the dashboard does. This is the rendered end-to-end proof.

With E2E_SINGLE_TASK=t0 + E2E_TREE=1 the harness boots SingleTaskApp straight into t0's detail (no
dashboard list) and the fake backend serves the same fixed tree hung off t0 as single_task_tree_check.py:

    tanc (Ancestor epic ANCESTOR)
      └─ t0   (Release task ROOT)          ← the launch task
           ├─ t0c1  (Subtask one CHILDONE)
           │    └─ t0c1a (Nested subtask GRANDKID)
           └─ t0c2  (Subtask two CHILDTWO)

Four legs, each its own boot:

  Leg A (it opens at all): Ctrl+U on the launch task's detail opens Quick Updates *titled with that
    task*, and Esc pops back to the detail without quitting the tab. This is what used to be a flash.
  Leg B (the write path runs with no list): Enter on a Status row and on a Priority row each commit —
    the screen stays open (#207 apply-on-Enter) and the footer reports the server-confirmed value. The
    load-bearing negative assertion is that "no longer in the list" never appears: that is the exact
    dead-end #297 removed, and the one a snapshot-bound write path would hit here.
  Leg C (tree targeting, single-task): on the Task Tree tab with CHILDTWO highlighted, Ctrl+U opens
    Quick Updates for *that* task — the title names CHILDTWO, not ROOT — the commit repaints CHILDTWO's
    own tree-row badge (`(IP)` → `(C )`/`(IR)`) and no sibling's, and the launch task's header STATUS
    (not just its title) is unchanged: a tree row for another task must not repaint this header, and the
    tree loads once per screen so a missed row repaint would never self-heal.
  Leg D (tree targeting, dashboard parity): the same gesture in the dashboard host (no E2E_SINGLE_TASK)
    also names CHILDTWO. The tree-tab targeting lives in TaskDetailScreen, so both hosts share it —
    this leg is what pins that they can't drift.

Asserts on the pyte screen. Status/priority writes round-trip through the default backend's
PUT /task/{id}, which echoes the canned detail (status "in review", no priority) — so the *confirmed*
value the footer reports is harness-defined and only its "Set …" shape is asserted, exactly as
qu_from_detail_check.py does for the dashboard."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

CTRL_U = b"\x15"
CTRL_RIGHT = b"\x1b[1;5C"
TAB = b"\x09"
DOWN = b"\x1b[B"
UP = b"\x1b[A"
ENTER = b"\r"
ESC = b"\x1b"

ROOT_NAME = "Release task ROOT"            # t0, the launch / current task
CHILDTWO_NAME = "Subtask two CHILDTWO"     # a non-current descendant row on the tree tab

# The Quick Updates frame title names the task it targets — the assertion that pins *which* task a
# Ctrl+U resolved to (QuickUpdatesScreen: `Quick Updates — {taskName}`).
def qu_title(name):
    return f"Quick Updates — {name}"


class Session:
    def __init__(self, single_task=True):
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        # E2E_SINGLE_TASK boots SingleTaskApp straight into t0's detail; E2E_TREE serves the tree fixture.
        # A high refresh interval keeps the background poll out of the way, so what the check observes is
        # the commit's own optimistic + confirmed state rather than a re-fetch.
        env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
                   E2E_TREE="1", E2E_TASKS="6", E2E_REFRESH="600")
        if single_task:
            env["E2E_SINGLE_TASK"] = "t0"
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

    def send(self, seq, wait=1.2):
        os.write(self.master, seq)
        self.pump(wait)

    def wait_for(self, needle, seconds=8.0, settle=0.8):
        """Poll until `needle` renders (rather than a fixed wait): four sequential app boots in one check
        leave the later ones racing under accumulated load, and Ctrl+U here costs two round-trips (the
        authoritative TaskItem, then the list's statuses)."""
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            self.pump(0.4)
            if needle in self.visible():
                self.pump(settle)
                return True
        return False

    def close(self):
        try:
            os.killpg(os.getpgid(self.proc.pid), signal.SIGKILL)
        except Exception:
            pass


def boot_single_task(s):
    """Single-task mode boots straight into the launch task's detail — no list row to open first."""
    assert s.wait_for("Description", 20.0), \
        "app never rendered the launch task's detail:\n" + s.visible()[-1500:]
    assert ROOT_NAME in s.visible(), f"launch task's title missing from the detail:\n{s.visible()}"


def boot_dashboard(s):
    """The dashboard host: wait for the list, then open the focused task (t0) in Task Detail."""
    assert s.wait_for("Task 0", 20.0), "app never rendered the list:\n" + s.visible()[-1500:]
    s.send(ENTER, 3.0)
    assert "Description" in s.visible(), f"Enter did not open the detail view:\n{s.visible()}"


def open_tree_tab(s):
    """Cycle to the Task Tree tab (6th) and let the lazy tree load land."""
    for _ in range(5):   # Stream -> Description -> Comments -> Other -> Checklists -> Task Tree
        s.send(CTRL_RIGHT, 0.4)
    s.pump(3.0)
    v = s.visible()
    for token in ("ANCESTOR", "ROOT", "CHILDONE", "GRANDKID", "CHILDTWO"):
        assert token in v, f"tree tab did not render {token}:\n{v}"


def open_quick_updates(s, name):
    """Ctrl+U, then wait for the Quick Updates frame titled with `name` (proving which task it targets)."""
    s.send(CTRL_U, 0.4)
    assert s.wait_for(qu_title(name)), \
        f"Ctrl+U did not open Quick Updates for '{name}':\n{s.visible()}"
    v = s.visible()
    for pane in ("Priority", "Assignees", "Lists"):
        assert pane in v, f"Quick Updates opened without its {pane} pane:\n{v}"
    return v


def header_status(s):
    """The status shown on the detail HEADER line (`Release task ROOT  ○ in progress`) — the surface a
    tree-targeted commit must not repaint. The tree rows carry `(IP)`-style abbreviation chips, not the
    `○` text badge, so the glyph identifies the header unambiguously."""
    for y in range(0, 8):
        line = s.screen.display[y]
        if "○" in line:
            return line[line.index("○"):].strip("│ ").strip()
    return None


def tree_row(s, name):
    """The rendered Task Tree row containing `name` (rows read `t0c2 (IP)   Subtask two CHILDTWO  · …`)."""
    for y in range(ROWS):
        if name in s.screen.display[y]:
            return s.screen.display[y]
    return None


def assert_no_snapshot_deadend(s, what):
    """#297's dead-end: with the old snapshot-bound write path a commit with no `_all` present reported
    "This task is no longer in the list" and changed nothing."""
    v = s.visible()
    assert "no longer in the list" not in v, \
        f"{what} dead-ended on the missing list snapshot (#297 regression):\n{v}"
    assert "Could not set" not in v, f"{what} failed:\n{v}"


def run_opens_and_returns():
    """Leg A: Ctrl+U opens Quick Updates in single-task mode (it used to flash a deferral), and Esc pops
    back to the detail rather than quitting the tab."""
    s = Session()
    try:
        boot_single_task(s)
        v = s.visible()
        assert "isn't available in single-task mode" not in v, \
            f"the pre-#297 deferral flash is still wired:\n{v}"

        open_quick_updates(s, ROOT_NAME)
        assert "isn't available in single-task mode" not in s.visible(), \
            f"Ctrl+U flashed the deferral instead of opening:\n{s.visible()}"

        s.send(ESC, 2.0)
        v = s.visible()
        assert qu_title(ROOT_NAME) not in v, f"Esc did not close Quick Updates:\n{v}"
        assert "Description" in v and ROOT_NAME in v, \
            f"Esc from Quick Updates did not return to the launch task's detail:\n{v}"
        assert "Confirm exit" not in v, \
            f"Esc from Quick Updates leaked through to the exit confirmation:\n{v}"
        print("ok — Ctrl+U opens Quick Updates for the launch task; Esc returns to its detail")
    finally:
        s.close()


def run_commits_with_no_list():
    """Leg B: Status and Priority both commit with no main-list snapshot present — the screen stays open
    (#207) and the footer reports a server-confirmed value, never #297's "no longer in the list"."""
    s = Session()
    try:
        boot_single_task(s)
        open_quick_updates(s, ROOT_NAME)

        for _ in range(5):            # move within the Status pane (clamps at the last row)
            s.send(DOWN, 0.3)
        s.send(ENTER, 0.4)            # Enter applies; #207 keeps the screen open
        assert s.wait_for("Set status to"), \
            f"the status commit never reported a confirmed value:\n{s.visible()}"
        assert_no_snapshot_deadend(s, "the status commit")
        assert qu_title(ROOT_NAME) in s.visible(), \
            f"Quick Updates should stay open after apply-on-Enter (#207):\n{s.visible()}"

        s.send(TAB, 0.5)              # Status -> Priority
        # The pane preselects the task's current level, and the fixture task has none — i.e. the last row,
        # "(no priority)". Move UP to land on a *different* level, since the screen drops an unchanged
        # commit ("Priority unchanged.") rather than writing it.
        s.send(UP, 0.3)
        s.send(ENTER, 0.4)
        assert s.wait_for("Set priority to"), \
            f"the priority commit never reported a confirmed value:\n{s.visible()}"
        assert_no_snapshot_deadend(s, "the priority commit")

        s.send(ESC, 2.0)
        assert "Description" in s.visible(), \
            f"Esc after apply-on-Enter did not return to the detail:\n{s.visible()}"
        print("ok — Status and Priority commit from a single-task tab with no list snapshot")
    finally:
        s.close()


def run_tree_targets_the_highlighted_node():
    """Leg C: on the Task Tree tab, Ctrl+U targets the highlighted node (CHILDTWO) — not the open task —
    and committing against it leaves the detail header on the launch task."""
    s = Session()
    try:
        boot_single_task(s)
        open_tree_tab(s)
        # Move to the bottom row (the ListView clamps there) — deterministically CHILDTWO, a non-current
        # node — regardless of the initial cursor, as single_task_tree_rename_check.py does for F2.
        for _ in range(4):
            s.send(DOWN, 0.2)
        assert CHILDTWO_NAME in s.visible(), f"CHILDTWO row not present to target:\n{s.visible()}"

        # Both surfaces the commit must (and must not) move, captured before it lands. The fixture serves
        # every node as "in progress", so both read `(IP)` / `○ in progress` up front.
        before_header = header_status(s)
        assert before_header is not None, f"could not find the detail header's status line:\n{s.visible()}"
        assert "in progress" in before_header, f"unexpected seeded header status: {before_header!r}"
        assert "(IP)" in (tree_row(s, CHILDTWO_NAME) or ""), \
            f"unexpected seeded badge on the CHILDTWO row:\n{tree_row(s, CHILDTWO_NAME)!r}"

        v = open_quick_updates(s, CHILDTWO_NAME)
        assert qu_title(ROOT_NAME) not in v, \
            f"Ctrl+U on the tree tab targeted the open task instead of the highlighted node:\n{v}"

        for _ in range(5):            # clamps on the last Status row, "complete"
            s.send(DOWN, 0.3)
        s.send(ENTER, 0.4)
        assert s.wait_for("Set status to"), \
            f"the tree-targeted status commit never reported a confirmed value:\n{s.visible()}"
        assert_no_snapshot_deadend(s, "the tree-targeted status commit")

        s.send(ESC, 2.0)
        v = s.visible()
        assert qu_title(CHILDTWO_NAME) not in v, f"Esc did not close Quick Updates:\n{v}"

        # The targeted node's own row was repainted — the whole point of the reflecting write target,
        # since the tree loads once per screen and never re-fetches. "complete" abbreviates to `(C )`
        # (the optimistic value) and the backend's canned echo "in review" to `(IR)`; either proves the
        # row followed the commit, and `(IP)` alone proves it did not.
        row = tree_row(s, CHILDTWO_NAME)
        assert row is not None, f"the CHILDTWO row vanished after the commit:\n{v}"
        assert "(IP)" not in row, f"the targeted tree row was never repainted by the commit:\n{row!r}"
        assert "(C )" in row or "(IR)" in row, f"unexpected badge on the repainted row:\n{row!r}"
        # Its siblings are untouched — the reflection is one row, not a re-render of the tree.
        assert "(IP)" in (tree_row(s, "CHILDONE") or ""), \
            f"a sibling row was wrongly repainted:\n{tree_row(s, 'CHILDONE')!r}"

        # And the header — which follows the LAUNCH task, not the targeted node — did not move. Asserting
        # the status text, not just the title: a `reflect` passed unconditionally would repaint the status
        # here while leaving the title alone, which a title-only assertion could never catch.
        assert ROOT_NAME in v, f"the detail header lost the launch task's title:\n{v}"
        after_header = header_status(s)
        assert after_header == before_header, \
            f"a tree-targeted commit repainted the launch task's header status: " \
            f"{before_header!r} -> {after_header!r}"
        print("ok — Task Tree Ctrl+U targets the highlighted node, repaints its row, header untouched")
    finally:
        s.close()


def run_dashboard_tree_parity():
    """Leg D: the same tree-tab targeting in the dashboard host — the gesture lives in TaskDetailScreen,
    so the two hosts share it."""
    s = Session(single_task=False)
    try:
        boot_dashboard(s)
        open_tree_tab(s)
        for _ in range(4):
            s.send(DOWN, 0.2)
        assert CHILDTWO_NAME in s.visible(), f"CHILDTWO row not present to target:\n{s.visible()}"

        v = open_quick_updates(s, CHILDTWO_NAME)
        assert qu_title(ROOT_NAME) not in v, \
            f"dashboard Ctrl+U on the tree tab targeted the open task, not the node:\n{v}"
        print("ok — the dashboard's Task Tree Ctrl+U targets the highlighted node too (no host drift)")
    finally:
        s.close()


run_opens_and_returns()
run_commits_with_no_list()
run_tree_targets_the_highlighted_node()
run_dashboard_tree_parity()
print("SINGLE-TASK QUICK UPDATES E2E: PASS")
