#!/usr/bin/env python3
"""Like screen_check but dumps a per-cell (char, fg, bg, reverse) signature so
stock vs diffed runs can be compared including colors. Also exercises a
mid-session resize and a screen swap (F1 help open/close)."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]
OUT = sys.argv[2]

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
    n = 0
    while time.monotonic() < end:
        r, _, _ = select.select([master], [], [], 0.05)
        if r:
            try:
                chunk = os.read(master, 65536)
            except OSError:
                break
            if not chunk:
                break
            n += len(chunk)
            answer(chunk)
            stream.feed(chunk)
    return n

def signature():
    lines = []
    for r in range(ROWS):
        row = screen.buffer[r]
        cells = []
        for c in range(COLS):
            ch = row[c]
            cells.append(f"{ch.data}|{ch.fg}|{ch.bg}|{int(ch.reverse)}")
        lines.append(";".join(cells).rstrip())
    return "\n".join(lines)

try:
    pump(8.0)
    assert "Task" in "\n".join(screen.display), "boot failed"
    # navigate a bit
    for _ in range(3):
        os.write(master, b"\x1b[B"); pump(0.4)
    # open help (F1) and close it (Esc) — full screen swap both ways
    os.write(master, b"\x1bOP"); pump(1.0)
    os.write(master, b"\x1b"); pump(1.0)
    if os.environ.get("DO_RESIZE"):
        # resize the terminal mid-session and let it settle, then keep navigating
        fcntl.ioctl(master, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS - 10, COLS - 40, 0, 0))
        os.write(master, b"\x1b[8;%d;%dt" % (ROWS - 10, COLS - 40))
        screen.resize(ROWS - 10, COLS - 40)
        pump(2.0)
        os.write(master, b"\x1b[B"); pump(0.5)
        rows, cols = ROWS - 10, COLS - 40
    else:
        os.write(master, b"\x1b[B"); pump(0.5)
        rows, cols = ROWS, COLS
    with open(OUT, "w") as f:
        for r in range(rows):
            row = screen.buffer[r]
            f.write(";".join(f"{row[c].data}|{row[c].fg}|{row[c].bg}|{int(row[c].reverse)}" for c in range(cols)))
            f.write("\n")
    print("ok")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
