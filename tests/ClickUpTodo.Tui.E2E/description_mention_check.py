#!/usr/bin/env python3
"""Asserts @-mention authoring in the Task Detail description editor (#326, sub-issue L of #313).

Boots the dashboard `TodoApp`, opens Task Detail, and drives the `Ctrl+E` description editor:

  - Ctrl+Home to the document start (deterministic insert point), then press `@` to open the mention
    picker, type "Ada" to surface the "Ada Lovelace" row, `Enter` to insert the "@Ada Lovelace " token;
  - assert the "@Ada Lovelace" reference is spliced into the editor;
  - Tab→Save→Enter, and assert the saved description renders "@Ada Lovelace" in the Description body.

Per the #321 spike (Finding 2), a description mention is *plain literal text* — ClickUp descriptions
carry no structured mention payload — so the deliverable is the `@Name` reference round-tripping through
the unchanged plain-string write path, asserted on the pyte screen (the fake backend echoes the
description PUT, so it lands in the body without a manual refresh). The member pool is the same assignee
top-up (`GET /team`) the #325 comment-composer wiring uses, so no new fake endpoint is needed.

Self-contained. Asserts on the pyte-rendered screen (never raw bytes). Exits nonzero / prints a
traceback on failure."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

MEMBER_NAME = "Ada Lovelace"   # Members[0] in Program.cs, seeded into the assignee/member pool
SEEDED = "Call Center training"  # first line of the fake task's description


class App:
    def __init__(self, **extra_env):
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet", **extra_env)
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
            r, _, _ = select.select([self.master], [], [], 0.05)
            if r:
                try:
                    chunk = os.read(self.master, 65536)
                except OSError:
                    break
                if not chunk:
                    break
                self._answer(chunk)
                self.stream.feed(chunk)

    def kill(self):
        try:
            os.killpg(os.getpgid(self.proc.pid), signal.SIGKILL)
        except Exception:
            pass

    def row_text(self, y):
        return "".join(self.screen.buffer[y][x].data for x in range(COLS))

    def visible(self):
        return "\n".join(self.row_text(y).rstrip() for y in range(ROWS))

    def on_detail(self):
        return "Description" in self.visible()

    def key(self, data, settle=1.5):
        os.write(self.master, data)
        self.pump(settle)

    def type_text(self, text, settle=1.5):
        self.key(text.encode(), settle=settle)

    def esc(self):
        self.key(b"\x1b")


def run():
    app = App()
    try:
        # Boot + settle long enough for the assignee pool top-up (GET /team) to land, so the picker has
        # Ada Lovelace to match — the pool the #325/#326 wiring projects into WorkspaceMembers.
        app.pump(9.0)
        assert "Task" in app.visible(), "list boot failed:\n" + app.visible()

        app.key(b"\r", settle=3.0)  # Enter → Task Detail
        assert app.on_detail(), "detail screen did not open:\n" + app.visible()
        assert SEEDED in app.visible(), "seed description not shown in detail body:\n" + app.visible()

        # ── Ctrl+E → editor; @ opens the mention picker; pick Ada Lovelace ───────────────────────────
        app.key(b"\x05", settle=1.5)             # Ctrl+E → description editor
        assert "Edit description" in app.visible(), "editor did not open on Ctrl+E:\n" + app.visible()
        app.key(b"\x1b[1;5H", settle=0.5)        # Ctrl+Home → caret to document start (deterministic)
        app.key(b"@", settle=1.5)                # @ opens the mention picker (consumes the literal @)
        app.type_text("Ada", settle=2.5)         # debounced search → the "Ada Lovelace" row
        assert MEMBER_NAME in app.visible(), \
            "mention picker did not surface the member row for 'Ada':\n" + app.visible()
        app.key(b"\r", settle=2.0)               # Enter → pick highlighted row → insert token, close picker
        assert "@" + MEMBER_NAME in app.visible(), \
            "the @Ada Lovelace token was not inserted into the editor:\n" + app.visible()
        # Still in the editor (the picker closed back to it, not out of the editor).
        assert "Edit description" in app.visible(), \
            "picking a mention should return to the editor, not close it:\n" + app.visible()

        # ── Save via the driver-robust Tab→Save→Enter, assert the @Name reference round-trips ─────────
        app.key(b"\t", settle=1.0)               # Tab → Save button
        app.key(b"\r", settle=2.5)               # Enter → save
        after = app.visible()
        assert "Edit description" not in after, "editor stayed open after Save:\n" + after
        assert "@" + MEMBER_NAME in after, \
            "the saved @Ada Lovelace reference did not render in the Description body:\n" + after
        assert SEEDED in after, "the original description text was lost on save:\n" + after
    finally:
        app.kill()


run()
print(f"ok — Ctrl+E → @ → pick '{MEMBER_NAME}' inserts the '@{MEMBER_NAME}' reference into the editor; "
      f"save round-trips it into the Description body as plain text (#326)")
