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
SHIFT_UP = b"\x1b[1;2A"     # move checklist item up (G, #569)
SHIFT_DOWN = b"\x1b[1;2B"   # move checklist item down
SHIFT_RIGHT = b"\x1b[1;2C"  # indent under the preceding sibling
SHIFT_LEFT = b"\x1b[1;2D"   # outdent to the grandparent
ENTER = b"\r"
SPACE = b" "
CTRL_R = b"\x12"          # refresh (alias of F5) — re-fetches detail from the fake backend
CTRL_G = b"\x07"          # new checklist group (F, #459)
CTRL_N = b"\x0e"           # add checklist item on the Checklists tab (C, #540; ASCII 14, retired F7)
F2 = b"\x1bOQ"            # rename the selected item / group (row-kind-scoped; D, #541 — retired F8)
BACKSPACE = b"\x7f"
DELETE = b"\x1b[3~"       # forward-delete (paired with BACKSPACE to clear a field caret-agnostically)
TAB = b"\x09"            # cycle overlay focus (name field → assignee picker → Save → Cancel), #572


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

    def row_of(self, substr):
        """The screen row (y) of the first line containing substr, or -1 — for order assertions."""
        for y in range(ROWS):
            if substr in self.screen.display[y]:
                return y
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
    """E (#458): Ctrl+N add → F2 rename → Delete delete, an item CRUD round-trip that returns the checklist to
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

        # ── Add (Ctrl+N): open the name overlay, type a name, Enter ───────────────────────────────────────
        s.send(CTRL_N)
        s.pump(0.8)
        assert "New item" in s.visible(), "Ctrl+N did not open the add-item overlay:\n" + s.visible()
        type_text(s, "Ship the release")
        s.send(ENTER)
        s.pump(1.5)
        v = s.visible()
        assert s.proc.poll() is None, "add crashed the process"
        assert "[ ] Ship the release" in v, "the new item did not appear after add:\n" + v
        assert "Checklists (2/6)" in v, "aggregate did not grow to (2/6) after add:\n" + v
        assert "Release steps  (1/4)" in v, "'Release steps' progress did not grow to (1/4) after add:\n" + v

        # ── Rename (F2): the new item landed selected; clear the prefilled name and type a new one ─────
        s.send(F2)
        s.pump(0.8)
        assert "Edit item" in s.visible(), "F2 did not open the edit overlay:\n" + s.visible()
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

        # ── Delete (Delete → Enter): confirm and remove; the checklist returns to its starting counts ──
        # Delete retargeted from #458's F9 (contextual chords F, #543); the default build arms the inline
        # Enter/Esc confirm (the native ConfirmDialog is behind CLICKUP_TODO_NATIVE_MODAL, off here).
        s.send(DELETE)
        s.pump(0.8)
        assert "Delete" in s.visible(), "Delete did not arm the delete confirm:\n" + s.visible()
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

        print("ok — CRUD: Ctrl+N adds an item (2/6), F2 renames it, Delete+Enter deletes it back to (2/5); persists over refresh")
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
        s.send(CTRL_N)
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


def run_delete_confirm_cleared_by_overlay():
    """E (#458) regression: arming the delete confirm (Delete, retargeted from F9 in #543) then opening the
    add overlay (Ctrl+N) must cancel the armed delete, so a later Enter can't silently delete the once-
    targeted item. (Default build — the inline confirm; the native ConfirmDialog is behind the flag.)"""
    s = Session({"E2E_CHECKLISTS": "1"})
    try:
        s.pump(8.0)
        assert "Task 0" in s.visible(), "list boot failed:\n" + s.visible()
        s.open_checklists_tab()
        # Select an item row: from the top header, one DOWN lands on "[x] Cut the tag".
        for _ in range(8):
            s.send(UP)
            s.pump(0.1)
        s.send(DOWN)
        s.pump(0.3)

        s.send(DELETE)            # arm the delete confirm for the selected item (Delete, was F9)
        s.pump(0.6)
        assert "Delete" in s.visible(), "Delete did not arm the delete confirm:\n" + s.visible()
        s.send(CTRL_N)                # open the add overlay — this must cancel the armed delete
        s.pump(0.6)
        s.send(b"\x1b")           # Esc: cancel the add overlay (creates nothing)
        s.pump(0.6)
        s.send(ENTER)             # a now-stray Enter must NOT delete anything
        s.pump(1.0)
        v = s.visible()
        assert "[x] Cut the tag" in v, "opening the add overlay left a delete armed — Enter deleted an item:\n" + v
        assert "Checklists (2/5)" in v, "an item was unexpectedly deleted (aggregate changed):\n" + v
        print("ok — delete-confirm cleared: Delete then Ctrl+N cancels the armed delete; a later Enter is inert")
    finally:
        s.kill()


def run_group_crud():
    """F (#459): checklist GROUP CRUD on a task that starts with no checklists (E2E_CHECKLISTS_EMPTY seeds
    the mutable DOM empty). Ctrl+G create a group -> Ctrl+N add an item to it -> Delete+Enter on its header
    delete the group, returning to the empty state. Each write persists in the fake backend, so a refresh agrees."""
    s = Session({"E2E_CHECKLISTS_EMPTY": "1"})
    try:
        s.pump(8.0)
        assert "Task 0" in s.visible(), "list boot failed:\n" + s.visible()
        s.open_checklists_tab()
        v = s.visible()
        assert "No checklists on this task." in v, "F leg should start on the empty state:\n" + v

        # ── Create a group (Ctrl+G): open the name overlay, type a name, Enter ────────────────────────
        s.send(CTRL_G)
        s.pump(0.8)
        assert "New checklist" in s.visible(), "Ctrl+G did not open the new-checklist overlay:\n" + s.visible()
        type_text(s, "Release steps")
        s.send(ENTER)
        s.pump(1.5)
        v = s.visible()
        assert s.proc.poll() is None, "group create crashed the process"
        assert "Release steps" in v, "the new checklist header did not appear:\n" + v
        assert "No checklists on this task." not in v, "empty state should be gone after create:\n" + v
        assert "(0/0)" in v, "the new empty checklist header should show (0/0):\n" + v

        # ── Add an item to it (Ctrl+N): the new group's header is selected, so Ctrl+N adds into that group ─────
        s.send(CTRL_N)
        s.pump(0.8)
        assert "New item" in s.visible(), "Ctrl+N did not open the add-item overlay on the new group:\n" + s.visible()
        type_text(s, "Ship it")
        s.send(ENTER)
        s.pump(1.5)
        v = s.visible()
        assert "[ ] Ship it" in v, "the item was not added to the new group:\n" + v
        assert "Release steps  (0/1)" in v, "the group progress did not grow to (0/1):\n" + v
        assert "Checklists (0/1)" in v, "the tab title aggregate did not update to (0/1):\n" + v

        # ── Delete the group (Delete on its header -> Enter): the item added lands selected, so ↑ to the header
        s.send(UP)
        s.pump(0.3)
        s.send(DELETE)
        s.pump(0.8)
        v = s.visible()
        assert "Delete checklist 'Release steps'" in v, "Delete on the header did not arm the group-delete confirm:\n" + v
        assert "1 item" in v, "the group-delete confirm did not name the item count:\n" + v
        s.send(ENTER)            # Enter confirms (a bare letter would be eaten by the ListView type-ahead)
        s.pump(1.5)
        v = s.visible()
        assert s.proc.poll() is None, "group delete crashed the process"
        assert "No checklists on this task." in v, "deleting the only group did not return to the empty state:\n" + v
        assert "Release steps" not in v, "the deleted group is still shown:\n" + v

        # ── Persists across a refresh: the fake persisted create+delete, so Ctrl+R stays empty ─────────
        s.send(CTRL_R)
        s.pump(2.5)
        v = s.visible()
        assert "No checklists on this task." in v and "Release steps" not in v, \
            "a refresh resurrected the deleted group:\n" + v

        print("ok — group CRUD: Ctrl+G creates a checklist, Ctrl+N adds an item (0/1), Delete+Enter on the header "
              "deletes the group back to the empty state; persists over refresh")
    finally:
        s.kill()


def run_group_rename():
    """F (#459): F2 on a checklist header renames the GROUP (not an item), optimistic + persisted."""
    s = Session({"E2E_CHECKLISTS": "1"})
    try:
        s.pump(8.0)
        assert "Task 0" in s.visible(), "list boot failed:\n" + s.visible()
        s.open_checklists_tab()
        # Normalise to the top row (the "Release steps" header).
        for _ in range(8):
            s.send(UP)
            s.pump(0.1)
        s.pump(0.3)
        assert "Release steps" in s.visible(), "precondition: 'Release steps' header present:\n" + s.visible()

        s.send(F2)               # F2 on a header opens the rename-checklist overlay
        s.pump(0.8)
        assert "Edit checklist" in s.visible(), "F2 on a header did not open the edit-group overlay:\n" + s.visible()
        for _ in range(24):      # clear the prefilled "Release steps"
            s.send(BACKSPACE)
            s.send(DELETE)
        s.pump(0.4)
        type_text(s, "Release plan")
        s.send(ENTER)
        s.pump(1.5)
        v = s.visible()
        assert s.proc.poll() is None, "group rename crashed the process"
        assert "Release plan" in v, "the checklist was not renamed:\n" + v
        assert "Release steps" not in v, "the pre-rename checklist name is still shown:\n" + v
        assert "(1/3)" in v, "renaming the group wrongly changed its item progress:\n" + v

        s.send(CTRL_R)           # persisted: the rename survives a refresh
        s.pump(2.5)
        v = s.visible()
        assert "Release plan" in v and "Release steps" not in v, \
            "a refresh resurrected the pre-rename checklist name:\n" + v
        print("ok — group rename: F2 on a header renames the checklist (persists over refresh) without "
              "disturbing its items")
    finally:
        s.kill()


def run_assignee():
    """G (#460 / #572): the rename overlay's assignee picker sets and clears a checklist item's assignee.
    It reuses the shared AssigneeSelectorView in ImmediateApply, so a pick writes immediately, the row's
    assignee suffix updates live, and both the set and the clear persist across a refresh.

    Row layout (E2E_CHECKLISTS=1):
        0  Release steps  (1/3)                     ← header
        1  [x] Cut the tag                          ← unassigned (i1)
        2  [ ] Draft release notes — Ada Lovelace   ← assigned (i2)
        ...
    """
    s = Session({"E2E_CHECKLISTS": "1"})
    try:
        s.pump(8.0)
        assert "Task 0" in s.visible(), "list boot failed:\n" + s.visible()
        s.open_checklists_tab()

        # Select "[x] Cut the tag" (row 1): normalise to the top header, then one DOWN.
        for _ in range(8):
            s.send(UP)
            s.pump(0.1)
        s.send(DOWN)
        s.pump(0.3)
        assert "[x] Cut the tag" in s.visible(), "precondition: 'Cut the tag' selected:\n" + s.visible()

        # ── Set: F2 opens the rename overlay, now carrying the assignee picker ─────────────────────────
        s.send(F2)
        s.pump(0.9)
        v = s.visible()
        assert "Edit item" in v, "F2 did not open the edit overlay:\n" + v
        assert "Assignee:" in v, "the rename overlay did not show the assignee picker (#572):\n" + v

        s.send(TAB)               # name field → the assignee picker's search box
        s.pump(0.5)
        type_text(s, "Grace")     # type-ahead over the frequency-ranked member pool
        # Wait out the ~1s type-ahead debounce so the list COLLAPSES to the match before Enter — otherwise
        # Enter picks the still-highlighted top-frequent empty-state row (row 0) mid-debounce, not "Grace".
        s.pump(1.6)
        assert "Grace Hopper" in s.visible(), "typing 'Grace' did not surface the candidate:\n" + s.visible()
        s.send(ENTER)             # pick the sole narrowed row (Grace Hopper) → ImmediateApply set-assignee write
        s.pump(1.3)
        s.send(b"\x1b")           # Esc closes the overlay (the name is unchanged, so it just closes)
        s.pump(1.0)
        v = s.visible()
        assert s.proc.poll() is None, "assigning crashed the process"
        assert "Cut the tag" in v and "Grace Hopper" in v, \
            "the assignee suffix did not appear on the row after assigning:\n" + v

        # ── Persists across a refresh (the fake persisted the assignee) ────────────────────────────────
        s.send(CTRL_R)
        s.pump(2.5)
        v = s.visible()
        assert "Cut the tag" in v and "Grace Hopper" in v, \
            "the assignee did not persist across a refresh:\n" + v

        # ── Clear: reopen on the same item (still selected); the picker seeds with Grace as a ✓ row.
        #    Into the list (Down), Enter on the current ✓ row removes it → clear (writes null). ──────────
        s.send(F2)
        s.pump(0.9)
        assert "Assignee:" in s.visible(), "reopening the rename overlay did not show the picker:\n" + s.visible()
        s.send(TAB)               # into the picker's search box
        s.pump(0.5)
        s.send(DOWN)              # into the list — the seeded ✓ Grace row sits first in the empty state
        s.pump(0.5)
        s.send(ENTER)             # remove the current assignee → clear
        s.pump(1.3)
        s.send(b"\x1b")           # close
        s.pump(1.0)
        v = s.visible()
        assert s.proc.poll() is None, "clearing crashed the process"
        assert "Grace Hopper" not in v, "clearing did not remove the assignee suffix from the row:\n" + v

        s.send(CTRL_R)
        s.pump(2.5)
        v = s.visible()
        assert "Grace Hopper" not in v, "the cleared assignee resurrected across a refresh:\n" + v

        print("ok — assignee: the rename overlay assigns 'Grace Hopper' to an item (live suffix + persistence) "
              "and clears it back, all from the shared AssigneeSelectorView (#572)")
    finally:
        s.kill()


def run_move():
    """G (#569): Shift+↓ reorders an item past its sibling; Shift+→ indents an item under its preceding
    sibling; Shift+↑ on the first item is a boundary no-op that stays on the tab. Each move persists in the
    fake backend, so a refresh agrees.

    Starting layout (E2E_CHECKLISTS=1):
        0  Release steps  (1/3)
        1  [x] Cut the tag
        2  [ ] Draft release notes — Ada Lovelace
        3    [ ] Verify the changelog
        4  QA signoff  (1/2)
        5  [x] Smoke test on staging
        6  [ ] Cross-browser check
    """
    s = Session({"E2E_CHECKLISTS": "1"})
    try:
        s.pump(8.0)
        assert "Task 0" in s.visible(), "list boot failed:\n" + s.visible()
        s.open_checklists_tab()

        # Normalise to the top header, then DOWN once → row 1 "[x] Cut the tag" (the first item).
        for _ in range(8):
            s.send(UP)
            s.pump(0.1)
        s.send(DOWN)
        s.pump(0.3)

        # ── Boundary no-op: Shift+↑ on the first item can't move up — stays on the tab, no reorder ───────
        assert s.row_of("Cut the tag") < s.row_of("Draft release notes"), \
            "precondition: 'Cut the tag' should start above 'Draft release notes':\n" + s.visible()
        s.send(SHIFT_UP)
        s.pump(0.8)
        assert s.proc.poll() is None, "Shift+Up on the first item crashed the process"
        v = s.visible()
        assert "Checklists (2/5)" in v and s.row_of("Cut the tag") < s.row_of("Draft release notes"), \
            "Shift+Up on the first item wrongly reordered or switched tabs:\n" + v

        # ── Shift+↓ moves "Cut the tag" below "Draft release notes" ─────────────────────────────────────
        s.send(SHIFT_DOWN)
        s.pump(1.5)
        v = s.visible()
        assert s.proc.poll() is None, "Shift+Down crashed the process"
        assert s.row_of("Draft release notes") < s.row_of("Cut the tag"), \
            "Shift+Down did not reorder 'Cut the tag' below 'Draft release notes':\n" + v
        assert "Checklists (2/5)" in v, "reorder wrongly changed the aggregate:\n" + v

        # Persists across a refresh (the fake applied the orderindex write; the echo agrees).
        s.send(CTRL_R)
        s.pump(2.5)
        v = s.visible()
        assert s.row_of("Draft release notes") < s.row_of("Cut the tag"), \
            "a refresh reverted the reorder:\n" + v

        # ── Shift+→ indents "Cross-browser check" under "Smoke test on staging" (its preceding sibling) ──
        smoke_col = s.glyph_col("Smoke test on staging")
        assert s.glyph_col("Cross-browser check") == smoke_col, \
            "precondition: 'Cross-browser check' should start at top level (same glyph col as its sibling):\n" + s.visible()
        for _ in range(15):       # drive the selection to the last row ("Cross-browser check"); clamps there
            s.send(DOWN)
            s.pump(0.06)
        s.send(SHIFT_RIGHT)
        s.pump(1.5)
        v = s.visible()
        assert s.proc.poll() is None, "Shift+Right crashed the process"
        after_col = s.glyph_col("Cross-browser check")
        assert after_col > smoke_col, \
            f"Shift+Right did not indent 'Cross-browser check' under 'Smoke test' (glyph col {after_col} vs sibling {smoke_col}):\n" + v
        assert "Checklists (2/5)" in v, "indent wrongly changed the aggregate:\n" + v

        # Persists across a refresh (the fake reparented it under the sibling's children).
        s.send(CTRL_R)
        s.pump(2.5)
        assert s.glyph_col("Cross-browser check") > s.glyph_col("Smoke test on staging"), \
            "a refresh reverted the indent:\n" + s.visible()

        print("ok — move: Shift+↑ on the first item is an inert boundary no-op; Shift+↓ reorders an item past "
              "its sibling and Shift+→ indents one under its preceding sibling; both persist over a refresh")
    finally:
        s.kill()


run_populated()
run_empty()
run_toggle()
run_crud()
run_add_cancel()
run_delete_confirm_cleared_by_overlay()
run_group_crud()
run_group_rename()
run_assignee()
run_move()
