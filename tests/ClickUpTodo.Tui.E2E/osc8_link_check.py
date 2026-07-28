#!/usr/bin/env python3
"""Asserts the OSC-8 terminal hyperlinks added in #380 (Task Detail, follow-up to #317).

Opens Task Detail and checks that the two links #317 already seeds are each wrapped, in the
raw output, in an OSC-8 hyperlink escape pointing at their own URL:

  - a ClickUp **task** link in the Description  (https://app.clickup.com/t/86a1b2c3d)
  - an **other web** link in the Comments        (https://github.com/rbcministries/ODBM.Secure/pull/64)

OSC-8 is `ESC ] 8 ; ; <url> ST  <visible text>  ESC ] 8 ; ; ST` (ST = `ESC \\`). Unlike every
other tui-validate check this asserts on the **raw byte stream**, not the pyte screen: OSC-8 is
a hyperlink escape a VT emulator consumes rather than renders, so it never appears on the pyte
screen — this is the harness's documented raw-bytes-for-escape-checks exception. pyte is still
driven in parallel purely to detect boot / navigate the tabs.

Fixed COLS=120 (as link_check.py) so the seeded URLs don't wrap — a wrapped link is out of #380's
scope (tracked with the wrapped-line rendering work, #413). Exits nonzero / prints a traceback on
failure."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

TASK_URL = "https://app.clickup.com/t/86a1b2c3d"
WEB_URL = "https://github.com/rbcministries/ODBM.Secure/pull/64"

ESC = b"\x1b"
ST = b"\x1b\\"


def osc8_start(url):
    """The exact OSC-8 open sequence that targets `url`."""
    return ESC + b"]8;;" + url.encode() + ST


OSC8_END = ESC + b"]8;;" + ST

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet")
proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                        env=env, close_fds=True, preexec_fn=os.setsid)
os.close(slave)

raw = bytearray()  # every byte the app emits, for the escape assertions


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
            raw.extend(chunk)
            answer(chunk)
            stream.feed(chunk)


def visible():
    return "\n".join(
        "".join(screen.buffer[y][x].data for x in range(COLS)).rstrip() for y in range(ROWS))


def assert_wrapped(url, where):
    """The URL's OSC-8 open sequence is present AND a close (OSC-8 end) follows it — so the hyperlink is
    scoped to the URL run rather than leaking onto the rest of the pane."""
    start = osc8_start(url)
    idx = raw.find(start)
    assert idx >= 0, f"no OSC-8 open sequence for the {where}"
    # A close must follow the open (after the wrapped visible text), bounding the run. Search from just
    # past the open sequence so we can't accidentally match a close that preceded it.
    assert raw.find(OSC8_END, idx + len(start)) >= 0, \
        f"OSC-8 open for the {where} is not followed by a close — the hyperlink run is unbounded"


try:
    pump(8.0)
    assert "Task" in visible(), "list boot failed"

    os.write(master, b"\r")          # Enter → open detail (opens on the Description tab)
    pump(3.0)
    assert "Description" in visible(), "detail screen did not open:\n" + visible()

    # ── Task link on the Description tab carries a bounded OSC-8 hyperlink to its own URL ─────────────
    assert_wrapped(TASK_URL, "task link on the Description tab")

    # ── Web link on the Comments tab: cycle tabs until its OSC-8 open sequence appears ───────────────
    found_web = osc8_start(WEB_URL) in raw
    for _ in range(4):
        if found_web:
            break
        os.write(master, b"\x1b[1;5C")   # Ctrl+→ → next tab
        pump(1.2)
        found_web = osc8_start(WEB_URL) in raw
    assert_wrapped(WEB_URL, "web link on the Comments tab")

    print("ok — task link (Description) and web link (Comments) each wrapped in a bounded OSC-8 hyperlink")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
