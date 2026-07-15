#!/usr/bin/env python3
"""Drives the Task Detail comment composer (#216): Enter opens the detail view, Ctrl+N
opens the composer overlay, the user types a comment, then Tab→Post→Enter posts it. The
fake backend answers POST /task/{id}/comment with a created-comment shape, so the posted
text should appear in the Stream/Comments body (optimistic append reconciled from the
server response). Also checks Esc cancels the composer and an empty body is a no-op.

Asserts on the pyte-rendered screen (never raw bytes). Exits nonzero / prints a traceback
on failure."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet")
proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                        env=env, close_fds=True, preexec_fn=os.setsid)
os.close(slave)


def answer(data):
    if b"\x1b[18t" in data:
        os.write(master, b"\x1b[8;%d;%dt" % (ROWS, COLS))
    if b"\x1b[6n" in data:
        os.write(master, b"\x1b[1;1R")


def pump(seconds):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        r, _, _ = select.select([master], [], [], 0.05)
        if r:
            try:
                chunk = os.read(master, 65536)
            except OSError:
                break
            if not chunk:
                break
            answer(chunk)
            stream.feed(chunk)


def visible():
    from wcwidth import wcwidth
    lines = []
    for y in range(ROWS):
        row = screen.buffer[y]
        out = []
        prev_wide = False
        for x in range(COLS):
            data = row[x].data
            if data == "":
                if not prev_wide:
                    out.append("▯")
                prev_wide = False
            else:
                out.append(data)
                prev_wide = len(data) > 0 and wcwidth(data[0]) == 2
        lines.append("".join(out).rstrip())
    return "\n".join(lines)


POSTED = "Composed via Ctrl N please review"
CANCELLED = "This draft should never post"

try:
    pump(8.0)
    assert "Task" in visible(), "list boot failed"

    os.write(master, b"\r")           # Enter → open detail
    pump(3.0)
    assert "Description" in visible(), "detail screen did not open:\n" + visible()

    # ── Ctrl+N opens the composer ──────────────────────────────────────────────
    os.write(master, b"\x0e")         # Ctrl+N (ASCII 14)
    pump(1.2)
    assert "New comment" in visible(), "composer did not open on Ctrl+N:\n" + visible()

    # Type the comment, then Tab to the Post button and Enter to post.
    os.write(master, POSTED.encode())
    pump(1.0)
    assert POSTED in visible(), "typed text not shown in composer:\n" + visible()
    os.write(master, b"\t")           # Tab: editor → Post button
    pump(0.6)
    os.write(master, b"\r")           # Enter on Post → post
    pump(2.0)
    after_post = visible()
    assert "New comment" not in after_post, "composer stayed open after Post:\n" + after_post
    assert POSTED in after_post, "posted comment not shown in body:\n" + after_post

    # ── Esc cancels without posting ────────────────────────────────────────────
    os.write(master, b"\x0e")         # Ctrl+N again
    pump(1.0)
    assert "New comment" in visible(), "composer did not reopen:\n" + visible()
    os.write(master, CANCELLED.encode())
    pump(0.8)
    os.write(master, b"\x1b")         # Esc → cancel
    pump(1.2)
    after_cancel = visible()
    assert "New comment" not in after_cancel, "composer stayed open after Esc:\n" + after_cancel
    assert CANCELLED not in after_cancel, "cancelled draft leaked into the body:\n" + after_cancel

    # ── Empty body is a no-op (opens, Tab→Post→Enter with nothing typed) ───────
    os.write(master, b"\x0e")
    pump(1.0)
    assert "New comment" in visible(), "composer did not reopen for empty-body check:\n" + visible()
    os.write(master, b"\t")
    pump(0.5)
    os.write(master, b"\r")           # Post with empty body
    pump(1.2)
    after_empty = visible()
    assert "New comment" not in after_empty, "composer stayed open after empty Post:\n" + after_empty
    # The one posted comment is still there; nothing else was added.
    assert POSTED in after_empty, "earlier posted comment vanished:\n" + after_empty

    print("ok")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
