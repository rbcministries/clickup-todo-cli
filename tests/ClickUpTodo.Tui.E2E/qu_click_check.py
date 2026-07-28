#!/usr/bin/env python3
"""Drives the Quick Updates MOUSE click-to-apply path (#288): a single left-click on a row does the
same as select + Enter. Two boots, each against the fake backend that models the relevant write.
Run it as documented with NO operator-supplied env — each phase sets the backend knob it needs on
its own boot, so an ambient E2E_FOREIGN=1 is neither required nor safe (Phase B forces it off):

  Phase A (boots with E2E_FOREIGN=1 — the only scenario that models a Status/Priority PUT truthfully,
    #232): open Quick Updates (Ctrl+U) on the assigned parent pt1 → click the "in progress" Status
    row → its ✓ moves there (the modelled PUT round-trips and ApplyStatus reconciles ✓ to the server
    value) → click a blank row below the last status → no-op (✓ stays put, never applies the nearest
    row) → click the "Urgent" Priority row → its ✓ moves there.

  Phase B (boots with the default backend, E2E_FOREIGN forced off — models the Assignee PUT on a
    shared mutable set): open Quick Updates (Ctrl+U) → click a candidate in the Assignees pane → ✓
    appears (add round-trips) → click that ✓ row → ✓ clears (remove round-trips).

Mouse is injected as SGR-1006 sequences (ESC[<b;x;yM/m) written to the PTY; the Terminal.Gui ansi
driver enables mouse reporting on boot, so we only emit the click bytes at the screen cell where the
target row is rendered (found by scanning the pyte screen — robust to the exact pane layout). No
network: the same in-process fake backend as the other checks."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

CTRL_U = b"\x15"   # Quick Updates — standardized to Ctrl+U (#159/#290); was Space before #290


def boot(extra_env):
    screen = pyte.Screen(COLS, ROWS)
    stream = pyte.ByteStream(screen)
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
    env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet", **extra_env)
    proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                            env=env, close_fds=True, preexec_fn=os.setsid)
    os.close(slave)
    return screen, stream, master, proc


def make_io(screen, stream, master):
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
        return "\n".join(line.rstrip() for line in screen.display)

    def lines():
        return [line.rstrip() for line in screen.display]

    def send(seq, wait=1.2):
        os.write(master, seq)
        pump(wait)

    def cell_of(substr):
        """(col, row) 0-based of the first screen line containing `substr`, at the substr's column —
        a cell inside the rendered row, so a click there lands on that ListView row."""
        for y, line in enumerate(screen.display):
            if substr in line:
                return line.index(substr), y
        return None, None

    def click_row(substr, gap=0.0):
        col, row = cell_of(substr)
        assert row is not None, f"could not find a row containing {substr!r} on screen:\n{visible()}"
        click_xy(col, row)

    def click_xy(col, row):
        seq = b"\x1b[<0;%d;%d%s"
        os.write(master, seq % (col + 1, row + 1, b"M"))   # press (SGR, 1-based, button 0 = left)
        os.write(master, seq % (col + 1, row + 1, b"m"))   # release
        pump(2.5)

    def marked(name):
        return any(name in ln and "✓" in ln for ln in lines())

    def qu_open():
        v = visible()
        return "Quick Updates" in v and "Priority" in v and "Assignees" in v

    return pump, visible, lines, send, cell_of, click_row, click_xy, marked, qu_open


def kill(proc):
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass


# ── Phase A: Status + Priority click-to-apply (E2E_FOREIGN=1 models the PUT truthfully) ──────────────
def phase_a():
    screen, stream, master, proc = boot({"E2E_FOREIGN": "1"})
    pump, visible, lines, send, cell_of, click_row, click_xy, marked, qu_open = make_io(screen, stream, master)
    PT1 = "Assigned parent — my task AA"
    try:
        pump(8.0)
        assert "Assigned parent" in visible() or "Personal Tasks" in visible(), \
            "foreign scenario never rendered:\n" + visible()

        # Open Quick Updates on the assigned parent (a normal top-level row, always visible). Ctrl+U
        # opens QU on the cursor row; retry down the list until its title is pt1.
        for _ in range(8):
            send(CTRL_U, 2.0)
            if qu_open() and PT1 in visible():
                break
            if qu_open():
                send(b"\x1b", 1.2)
            send(b"\x1b[B", 0.6)
        assert qu_open() and PT1 in visible(), f"could not open Quick Updates on pt1:\n{visible()}"
        assert marked("to do"), f"status pane did not mark the current status 'to do':\n{visible()}"
        print("OPEN ok — Quick Updates on pt1, ✓ on 'to do'")

        # Click the "in progress" Status row → apply + reconcile moves the ✓ there (truthful round-trip).
        click_row("in progress")
        assert marked("in progress"), f"click did not apply/round-trip 'in progress':\n{visible()}"
        assert not marked("to do"), f"the old status kept its ✓ after the click:\n{visible()}"
        print("STATUS CLICK ok — ✓ moved to 'in progress' (round-tripped)")

        # Click a BLANK row two lines below the last status → resolves to no row → no-op (✓ stays put).
        _, complete_row = cell_of("complete")
        col, _ = cell_of("in progress")
        assert complete_row is not None, "status pane missing the 'complete' row"
        click_xy(col, complete_row + 2)
        assert qu_open(), f"empty-space click lost the screen:\n{visible()}"
        assert marked("in progress"), f"empty-space click wrongly changed the status:\n{visible()}"
        print("EMPTY-SPACE ok — click below the last status no-ops")

        # Click the "Urgent" Priority row (clicking focuses that pane) → its ✓ moves there.
        click_row("Urgent")
        assert marked("Urgent"), f"click did not apply/round-trip 'Urgent':\n{visible()}"
        print("PRIORITY CLICK ok — ✓ moved to 'Urgent' (round-tripped)")
        print("PHASE A: PASS")
    finally:
        kill(proc)


# ── Phase B: Assignees click add/remove (default backend models the assignee PUT) ────────────────────
def phase_b():
    # Force E2E_FOREIGN off (not just absent) so an ambient E2E_FOREIGN=1 — which the Phase A note
    # invites an operator to set — can't leak in and swap out the default Assignee-PUT backend (#409).
    screen, stream, master, proc = boot({"E2E_FOREIGN": "0"})
    pump, visible, lines, send, cell_of, click_row, click_xy, marked, qu_open = make_io(screen, stream, master)
    try:
        pump(8.0)
        assert "Task" in visible(), "default scenario never rendered:\n" + visible()[-1500:]
        send(CTRL_U, 2.0)
        assert qu_open(), f"Quick Updates did not open:\n{visible()}"

        # The Assignees empty state (bottom frame) shows the seeded frequency-pool candidates. Click
        # "Grace Hopper" (an unselected candidate) → immediate-apply add → she gains a ✓.
        assert not marked("Grace Hopper"), f"Grace Hopper is unexpectedly pre-selected:\n{visible()}"
        click_row("Grace Hopper")
        assert marked("Grace Hopper"), f"click did not add Grace Hopper (✓):\n{visible()}"
        assert qu_open(), f"screen lost after add-click:\n{visible()}"
        print("ASSIGNEE ADD ok — click added Grace Hopper (✓, write round-tripped)")

        # Click her ✓ row again → immediate-apply remove → the ✓ clears.
        click_row("Grace Hopper")
        assert not marked("Grace Hopper"), f"click did not remove Grace Hopper's ✓:\n{visible()}"
        assert qu_open(), f"screen lost after remove-click:\n{visible()}"
        print("ASSIGNEE REMOVE ok — click removed Grace Hopper's ✓")
        print("PHASE B: PASS")
    finally:
        kill(proc)


phase_a()
phase_b()
print("QU CLICK E2E: PASS")
