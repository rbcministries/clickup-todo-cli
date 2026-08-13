#!/usr/bin/env python3
"""Task Detail Other tab: the navigable custom-field row model (#587 §2). Boots the dashboard, opens a
task's detail, cycles to the Other tab (index 3), and asserts the row model the §2 slice adds:

  1. The custom-fields body renders as per-field rows — the `Custom fields:` heading plus one row per
     seeded field (Reviewed / Ticket ref / Severity / Computed total) — not one opaque text blob.
  2. A bare ↓ moves the focus-highlighted row down one field, and a bare ↑ moves it back — proving the
     body is a navigable ListView driven through the detail screen's MoveActiveTab (the same key path
     the Task Tree / Checklists tabs use), which was completely inert on the old read-only TextView body.
  3. The moves never switch tabs (the field rows stay on screen) — the NavSafe boundary contract, so a
     bare arrow moves the selection *within* the tab and is a consumed no-op at a content edge.

Requires E2E_TASK_CUSTOM_FIELDS=1 (DetailCustomFieldsScenario) so the detail read carries a seeded
`custom_fields` array — off, the Other tab is the `(none)` empty state with no field rows to move over.
The row projection / selectability itself is unit-tested in CI (CustomFieldOtherTabArrangerTests, #602);
this is the rendered end-to-end proof the view drives it. Fails on the pre-#587-§2 read-only TextView
body (the field names render as wrapped text, but no row is focus-highlighted and bare ↓/↑ don't move a
selection)."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 40, 110
DLL = sys.argv[1]

CTRL_RIGHT = b"\x1b[1;5C"
DOWN = b"\x1b[B"
UP = b"\x1b[A"
ENTER = b"\r"

HEADING = "Custom fields:"
FIELD_NAMES = ["Reviewed", "Ticket ref", "Severity", "Computed total"]

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
           E2E_TASK_CUSTOM_FIELDS="1", E2E_TASKS="6", E2E_REFRESH="600")
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


def visible():
    return "\n".join(screen.display[y].rstrip() for y in range(ROWS))


def send(seq, wait=1.0):
    os.write(master, seq)
    pump(wait)


def heading_y():
    for y in range(ROWS):
        if HEADING in screen.display[y]:
            return y
    return -1


def selected_field():
    """The field name on the focus-highlighted body row: among the rows at/below the `Custom fields:`
    heading that carry a seeded field name, the one whose cells have the most non-default background (the
    ListView focus fill), or None if no row stands out. Scanning below the heading avoids the coloured
    Priority/Status header attributes above it (which also carry a non-default background)."""
    hy = heading_y()
    if hy < 0:
        return None
    best_name, best_bg = None, 0
    for y in range(hy, ROWS):
        t = screen.display[y]
        name = next((n for n in FIELD_NAMES if n in t), None)
        if name is None:
            continue
        nd = sum(1 for x in range(1, COLS - 1) if screen.buffer[y][x].bg != "default")
        if nd > best_bg:
            best_bg, best_name = nd, name
    return best_name if best_bg > 15 else None


def boot():
    end = time.monotonic() + 18.0
    while time.monotonic() < end:
        pump(1.0)
        if "Task 0" in visible():
            pump(1.0)
            return
    raise AssertionError("app never rendered the list:\n" + visible()[-1500:])


def open_other_tab():
    send(ENTER, 3.0)          # open the focused task (t0) in Task Detail
    for _ in range(3):        # Stream -> Description -> Comments -> Other
        send(CTRL_RIGHT, 0.4)
    pump(1.0)


try:
    boot()
    open_other_tab()

    # 1) The custom-fields body renders as per-field rows.
    v = visible()
    assert HEADING in v, f"the 'Custom fields:' heading did not render on the Other tab:\n{v}"
    for name in FIELD_NAMES:
        assert name in v, f"custom field '{name}' did not render as a row on the Other tab:\n{v}"

    # 2) The tab is a navigable ListView: a row is focus-highlighted and bare ↓/↑ move the selection.
    start = selected_field()
    assert start is not None, \
        f"no custom-field row is focus-highlighted — the Other tab is not a navigable ListView:\n{visible()}"

    send(DOWN, 0.8)
    down1 = selected_field()
    assert down1 is not None and down1 != start, \
        f"bare ↓ did not move the Other-tab selection (start={start!r}, after ↓={down1!r}):\n{visible()}"

    send(UP, 0.8)
    up1 = selected_field()
    assert up1 == start, \
        f"bare ↑ did not move the Other-tab selection back (start={start!r}, after ↑={up1!r}):\n{visible()}"

    # 3) NavSafe: the moves stayed on the Other tab — every field row is still on screen, no tab switch.
    v = visible()
    for name in FIELD_NAMES:
        assert name in v, f"a bare arrow switched away from the Other tab (lost '{name}'):\n{v}"

    print(f"ok — Other tab renders navigable custom-field rows; bare ↓/↑ move the selection "
          f"({start!r} -> {down1!r} -> {up1!r}) and stay on the tab")
    print("OTHER TAB NAV E2E: PASS")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
