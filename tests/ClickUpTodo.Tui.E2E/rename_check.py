#!/usr/bin/env python3
"""Boots the TUI under a PTY and exercises the main-list F2 task rename (contextual chords H, #545):

  Leg 1 (rename round-trip): F2 on the focused task opens the "Rename task" overlay pre-filled with
    the current title; clearing it and typing a new title, then Enter, renames the row in place —
    optimistically, then confirmed by the PUT echo (the fake's mutable task Name applier) — and closes
    the overlay back to the list with the new title on the row.
  Leg 2 (Esc cancels): F2 → type a distinctive marker → Esc closes the overlay and the marker never
    reaches the list, so no rename was written.

Asserts each step on the pyte screen. The rename write round-trips through the default backend's
PUT /task/{id} (now applying a {"name":...} body), so no scenario is needed."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

F2 = b"\x1bOQ"            # rename the focused task (main list, #545)
ENTER = b"\r"
ESC = b"\x1b"
BACKSPACE = b"\x7f"
DELETE = b"\x1b[3~"      # forward-delete (paired with BACKSPACE to clear a field caret-agnostically)

RENAME = "RENAMED VIA F2 E2E"
CANCEL_MARKER = "CANCELLED RENAME MARKER"


class Session:
    def __init__(self):
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        # A high refresh interval keeps the background poll out of the way so the rename's optimistic +
        # confirmed row is what the check observes (the list isn't re-fetched mid-check).
        env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
                   E2E_TASKS="20", E2E_REFRESH="600")
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

    def close(self):
        try:
            os.killpg(os.getpgid(self.proc.pid), signal.SIGKILL)
        except Exception:
            pass


def boot(s):
    s.pump(8.0)
    assert "Task" in s.visible(), "app never rendered the list:\n" + s.visible()[-1500:]
    s.pump(1.0)


def run_rename_roundtrip():
    s = Session()
    try:
        boot(s)

        # F2 opens the rename overlay pre-filled with the focused task's title.
        s.send(F2, 1.6)
        v = s.visible()
        assert "New title:" in v, f"F2 did not open the rename overlay:\n{v}"
        assert "Rename task" in v, f"rename overlay title missing:\n{v}"
        # Its footer advertises only what the overlay does (save/help/cancel).
        assert "save" in v and "cancel" in v, f"rename overlay footer missing save/cancel:\n{v}"
        print("OPEN ok — F2 opens the 'Rename task' overlay")

        # Clear the prefilled title (caret-agnostic: backspaces then forward-deletes) and type a new one.
        s.send(BACKSPACE * 90 + DELETE * 90, 0.6)
        s.send(RENAME.encode(), 1.0)
        assert RENAME in s.visible(), f"typed new title not shown in the overlay:\n{s.visible()}"
        print("TYPE ok — cleared the prefill and typed a new title")

        # Enter saves: the overlay closes and the row reflects the new title (optimistic, then confirmed
        # by the PUT echo).
        s.send(ENTER, 2.5)
        v = s.visible()
        assert "New title:" not in v, f"Enter did not close the rename overlay:\n{v}"
        assert RENAME in v, f"the renamed title is not on the list row after save:\n{v}"
        print("SAVE ok — the row shows the new title after Enter (optimistic + confirmed)")
        print("ok — F2 rename round-trip: overlay → clear → type → Enter renames the row")
    finally:
        s.close()


def run_esc_cancels():
    s = Session()
    try:
        boot(s)

        s.send(F2, 1.6)
        assert "New title:" in s.visible(), f"F2 did not open the rename overlay:\n{s.visible()}"
        s.send(BACKSPACE * 90 + DELETE * 90, 0.6)
        s.send(CANCEL_MARKER.encode(), 1.0)
        assert CANCEL_MARKER in s.visible(), f"typed marker not shown in the overlay:\n{s.visible()}"

        # Esc cancels: the overlay closes and the marker never reaches the list (no rename written).
        s.send(ESC, 1.6)
        v = s.visible()
        assert "New title:" not in v, f"Esc did not close the rename overlay:\n{v}"
        assert CANCEL_MARKER not in v, f"Esc still applied the rename (marker leaked to the list):\n{v}"
        print("ok — Esc cancels the rename: overlay closes and no rename is written")
    finally:
        s.close()


run_rename_roundtrip()
run_esc_cancels()
print("RENAME E2E: PASS")
