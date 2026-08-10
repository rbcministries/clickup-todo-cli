#!/usr/bin/env python3
"""Drives the Task Detail "Checklists" tab (C, #456) end-to-end against the in-process fake backend.

Populated leg (E2E_CHECKLISTS=1 seeds the opened task with two checklist groups). Headers and
top-level items render flush-left; only a nested item is indented (two spaces per level):

    Release steps  (1/3)
    [x] Cut the tag
    [ ] Draft release notes — Ada Lovelace
      [ ] Verify the changelog            ← nested one level under "Draft release notes"
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
SPACE = b" "
CTRL_R = b"\x12"          # refresh (alias of F5) — re-fetches detail from the fake backend
F7 = b"\x1b[18~"          # add checklist item (E, #458)
F8 = b"\x1b[19~"          # rename the selected checklist item
F9 = b"\x1b[20~"          # delete the selected checklist item
BACKSPACE = b"\x7f"
DELETE = b"\x1b[3~"       # forward-delete (paired with BACKSPACE to clear a field caret-agnostically)


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


def run_toggle():
    """D (#457): Space on an item row toggles resolved (optimistic + persisted); Space on a header is inert.

    Row layout on the Checklists tab (E2E_CHECKLISTS=1):
        0  Release steps  (1/3)          ← header
        1  [x] Cut the tag
        2  [ ] Draft release notes — Ada Lovelace
        3    [ ] Verify the changelog    ← nested under row 2
        4  QA signoff  (1/2)             ← header
        5  [x] Smoke test on staging
        6  [ ] Cross-browser check
    """
    s = Session({"E2E_CHECKLISTS": "1"})
    try:
        s.pump(8.0)
        assert "Task 0" in s.visible(), "list boot failed:\n" + s.visible()
        s.open_checklists_tab()

        # Normalise the selection to the top row (row 0 = the "Release steps" header). Bare ↑ past the top
        # is a boundary no-op that stays on the tab.
        for _ in range(8):
            s.send(UP)
            s.pump(0.1)
        s.pump(0.3)

        # ── Header row is inert: Space on "Release steps" changes no glyph and no progress ────────────
        before = s.visible()
        assert "Checklists (2/5)" in before, "precondition: aggregate should start at (2/5):\n" + before
        s.send(SPACE)
        s.pump(0.8)
        v = s.visible()
        assert s.proc.poll() is None, "Space on a header crashed the process"
        assert "Checklists (2/5)" in v, "Space on a header row wrongly changed the aggregate:\n" + v
        assert "[ ] Cross-browser check" in v, "Space on a header wrongly toggled an item:\n" + v

        # ── Toggle an item: move to "[ ] Cross-browser check" (row 6) and press Space ────────────────
        for _ in range(6):        # row 0 (header) → … → row 6 (Cross-browser check)
            s.send(DOWN)
            s.pump(0.15)
        s.send(SPACE)
        s.pump(1.2)
        v = s.visible()
        assert "[x] Cross-browser check" in v, "Space did not tick the item ([ ]→[x]):\n" + v
        assert "(2/2)" in v, "'QA signoff' group progress did not update to (2/2):\n" + v
        assert "Checklists (3/5)" in v, "tab-title aggregate did not update to (3/5):\n" + v
        assert "[x] Smoke test on staging" in v, "a sibling item wrongly changed:\n" + v

        # ── Persists across a refresh: the fake persisted the toggle, so Ctrl+R must not resurrect the
        #    stale [ ] / (2/5) state (the in-flight overlay + the mutated backend agree). ───────────────
        s.send(CTRL_R)
        s.pump(2.5)
        v = s.visible()
        assert "[x] Cross-browser check" in v and "Checklists (3/5)" in v, \
            "a refresh after the toggle resurrected the stale resolved state:\n" + v

        print("ok — toggle: Space on a header is inert; Space on an item flips [ ]→[x], "
              "updates group (2/2) + title (3/5), and survives a refresh")
    finally:
        s.kill()


def type_text(s, text):
    s.send(text.encode())
    s.pump(0.4)


def run_crud():
    """E (#458): F7 add → F8 rename → F9 delete, an item CRUD round-trip that returns the checklist to
    its starting counts, driven against the mutable fake backend (which persists each write).

    Starting layout / aggregate (E2E_CHECKLISTS=1): Release steps (1/3), QA signoff (1/2) → (2/5).
    """
    s = Session({"E2E_CHECKLISTS": "1"})
    try:
        s.pump(8.0)
        assert "Task 0" in s.visible(), "list boot failed:\n" + s.visible()
        s.open_checklists_tab()

        # Normalise the selection to the top row (row 0 = the "Release steps" header). A new item added
        # while a Release-steps row is selected joins that checklist.
        for _ in range(8):
            s.send(UP)
            s.pump(0.1)
        s.pump(0.3)
        assert "Checklists (2/5)" in s.visible(), "precondition: aggregate should start at (2/5):\n" + s.visible()

        # ── Add (F7): open the name overlay, type a name, Enter ───────────────────────────────────────
        s.send(F7)
        s.pump(0.8)
        assert "New item" in s.visible(), "F7 did not open the add-item overlay:\n" + s.visible()
        type_text(s, "Ship the release")
        s.send(ENTER)
        s.pump(1.5)
        v = s.visible()
        assert s.proc.poll() is None, "add crashed the process"
        assert "[ ] Ship the release" in v, "the new item did not appear after add:\n" + v
        assert "Checklists (2/6)" in v, "aggregate did not grow to (2/6) after add:\n" + v
        assert "Release steps  (1/4)" in v, "'Release steps' progress did not grow to (1/4) after add:\n" + v

        # ── Rename (F8): the new item landed selected; clear the prefilled name and type a new one ─────
        s.send(F8)
        s.pump(0.8)
        assert "Rename item" in s.visible(), "F8 did not open the rename overlay:\n" + s.visible()
        for _ in range(24):        # clear the prefilled "Ship the release" regardless of caret position
            s.send(BACKSPACE)
            s.send(DELETE)
        s.pump(0.4)
        type_text(s, "Publish v2")
        s.send(ENTER)
        s.pump(1.5)
        v = s.visible()
        assert "[ ] Publish v2" in v, "the item was not renamed:\n" + v
        assert "Ship the release" not in v, "the pre-rename name is still shown:\n" + v
        assert "Checklists (2/6)" in v, "rename wrongly changed the aggregate:\n" + v

        # ── Delete (F9 → Y): confirm and remove; the checklist returns to its starting counts ──────────
        s.send(F9)
        s.pump(0.8)
        assert "Delete" in s.visible(), "F9 did not arm the delete confirm:\n" + s.visible()
        s.send(ENTER)             # Enter confirms the delete (a letter would be eaten by the ListView type-ahead)
        s.pump(1.5)
        v = s.visible()
        assert s.proc.poll() is None, "delete crashed the process"
        assert "Publish v2" not in v, "the item was not deleted:\n" + v
        assert "Checklists (2/5)" in v, "aggregate did not return to (2/5) after delete:\n" + v
        assert "Release steps  (1/3)" in v, "'Release steps' progress did not return to (1/3) after delete:\n" + v

        # ── Persists across a refresh: the fake persisted create+rename+delete, so Ctrl+R agrees ───────
        s.send(CTRL_R)
        s.pump(2.5)
        v = s.visible()
        assert "Checklists (2/5)" in v and "Publish v2" not in v, \
            "a refresh resurrected the deleted item or wrong counts:\n" + v

        print("ok — CRUD: F7 adds an item (2/6), F8 renames it, F9+Y deletes it back to (2/5); persists over refresh")
    finally:
        s.kill()


def run_add_cancel():
    """E (#458): Esc dismisses the add overlay without creating an item (no write)."""
    s = Session({"E2E_CHECKLISTS": "1"})
    try:
        s.pump(8.0)
        assert "Task 0" in s.visible(), "list boot failed:\n" + s.visible()
        s.open_checklists_tab()
        for _ in range(8):
            s.send(UP)
            s.pump(0.1)
        s.send(F7)
        s.pump(0.8)
        type_text(s, "Should not persist")
        s.send(b"\x1b")           # Esc — add cancels immediately (no discard confirm for a new item)
        s.pump(1.0)
        v = s.visible()
        assert "Should not persist" not in v, "a cancelled add still created the item:\n" + v
        assert "Checklists (2/5)" in v, "a cancelled add changed the aggregate:\n" + v
        print("ok — add cancel: Esc dismisses the overlay and creates nothing")
    finally:
        s.kill()


run_populated()
run_empty()
run_toggle()
run_crud()
run_add_cancel()
