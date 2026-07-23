#!/usr/bin/env python3
"""Instant closed-task bridge-paint validation (#333, follow-up to #253/#280).

When F12 cycles the main list to *All*, TodoApp splices the warm closed-task set into the
snapshot (TaskService.SupplementWithClosed) and paints it *before* the authoritative
include_closed=true refresh returns — so closed rows appear instantly instead of after a
fetch stall. This asserts that pre-refresh bridge frame end-to-end via an A/B that isolates
the bridge from the authoritative refresh:

  • Warm leg   (E2E_WARM_CLOSED=1): the cache is warmed before boot, and the authoritative
    F12→All refresh is stalled. During the stall the closed row is ALREADY on screen — the
    only thing that could have painted it is the bridge. After the stall it stays (the
    authoritative superset converges without a flicker-out).

  • Control leg (no warm): same stall, empty warm set. During the stall the closed row is
    ABSENT (the bridge is a no-op); only after the stall — when the authoritative refresh
    lands — does it appear. This proves the warm leg's early row is the bridge, not the
    refresh firing early.

The stall (E2E_STALL_CLOSED_MS) dwarfs the pyte read cadence, so "during"/"after" captures
land deterministically inside/outside the window — no race.

Usage: closed_bridge_check.py <ClickUpTodo.Tui.E2E.dll>
"""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

STALL_MS = 3000            # server-side delay on the authoritative include_closed refresh
DURING = 1.2               # capture this long after F12→All — comfortably inside the stall
AFTER = STALL_MS / 1000 + 2.0  # then wait past the stall for the authoritative frame

F12 = b"\x1b[24~"          # main-list completed-view cycle: Active → WithDone → All
CLOSED_ROW = "Closed ticket"   # distinctive fragment of the seeded closed task's title
ALL_FLAG = "+done & closed"    # frame-title flag shown only in the All completed view


def answer(master, data):
    if b"\x1b[18t" in data:
        os.write(master, b"\x1b[8;%d;%dt" % (ROWS, COLS))
    if b"\x1b[6n" in data:
        os.write(master, b"\x1b[1;1R")
    if b"\x1b[0c" in data or b"\x1b[c" in data:
        os.write(master, b"\x1b[?62;22c")


def run_leg(name, warm):
    """Boots the harness, cycles to All, and returns (during_view, after_view)."""
    screen = pyte.Screen(COLS, ROWS)
    stream = pyte.ByteStream(screen)
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
    env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
               E2E_TASKS="12", E2E_REFRESH="600", E2E_STALL_CLOSED_MS=str(STALL_MS))
    if warm:
        env["E2E_WARM_CLOSED"] = "1"
    proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                            env=env, close_fds=True, preexec_fn=os.setsid)
    os.close(slave)

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
                answer(master, chunk)
                stream.feed(chunk)

    def visible():
        return "\n".join(line.rstrip() for line in screen.display).rstrip()

    def fail(msg, view):
        print(f"FAIL [{name}]:", msg)
        print("---- visible screen ----")
        print(view)
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        sys.exit(1)

    try:
        pump(9.0)
        v = visible()
        if "Task" not in v:
            fail("dashboard did not boot", v)
        # Below All (default Active view) the closed task is never in the snapshot. This
        # precondition relies on the harness building a fresh Active AppConfig every boot
        # (Program.cs) and TodoApp not reloading persisted config — the warm leg does
        # Save(Completed=All) via the shared ConfigStore, so a future boot-time config
        # reload would break this line, not the bridge itself.
        if CLOSED_ROW in v:
            fail("closed row showed before F12 reached All", v)

        # F12 → WithDone (pure client-side re-render, no fetch), then F12 → All (bridge +
        # authoritative refresh, which the harness stalls).
        os.write(master, F12); pump(0.6)
        os.write(master, F12); pump(DURING)
        during = visible()
        if ALL_FLAG not in during:
            fail(f"frame title did not show '{ALL_FLAG}' after cycling to All", during)

        # Let the stall elapse so the authoritative include_closed=true refresh lands.
        pump(AFTER)
        after = visible()
        return during, after
    finally:
        try:
            os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        except Exception:
            pass


def expect(cond, msg, view):
    if not cond:
        print("FAIL:", msg)
        print("---- visible screen ----")
        print(view)
        sys.exit(1)


# ── Warm leg: the bridge paints the closed row before the refresh returns ──────────────
w_during, w_after = run_leg("warm", warm=True)
expect(CLOSED_ROW in w_during,
       "warm leg: closed row was NOT on the pre-refresh frame — the F12→All bridge paint "
       "did not splice the warm set (SupplementWithClosed)", w_during)
expect(CLOSED_ROW in w_after,
       "warm leg: closed row vanished after the authoritative refresh landed (should be a "
       "superset, never a flicker-out)", w_after)

# ── Control leg: without a warm set the row appears only after the refresh ──────────────
c_during, c_after = run_leg("control", warm=False)
expect(CLOSED_ROW not in c_during,
       "control leg: closed row appeared during the stall with an empty warm set — the "
       "'during' capture is racing the authoritative refresh, or the bridge is pulling from "
       "somewhere other than the warm cache", c_during)
expect(CLOSED_ROW in c_after,
       "control leg: closed row never appeared even after the authoritative include_closed "
       "refresh — the F12→All fetch path is broken", c_after)

print("ok — F12→All paints the warm closed set on the pre-refresh frame (bridge), and the "
      "authoritative refresh converges to a superset; without a warm set the row appears "
      "only after the refresh")
