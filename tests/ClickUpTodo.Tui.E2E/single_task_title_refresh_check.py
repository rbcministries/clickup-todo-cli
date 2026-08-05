#!/usr/bin/env python3
"""Single-task terminal title updates on refresh (#425): boots the harness in single-task
launch mode (E2E_SINGLE_TASK=<id>) with the E2E_TITLE_REFRESH=1 gate, which renames the launch
task after its first (boot) detail fetch. Asserts pyte's screen.title leads with the ORIGINAL
name at boot, then CHANGES to the renamed title after a Ctrl+R refresh — the end-to-end proof
that SingleTaskApp reassigns its window Title on refresh and Terminal.Gui re-emits the OSC title
escape to the terminal. #418 only set the title once at launch; this covers the refresh wire-in.

TerminalTitleTests already pins the pure formatting/decision (ForTask/Retitle) in CI; this is the
host-code proof. Single-run behavioural check (there is no stock baseline for a title write)."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]
OUT = sys.argv[2] if len(sys.argv) > 2 else None
TASK_ID = os.environ.get("E2E_SINGLE_TASK", "t5")

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
           E2E_SINGLE_TASK=TASK_ID, E2E_TITLE_REFRESH="1")
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

def pump_until_title_changes(from_title, seconds):
    """Pump for up to `seconds`, returning as soon as screen.title differs from from_title."""
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
        if screen.title and screen.title != from_title:
            return

try:
    # Boot: the launch-task detail (first fetch) keeps the original long name, titled + truncated.
    pump(6.0)
    initial = screen.title
    expected_prefix = f"{TASK_ID}: My Account - Address display"
    assert initial, "no terminal title was set on --task launch (screen.title is empty)"
    assert initial.startswith(expected_prefix), \
        f"boot title did not lead with the id + original name; got: {initial!r}"
    assert len(initial) <= 40, f"boot title exceeds 40 chars: {initial!r} (len {len(initial)})"

    # Refresh: Ctrl+R (0x12) re-fetches; the gate now serves "Renamed on refresh", so the window
    # Title must be reassigned and re-emitted to the terminal.
    os.write(master, b"\x12")
    pump_until_title_changes(initial, 8.0)
    refreshed = screen.title

    expected_refreshed = f"{TASK_ID}: Renamed on refresh"
    assert refreshed != initial, \
        f"terminal title did not change after refresh (stayed {initial!r}) — retitle-on-refresh missing"
    assert refreshed == expected_refreshed, \
        f"refreshed title was not the renamed task; expected {expected_refreshed!r}, got: {refreshed!r}"

    if OUT:
        with open(OUT, "w") as f:
            f.write(f"initial={initial!r}\nrefreshed={refreshed!r}\n")
    print(f"ok — title updated on refresh ({initial!r} -> {refreshed!r})")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
