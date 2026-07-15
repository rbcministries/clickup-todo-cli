#!/usr/bin/env python3
"""Drives the Task Detail description editor (#217): Enter opens the detail view, Ctrl+E
opens the editor overlay pre-filled with the current description. The fake backend echoes
a description PUT (see ApplyDescriptionMutation), so a saved edit round-trips into the
Description body without a manual refresh.

Covers the acceptance criteria:
  - pre-filled: opening then Esc with no edits closes WITHOUT a discard prompt (an empty
    editor would be 'dirty' vs the existing description and would prompt);
  - save: typed text + Tab→Save→Enter persists and shows in the detail body, editor closed;
  - cancel with unsaved edits: Esc arms an inline Y/N confirm; N keeps editing (draft kept),
    Y discards (draft never reaches the body).

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


SEEDED = "Call Center training"          # first line of the fake task's description
SAVED = "EDITED-VIA-CTRL-E"              # a marker we type and save
DISCARD = "DISCARD-THIS-DRAFT"          # a marker we type then discard

try:
    pump(8.0)
    assert "Task" in visible(), "list boot failed"

    os.write(master, b"\r")           # Enter → open detail
    pump(3.0)
    v = visible()
    assert "Description" in v, "detail screen did not open:\n" + v
    assert SEEDED in v, "seed description not shown in detail body:\n" + v

    # ── Ctrl+E opens the editor, pre-filled (Esc with no edits closes WITHOUT a prompt) ──
    os.write(master, b"\x05")         # Ctrl+E (ASCII 5)
    pump(1.2)
    assert "Edit description" in visible(), "editor did not open on Ctrl+E:\n" + visible()
    os.write(master, b"\x1b")         # Esc immediately (no edits)
    pump(1.2)
    after = visible()
    assert "Edit description" not in after, "editor stayed open after no-edit Esc:\n" + after
    assert "Discard unsaved changes" not in after, \
        "no-edit Esc prompted to discard — editor was NOT pre-filled:\n" + after

    # ── Save: type a marker, Tab→Save→Enter, assert it round-trips into the body ─────────
    os.write(master, b"\x05")         # Ctrl+E again
    pump(1.2)
    assert "Edit description" in visible(), "editor did not reopen:\n" + visible()
    os.write(master, b"\x1b[1;5H")    # Ctrl+Home → caret to document start (deterministic insert point)
    pump(0.4)
    os.write(master, SAVED.encode())
    pump(0.8)
    assert SAVED in visible(), "typed text not shown in editor:\n" + visible()
    os.write(master, b"\t")           # Tab: editor → Save button
    pump(0.6)
    os.write(master, b"\r")           # Enter on Save → save
    pump(2.5)
    after_save = visible()
    assert "Edit description" not in after_save, "editor stayed open after Save:\n" + after_save
    assert SAVED in after_save, "saved description not reflected in body:\n" + after_save

    # ── Cancel with unsaved edits: Esc → Y/N confirm; N keeps editing, Y discards ────────
    os.write(master, b"\x05")         # Ctrl+E
    pump(1.2)
    assert "Edit description" in visible(), "editor did not reopen for cancel check:\n" + visible()
    os.write(master, DISCARD.encode())
    pump(0.8)
    assert DISCARD in visible(), "discard draft not shown in editor:\n" + visible()
    os.write(master, b"\x1b")         # Esc → arm the discard confirm
    pump(1.0)
    confirm = visible()
    assert "Discard unsaved changes" in confirm, "Esc on dirty did not prompt to confirm:\n" + confirm
    os.write(master, b"n")            # N → dismiss confirm, keep editing (draft kept)
    pump(1.0)
    kept = visible()
    assert "Edit description" in kept, "editor closed on N (should keep editing):\n" + kept
    assert "Discard unsaved changes" not in kept, "confirm prompt lingered after N:\n" + kept
    assert DISCARD in kept, "draft was lost after N:\n" + kept
    os.write(master, b"\x1b")         # Esc → arm confirm again
    pump(1.0)
    assert "Discard unsaved changes" in visible(), "second Esc did not re-arm confirm:\n" + visible()
    os.write(master, b"y")            # Y → discard and close
    pump(1.5)
    discarded = visible()
    assert "Edit description" not in discarded, "editor stayed open after Y-discard:\n" + discarded
    assert DISCARD not in discarded, "discarded draft leaked into the body:\n" + discarded
    assert SAVED in discarded, "previously-saved text vanished after discard:\n" + discarded

    print("ok")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
