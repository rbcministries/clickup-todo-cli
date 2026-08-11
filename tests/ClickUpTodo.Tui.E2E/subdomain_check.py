#!/usr/bin/env python3
"""Boots the TUI under a PTY and validates the workspace-subdomain browser rewrite (#304):
with E2E_SUBDOMAIN=odbm seeded into config, (1) F10 opens Settings and the "ClickUp subdomain"
field renders with the seeded value, and (2) Ctrl+B on a task row launches the browser with the
task's app.clickup.com URL rewritten onto odbm.clickup.com — observed via a recording
IBrowserLauncher that logs each launched URL to E2E_BROWSER_LOG. Asserts each step on the pyte
screen (Settings) and on the recorded log (rewrite)."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess, tempfile
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

browser_log = tempfile.NamedTemporaryFile(prefix="clickup-e2e-browser-", suffix=".log", delete=False)
browser_log.close()
LOG = browser_log.name

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)

master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
           E2E_SUBDOMAIN="odbm", E2E_BROWSER_LOG=LOG)
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

# F10 opens Settings since #539 (was F2); CSI-tilde form, matching the harness's F5/F6/F12.
F10 = b"\x1b[21~"
ESC = b"\x1b"
TAB = b"\t"
CTRL_B = b"\x02"

try:
    pump(8.0)
    assert "Task" in visible(), "app never rendered the list:\n" + visible()[-1500:]
    pump(1.0)

    # (1) F10 opens Settings; the ClickUp subdomain field renders with the seeded value.
    send(F10, 2.0)
    v = visible()
    assert "ClickUp subdomain" in v, f"subdomain field label missing in Settings:\n{v}"
    assert "odbm" in v, f"seeded subdomain 'odbm' not shown in Settings:\n{v}"
    print("SETTINGS ok — ClickUp subdomain field renders with seeded 'odbm'")

    # Esc closes Settings without saving (the value is already in config); back to the list.
    send(ESC, 1.0)
    assert "ClickUp subdomain" not in visible(), f"Settings didn't close on Esc:\n{visible()}"

    # (2) Tab lands the cursor on the first task row; Ctrl+B launches the (rewritten) URL.
    send(TAB, 0.8)
    send(CTRL_B, 1.5)

    with open(LOG, encoding="utf-8") as f:
        launched = [ln.strip() for ln in f if ln.strip()]
    assert launched, "Ctrl+B recorded no browser launch (cursor not on a task?)"
    assert all(u.startswith("https://odbm.clickup.com/t/") for u in launched), \
        f"launched URL(s) not rewritten onto the subdomain host: {launched}"
    assert not any("app.clickup.com" in u for u in launched), \
        f"launched URL still points at app.clickup.com (redirect not skipped): {launched}"
    print(f"REWRITE ok — Ctrl+B launched {launched[-1]} (app.clickup.com → odbm.clickup.com)")

    print("PASS")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGTERM)
    except ProcessLookupError:
        pass
    try:
        os.unlink(LOG)
    except OSError:
        pass
