#!/usr/bin/env python3
"""Drives the TUI under a PTY: waits for the task list to load, then sends
arrow-key presses and measures time-to-first-output-byte for each."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess

ROWS, COLS = 50, 200
DLL = sys.argv[1]
PRESSES = int(sys.argv[2]) if len(sys.argv) > 2 else 15

master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))

env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet")
proc = subprocess.Popen(
    ["dotnet", DLL],
    stdin=slave, stdout=slave, stderr=slave,
    env=env, close_fds=True, preexec_fn=os.setsid,
)
os.close(slave)

def answer_queries(data):
    """Responds to terminal queries the app sends (size, cursor pos, DA)."""
    if b"\x1b[18t" in data:
        os.write(master, b"\x1b[8;%d;%dt" % (ROWS, COLS))
    if b"\x1b[6n" in data:
        os.write(master, b"\x1b[1;1R")
    if b"\x1b[0c" in data or b"\x1b[c" in data:
        os.write(master, b"\x1b[?62;22c")

def read_available(timeout):
    """Reads whatever arrives within timeout; returns bytes (b'' if none)."""
    out = b""
    end = time.monotonic() + timeout
    while True:
        remaining = end - time.monotonic()
        if remaining <= 0:
            break
        r, _, _ = select.select([master], [], [], remaining)
        if not r:
            break
        try:
            chunk = os.read(master, 65536)
        except OSError:
            break
        if not chunk:
            break
        out += chunk
        answer_queries(chunk)
        # keep draining briefly in case more follows
        end = min(end, time.monotonic() + 0.15)
    return out

def wait_for_redraw(timeout):
    """Returns seconds until a chunk with actual row content arrives (the list
    redraw), ignoring the idle per-iteration cursor chatter. None on timeout."""
    t0 = time.monotonic()
    while time.monotonic() - t0 < timeout:
        r, _, _ = select.select([master], [], [], 0.02)
        if not r:
            continue
        try:
            chunk = os.read(master, 65536)
        except OSError:
            return None
        answer_queries(chunk)
        if b"Task" in chunk:
            return time.monotonic() - t0
    return None

try:
    # Let the app boot + first fetch + render, until output goes quiet.
    boot = b""
    quiet = 0
    t0 = time.monotonic()
    while time.monotonic() - t0 < 30:
        chunk = read_available(1.0)
        if chunk:
            boot += chunk
            quiet = 0
        else:
            quiet += 1
            if quiet >= 2 and b"Task 1" in boot:
                break
    if b"Task 1" not in boot:
        print("BOOT FAILED — captured output tail:")
        print(boot[-2000:].decode(errors="replace"))
        raise SystemExit(1)
    print(f"boot ok ({len(boot)} bytes)")

    latencies = []
    for i in range(PRESSES):
        os.write(master, b"\x1b[B")  # Down arrow
        lat = wait_for_redraw(10.0)
        if lat is None:
            print(f"press {i}: NO REDRAW within 10s")
            latencies.append(None)
        else:
            drained = read_available(0.5)  # drain the rest of the redraw
            latencies.append(lat)
            print(f"press {i}: redraw after {lat*1000:.0f} ms, drained {len(drained)} bytes")
    ok = [l for l in latencies if l is not None]
    if ok:
        print(f"\nmedian {sorted(ok)[len(ok)//2]*1000:.0f} ms · min {min(ok)*1000:.0f} ms · max {max(ok)*1000:.0f} ms")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
