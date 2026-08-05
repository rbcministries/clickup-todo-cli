#!/usr/bin/env python3
"""Drives the Task Detail "Checklists" tab (C, #456) end-to-end against the in-process fake backend.

Populated leg (E2E_CHECKLISTS=1 seeds the opened task with two checklist groups):

    Release steps  (1/3)
      [x] Cut the tag
      [ ] Draft release notes — Ada Lovelace
        [ ] Verify the changelog          ← nested one level under "Draft release notes"
    QA signoff  (1/2)
      [x] Smoke test on staging
      [ ] Cross-browser check

Asserts:
  1. Cycling to the Checklists tab (index 4, after Other, before the Task Tree tab) renders both
     group headers with their resolved/total progress, every item with a [x]/[ ] glyph, the nested
     item indented under its parent, and the assignee suffix on the one assigned item — proving the
     pure ChecklistArranger projection + ChecklistTabModel rendering drove real rows.
  2. The tab title carries the aggregate progress "Checklists (2/5)".
  3. Bare ↑/↓ move the selection within the tab (given #452) and never switch away from it — the
     NavSafe boundary contract (pairs with tab_boundary_check.py): after driving arrows the tab
     content and title are unchanged.

Empty leg (no E2E_CHECKLISTS): the Checklists tab of a checklist-free task shows the single
explanatory empty-state row and a bare "Checklists" title (no progress)."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 40, 120
DLL = sys.argv[1]

CTRL_RIGHT = b"\x1b[1;5C"
DOWN = b"\x1b[B"
UP = b"\x1b[A"
ENTER = b"\r"


class Session:
    def __init__(self, extra_env):
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        env = dict(os.environ, TERM="xterm-256color", E2E_TASKS="6", **extra_env)
        self.proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                                     env=env, close_fds=True, preexec_fn=os.setsid)
        os.close(slave)

    def _answer(self, data):
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
                self._answer(chunk)
                self.stream.feed(chunk)

    def visible(self):
        return "\n".join(self.screen.display[y].rstrip() for y in range(ROWS))

    def send(self, data):
        os.write(self.master, data)

    def open_checklists_tab(self):
        """From the main list: open t0's detail and cycle to the Checklists tab (index 4)."""
        self.send(ENTER)          # open the focused task (t0) in Task Detail
        self.pump(3.0)
        for _ in range(4):        # Stream -> Description -> Comments -> Other -> Checklists
            self.send(CTRL_RIGHT)
            self.pump(0.4)
        self.pump(1.0)

    def kill(self):
        try:
            os.killpg(os.getpgid(self.proc.pid), signal.SIGKILL)
        except Exception:
            pass

    def glyph_col(self, substr):
        """The screen column of the '[' checkbox glyph on the first row containing substr, or -1. A more
        deeply-nested item's glyph sits further right (ChecklistTabModel indents two spaces per level), so
        comparing two rows' glyph columns is a robust, border-agnostic nesting assertion."""
        for y in range(ROWS):
            line = self.screen.display[y]
            if substr in line:
                return line.find("[")
        return -1


def run_populated():
    s = Session({"E2E_CHECKLISTS": "1"})
    try:
        s.pump(8.0)
        assert "Task 0" in s.visible(), "list boot failed:\n" + s.visible()

        s.open_checklists_tab()
        v = s.visible()

        # ── 1) Both group headers with progress + every item glyph + assignee suffix ──────────────
        assert "Release steps" in v, "checklist group header 'Release steps' missing:\n" + v
        assert "(1/3)" in v, "'Release steps' header progress (1/3) missing:\n" + v
        assert "QA signoff" in v, "checklist group header 'QA signoff' missing:\n" + v
        assert "(1/2)" in v, "'QA signoff' header progress (1/2) missing:\n" + v

        assert "[x] Cut the tag" in v, "resolved item glyph/text missing:\n" + v
        assert "[ ] Draft release notes" in v, "unresolved item glyph/text missing:\n" + v
        assert "Ada Lovelace" in v, "assignee suffix missing on the assigned item:\n" + v
        assert "[ ] Verify the changelog" in v, "nested item glyph/text missing:\n" + v
        assert "[x] Smoke test on staging" in v, "second-group resolved item missing:\n" + v
        assert "[ ] Cross-browser check" in v, "second-group unresolved item missing:\n" + v

        # Nesting: the child "Verify the changelog" is indented deeper than its parent "Draft release…"
        # (its checkbox glyph sits further right).
        parent_col = s.glyph_col("Draft release notes")
        child_col = s.glyph_col("Verify the changelog")
        assert parent_col >= 0 and child_col > parent_col, \
            f"nested item not indented under its parent (parent glyph col={parent_col}, child={child_col}):\n" + v

        # ── 2) Tab title carries aggregate progress ───────────────────────────────────────────────
        assert "Checklists (2/5)" in v, "tab title did not show aggregate progress 'Checklists (2/5)':\n" + v

        # ── 3) Bare ↑/↓ move the selection within the tab and never switch away from it (#452) ─────
        for _ in range(3):
            s.send(DOWN)
            s.pump(0.2)
        for _ in range(6):        # well past the top — a boundary no-op, never a tab switch/crash
            s.send(UP)
            s.pump(0.2)
        assert s.proc.poll() is None, "arrows crashed the process on the Checklists tab"
        after = s.visible()
        assert "Release steps" in after and "Checklists (2/5)" in after, \
            "bare arrows switched away from the Checklists tab (NavSafe boundary broken):\n" + after
        print("ok — populated: two groups render with progress/glyphs/nesting/assignee; "
              "title 'Checklists (2/5)'; bare ↑/↓ stay on the tab")
    finally:
        s.kill()


def run_empty():
    s = Session({})              # no E2E_CHECKLISTS → the opened task has no checklists
    try:
        s.pump(8.0)
        assert "Task 0" in s.visible(), "list boot failed:\n" + s.visible()
        s.open_checklists_tab()
        v = s.visible()
        assert "No checklists on this task." in v, "empty-state row missing on a checklist-free task:\n" + v
        assert "Checklists" in v, "Checklists tab title missing on the empty leg:\n" + v
        assert "(0/0)" not in v and "Checklists (" not in v, \
            "empty leg should show a bare 'Checklists' title, no progress parens:\n" + v
        print("ok — empty: a checklist-free task shows the empty-state row and a bare 'Checklists' title")
    finally:
        s.kill()


run_populated()
run_empty()
