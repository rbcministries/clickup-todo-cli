#!/usr/bin/env python3
"""Comment delete (#594, the deferred comment half of contextual Delete #543): drives deleting a comment
from the Task Detail Comments tab and asserts it disappears from the pane, that the DELETE reached the
backend keyed to the picked comment, and that the cancel and permission-revert legs behave.

The Comments/Stream panes are read-only text (no row-selectable comment), so Delete opens a transient
delete-target picker (a parallel of the reply picker #330) listing the task's deletable comments
newest-first — the pre-selected row is c3, authored by "Alex Kim". Picking never deletes in one touch (the
API delete is not undoable): with the native modal off (the default), picking arms an inline Enter/Esc
confirm, exactly like the checklist delete.

Legs (boot 1, E2E_COMMENT_DELETE_LOG set, no forbid):
  • Delete opens the picker (its "Enter delete" keys shown; "Alex Kim" listed);
  • Esc on the picker closes it, deleting nothing (picker-cancel);
  • Delete → Enter picks c3 → the confirm names "Alex Kim" and offers Enter/Esc; Esc cancels, c3 stays
    (confirm-cancel);
  • Delete → Enter picks c3 → Enter confirms → c3's body disappears from the Comments pane, and the
    backend recorded DELETE /comment/c3.
Leg (boot 2, E2E_COMMENT_DELETE_FORBID=c3): deleting c3 returns 403 (only the author may delete), so the
optimistic removal reverts — c3 reappears and an error flashes.

Asserts on the pyte-rendered screen (never raw bytes); exits nonzero / prints a traceback on failure.
Self-contained (sets its own env)."""
import os, pty, select, struct, sys, termios, fcntl, time, tempfile, subprocess
import pyte

ROWS, COLS = 40, 120
DLL = sys.argv[1]

CTRL_RIGHT = b"\x1b[1;5C"       # Ctrl+→ : next detail tab
DELETE = b"\x1b[3~"            # forward-delete: opens the comment-delete picker on the Comments tab (#594)
ENTER = b"\r"
ESC = b"\x1b"
DOWN = b"\x1b[B"

# A distinctive fragment of c3's body ("@bench can you take a look when you get a chance?") — present while
# c3 exists, absent once it's deleted. The leading "@bench" may render as a mention chip, so key off the tail.
C3_MARK = "take a look when you get a chance"


class Session:
    def __init__(self, extra_env):
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        # E2E_REFRESH high so no background poll re-fetches the flat c1/c2/c3 mid-check (which would
        # resurrect a just-deleted comment once the optimistic overlay clears on a successful write).
        env = dict(os.environ, TERM="xterm-256color", E2E_TASKS="5", E2E_REFRESH="600", **extra_env)
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

    def open_comments_tab(self):
        """From the main list: open t0's detail and cycle Stream → Description → Comments."""
        self.send(ENTER)
        self.pump(3.0)
        assert "Description" in self.visible(), "detail did not open:\n" + self.visible()
        self.send(CTRL_RIGHT); self.pump(0.5)
        self.send(CTRL_RIGHT); self.pump(0.8)

    def open_delete_picker(self):
        """Press Delete until the delete picker is up (comments may still be loading on a cold boot)."""
        for _ in range(10):
            self.send(DELETE)
            self.pump(1.0)
            v = self.visible()
            if "Enter delete" in v or "Delete comment" in v:
                return v
        raise AssertionError("delete picker did not open on Delete:\n" + self.visible())

    def scrolled_lines(self, presses=30):
        """Scroll the pane, accumulating every visible line so a comment is found wherever it sits."""
        seen = set(self.visible().split("\n"))
        for _ in range(presses):
            self.send(DOWN); self.pump(0.08)
            seen.update(self.visible().split("\n"))
        return "\n".join(sorted(seen))

    def kill(self):
        try:
            os.killpg(os.getpgid(self.proc.pid), 15)
        except ProcessLookupError:
            pass
        try:
            os.close(self.master)
        except OSError:
            pass


def run_happy_and_cancel(log):
    s = Session(dict(E2E_COMMENT_DELETE_LOG=log))
    try:
        s.pump(8.0)
        assert "Task" in s.visible(), "list boot failed"
        s.open_comments_tab()

        # ── Delete opens the picker; it lists the deletable comments (c3 / Alex Kim pre-selected) ──
        picker = s.open_delete_picker()
        assert "Alex Kim" in picker, "delete picker did not list the comments (Alex Kim absent):\n" + picker

        # ── Esc on the picker closes it, deleting nothing ──
        s.send(ESC); s.pump(0.8)
        assert "Delete comment" not in s.visible(), "Esc did not close the delete picker:\n" + s.visible()

        # ── Pick c3 → the confirm names the author and offers Enter/Esc; Esc cancels ──
        s.open_delete_picker()
        s.send(ENTER); s.pump(1.0)          # pick the pre-selected newest comment (c3 / Alex Kim)
        armed = s.visible()
        assert "Alex Kim" in armed and "Enter = delete" in armed, \
            "picking did not arm the confirm naming the author:\n" + armed
        s.send(ESC); s.pump(0.8)
        assert "cancelled" in s.visible().lower(), "Esc did not cancel the armed delete:\n" + s.visible()
        assert C3_MARK in s.scrolled_lines(), "a cancelled delete removed the comment anyway"

        # ── Pick c3 → Enter confirms → c3 disappears from the pane ──
        s.open_delete_picker()
        s.send(ENTER); s.pump(1.0)          # pick c3
        s.send(ENTER); s.pump(2.0)          # Enter confirms the delete
        assert C3_MARK not in s.scrolled_lines(), \
            "the confirmed comment did not disappear from the Comments pane:\n" + s.visible()

        # ── The DELETE reached the backend, keyed to the picked comment (c3) ──
        with open(log, encoding="utf-8") as f:
            recorded = [ln.strip() for ln in f if ln.strip()]
        assert "c3" in recorded, "DELETE /comment/c3 not recorded (wrong/absent target id): " + repr(recorded)
        print("ok — Delete → picker → pick c3 (Alex Kim) → confirm removes it; Esc on the picker and on the "
              "armed confirm both cancel; DELETE /comment/c3 recorded")
    finally:
        s.kill()


def run_forbid(log):
    s = Session(dict(E2E_COMMENT_DELETE_LOG=log, E2E_COMMENT_DELETE_FORBID="c3"))
    try:
        s.pump(8.0)
        assert "Task" in s.visible(), "list boot failed"
        s.open_comments_tab()

        s.open_delete_picker()
        s.send(ENTER); s.pump(1.0)          # pick c3
        s.send(ENTER); s.pump(2.5)          # confirm → the backend answers 403 → revert
        after = s.visible()
        assert "Could not delete comment" in after, \
            "a forbidden delete did not flash the permission error:\n" + after
        assert C3_MARK in s.scrolled_lines(), \
            "a forbidden delete did not revert (c3 stayed removed):\n" + s.visible()
        print("ok — a forbidden (403) delete reverts: c3 reappears and 'Could not delete comment' flashes")
    finally:
        s.kill()


def main():
    log = os.path.join(tempfile.mkdtemp(), "comment-deletes.log")
    run_happy_and_cancel(log)
    open(log, "w").close()   # reset the recorder between boots
    run_forbid(log)


main()
