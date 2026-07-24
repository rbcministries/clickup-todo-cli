#!/usr/bin/env python3
"""Boots the TUI under a PTY and validates the opt-in subdomain auto-detect (#351): with the workspace
subdomain UNSET and a deterministic detector injected via E2E_DETECT_SUBDOMAIN=odbm, F2 opens Settings
with a blank "ClickUp subdomain" field and a "Detect" button; activating Detect fills the field with the
detected 'odbm'. The real probe hits app.clickup.com (unreachable under the PTY), so the harness swaps in
a canned detector — this asserts the button → field wiring, not the network round-trip. Asserts on the
pyte screen."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)

master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
# No E2E_SUBDOMAIN → the field starts blank; E2E_DETECT_SUBDOMAIN injects a canned detector.
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
           E2E_DETECT_SUBDOMAIN="odbm")
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


F2 = b"\x1bOQ"
ESC = b"\x1b"
TAB = b"\t"
SPACE = b" "

try:
    pump(8.0)
    assert "Task" in visible(), "app never rendered the list:\n" + visible()[-1500:]
    pump(1.0)

    # F2 opens Settings; the subdomain field is blank and the Detect button is present.
    send(F2, 2.0)
    v = visible()
    assert "ClickUp subdomain" in v, f"subdomain field label missing in Settings:\n{v}"
    assert "Detect" in v, f"Detect button missing in Settings:\n{v}"
    assert "odbm" not in v, f"subdomain unexpectedly already set before Detect:\n{v}"
    print("SETTINGS ok — subdomain blank, Detect button present")

    # Focus starts on the refresh field; the Detect button sits right after the subdomain field, so five
    # Tabs (refresh → feed-refresh → feed-lookback → working-dir → subdomain → Detect) lands on it. Space
    # activates it.
    for _ in range(5):
        send(TAB, 0.3)
    send(SPACE, 0.5)
    # Poll for the async detect (Task.Run → Application.Invoke) to land the value in the field.
    deadline = time.monotonic() + 6.0
    while time.monotonic() < deadline and "odbm" not in visible():
        pump(0.4)
    v = visible()
    assert "odbm" in v, f"Detect did not fill the subdomain field with 'odbm':\n{v}"
    print("DETECT ok — Detect filled the ClickUp subdomain field with 'odbm'")

    send(ESC, 1.0)
    print("PASS")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGTERM)
    except ProcessLookupError:
        pass
