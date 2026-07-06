#!/usr/bin/env python3
"""Boots the TUI under a PTY, renders its output through a real VT emulator
(pyte), presses Down N times, and reports (a) the final visible screen text,
(b) bytes emitted per press. Used to compare stock vs diffed output for both
correctness (identical screens) and volume."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]
PRESSES = int(sys.argv[2]) if len(sys.argv) > 2 else 5
OUT = sys.argv[3] if len(sys.argv) > 3 else "/tmp/screen.txt"

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
    out = b""
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
            out += chunk
            answer(chunk)
            stream.feed(chunk)
    return out

def visible():
    return "\n".join(line.rstrip() for line in screen.display).rstrip()

try:
    pump(8.0)
    assert "Task" in visible(), visible()[-1000:]
    pump(1.0)

    per_press = []
    for i in range(PRESSES):
        os.write(master, b"\x1b[B")
        data = pump(0.8)
        per_press.append(len(data))

    with open(OUT, "w") as f:
        f.write(visible())
    # idle chatter baseline over the same window, to subtract mentally
    idle = len(pump(0.8))
    print(f"bytes per Down-press: {per_press} (idle window: {idle})")
    print(f"screen written to {OUT}")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
