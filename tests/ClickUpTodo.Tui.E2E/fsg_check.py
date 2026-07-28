#!/usr/bin/env python3
"""Boots the TUI under a PTY and exercises the Filter · Sort · Group (F3) screen's
command keys after they were migrated to dispatch through the central KeybindingDispatcher
/ Keybindings table (#398 slice for FilterSortGroupScreen):

  list --F3--> Filter·Sort·Group  --F1--> Help  --Esc--> back to Filter·Sort·Group  --Esc--> list

Asserts each transition on the pyte screen, proving the table-driven Back (Esc) and Help (F1)
bindings still dispatch exactly as the literal switch did before the refactor. Run with the
default E2E_TASKS so the list renders; the F3 screen has no backend dependency."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
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

def send(seq, wait=1.2):
    os.write(master, seq)
    return pump(wait)

F3 = b"\x1bOR"   # SS3 — matches the F1=\x1bOP encoding the harness already drives
F1 = b"\x1bOP"
ESC = b"\x1b"

try:
    pump(8.0)
    assert "Task" in visible(), "app never rendered the list:\n" + visible()[-1500:]
    pump(1.0)

    # 1. F3 opens the Filter · Sort · Group screen (main-list FilterSortGroup command).
    send(F3, 1.5)
    v = visible()
    assert "Add a filter" in v, f"F3 did not open Filter·Sort·Group:\n{v}"
    assert "Active filters" in v, f"Filter·Sort·Group body missing:\n{v}"
    print("F3-OPEN ok — Filter · Sort · Group renders")

    # 2. F1 (the table's Help binding for this context) opens Help over the screen.
    send(F1, 1.5)
    v = visible()
    assert "Keyboard shortcuts" in v, f"F1 did not open Help from Filter·Sort·Group:\n{v}"
    print("F1-HELP ok — Help opens over Filter · Sort · Group")

    # 3. Esc (the table's Back binding) closes Help back to Filter · Sort · Group, not to the list.
    send(ESC, 1.5)
    v = visible()
    assert "Keyboard shortcuts" not in v, f"Esc did not close Help:\n{v}"
    assert "Add a filter" in v, f"Esc from Help did not return to Filter·Sort·Group:\n{v}"
    print("ESC-BACK-TO-FSG ok — Help dismissed, back on Filter · Sort · Group")

    # 4. Esc again closes Filter · Sort · Group back to the list (Result stays null / cancelled).
    send(ESC, 1.5)
    v = visible()
    assert "Add a filter" not in v, f"Esc did not close Filter·Sort·Group:\n{v}"
    assert "next section" in v, f"Esc from Filter·Sort·Group did not return to the list:\n{v}"
    print("ESC-CLOSE ok — Filter · Sort · Group cancelled back to the list")

    print("FILTER-SORT-GROUP E2E: PASS")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
