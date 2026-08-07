#!/usr/bin/env python3
"""Boots the TUI under a PTY and exercises the Quick Updates *List* pane (#242/#365), now
RE-ENABLED behind the field-strand handling.

With E2E_QU_LISTS=1 the fake backend pre-seeds the task into one additional list
("Q3 Website Refresh" / list2) alongside its home list ("Personal Tasks" / plist), gives that
additional list a *local* "Sprint Points" Custom Field the home list doesn't define, and serves
the task detail with values for both "Sprint Points" (list-local) and "Notes" (shared). So
removing the additional list strands the Sprint Points value; removing the home list is a move
(blocked).

Flow: Ctrl+U opens Quick Updates → the (enriched) Lists pane shows the home list marked
"(home)" and the additional "Q3 Website Refresh" → Tab to the Lists pane → Down onto the
additional-list row and Enter → the remove is NOT written: the pane flashes that "Sprint Points"
would be hidden and *arms* a confirmation, the row staying selected → Enter again confirms and the
remove round-trips (the row loses its ✓, read back from the server) → then Enter on the "(home)"
row is refused with a flash and the home stays. Esc returns to the list.

Validates the surface #365 adds on top of #242: the field-strand preflight
(GetListCustomFieldsAsync per list + ListMembershipMigration) drives an arm/confirm on the status
line rather than a silent write, the home list can't be removed, and a confirmed remove reaches the
membership DELETE facade and reconciles the pane from the server set."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)

master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
           E2E_QU_LISTS="1", E2E_REFRESH="600")
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

try:
    pump(8.0)
    assert "Task" in visible(), "app never rendered the list:\n" + visible()[-1500:]
    pump(1.0)

    # Ctrl+U opens Quick Updates (#290). Give the background enrich time to fetch the task's
    # additional locations and merge them into the pane (list-origin launch).
    send(b"\x15", 2.5)
    v = visible()
    assert "Quick Updates" in v, "Quick Updates did not open:\n" + v
    for token in ("Status", "Priority", "Assignees", "Lists"):
        assert token in v, f"missing {token!r} pane after opening Quick Updates:\n{v}"
    assert "(home)" in v, f"the home list is not marked '(home)':\n{v}"
    assert "Q3 Website Refresh" in v, f"the additional list did not enrich into the pane:\n{v}"
    print("OPEN ok — Lists pane present with (home) + the additional list")

    # Tab three times: Status -> Priority -> Assignees -> Lists. Focus lands in the Lists search box.
    send(b"\t", 0.7)
    send(b"\t", 0.7)
    send(b"\t", 0.7)

    # Down moves focus into the list onto row 0 — the "(home)" row (a list-origin open seeds the home
    # list first, then enriches the additional locations after it). Do the home guard here, where row 0
    # is deterministically the home row.
    send(b"\x1b[B", 0.7)   # into the list, row 0 (home)
    send(b"\r", 2.0)       # attempt to remove the home list -> refused (it's a move)
    v = visible()
    assert "home list" in v.lower(), f"removing the home list was not refused with a flash:\n{v}"
    assert "Personal Tasks" in v, f"the home list must stay after a refused remove:\n{v}"
    print("HOME-GUARD ok — home-list remove refused, home stayed")

    # Down onto row 1 — the additional "Q3 Website Refresh" — and Enter. The remove would strand the
    # list-local "Sprint Points" value, so it's NOT written: it flashes the field and arms a confirm.
    send(b"\x1b[B", 0.7)   # row 1 (Q3 Website Refresh)
    send(b"\r", 2.5)       # attempt remove -> strands "Sprint Points" -> arm (no write)
    v = visible()
    assert "Sprint Points" in v, f"stranding remove did not flash the affected field:\n{v}"
    assert "again" in v.lower(), f"stranding remove did not arm a confirmation:\n{v}"
    assert "Q3 Website Refresh" in v, f"arming should NOT remove the list (row must stay):\n{v}"
    print("ARM ok — remove flagged 'Sprint Points' and armed instead of writing")

    # Second Enter on the same row confirms; the remove now round-trips (DELETE membership) and the
    # pane reconciles from the server set — the additional list loses its ✓ (drops to an unselected
    # candidate, or off the list entirely).
    send(b"\r", 2.5)
    v = visible()
    assert "✓ Q3 Website Refresh" not in v, f"confirmed remove did not drop the additional list:\n{v}"
    print("CONFIRM ok — second Enter removed the additional list (round-tripped)")

    # Esc returns to the task list.
    send(b"\x1b", 1.5)
    v = visible()
    assert "Quick Updates" not in v, f"Esc did not close Quick Updates:\n{v}"
    assert "Task" in v, f"did not return to the task list after Esc:\n{v}"
    print("ESC ok — returned to the task list")
    print("QU LISTS E2E: PASS")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
