#!/usr/bin/env python3
"""Task Detail Other tab: per-type custom-field editing (#587 §3). Boots the dashboard, opens a task's
detail, cycles to the Other tab, and drives the §3 activation gestures the slice adds — asserting the
writes actually reach the backend (captured by CustomFieldWriteLogScenario) keyed to the right field:

  1. Space on the highlighted `checkbox` field (Reviewed, seeded "false") POSTs value `true` to its id —
     the optimistic toggle → host callback → POST body path, end to end.
  2. Enter on a `short_text` field (Ticket ref, seeded "OPS-4271") opens the single-line value editor
     pre-filled with the round-trippable current value; editing + Enter POSTs the new string to its id
     (the seed survives in the body, proving the editor sourced the real value, not a truncated display).
  3. Enter on a `drop_down` field (Severity) is the deferred option-picker path — it flashes a "edit in
     ClickUp" notice and writes nothing (no POST/DELETE for that field id).

Requires E2E_TASK_CUSTOM_FIELDS=1 (DetailCustomFieldsScenario seeds the fields) and E2E_CUSTOM_FIELD_LOG
(CustomFieldWriteLogScenario records `{fieldId}\t{SET|CLEAR}\t{body}` per write). The pure per-type routing
/ serialization is unit-tested in CI (CustomFieldActivationTests / CustomFieldValueSerializerTests); this is
the rendered end-to-end proof the Other tab drives them and the writes reach the wire."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess, tempfile
import pyte

ROWS, COLS = 40, 110
DLL = sys.argv[1]

CTRL_RIGHT = b"\x1b[1;5C"
DOWN = b"\x1b[B"
UP = b"\x1b[A"
RIGHT = b"\x1b[C"
ENTER = b"\r"
ESC = b"\x1b"

HEADING = "Custom fields:"
FIELD_NAMES = ["Reviewed", "Ticket ref", "Severity", "Computed total"]

LOG = tempfile.NamedTemporaryFile(prefix="cf_writes_", suffix=".log", delete=False).name
open(LOG, "w").close()

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
           E2E_TASK_CUSTOM_FIELDS="1", E2E_CUSTOM_FIELD_LOG=LOG, E2E_TASKS="6", E2E_REFRESH="600")
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
    """The field name on the focus-highlighted body row (most non-default background below the heading)."""
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


def writes():
    with open(LOG) as f:
        return [ln.rstrip("\n") for ln in f if ln.strip()]


def select_field(name):
    """Move the selection to the named field row (down then up, bounded)."""
    for _ in range(len(FIELD_NAMES) + 1):
        if selected_field() == name:
            return True
        send(DOWN, 0.4)
    for _ in range(len(FIELD_NAMES) + 1):
        if selected_field() == name:
            return True
        send(UP, 0.4)
    return selected_field() == name


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
    assert HEADING in visible(), f"the Other tab did not render custom fields:\n{visible()}"

    # 1) Space on the checkbox field POSTs value true to its id.
    assert select_field("Reviewed"), f"could not select the 'Reviewed' checkbox row:\n{visible()}"
    send(b" ", 1.0)
    cf1 = [w for w in writes() if w.startswith("cf1\t")]
    assert cf1, f"Space on the checkbox field wrote nothing to cf1:\n{writes()}\n{visible()}"
    assert "SET" in cf1[-1] and "true" in cf1[-1], \
        f"the checkbox toggle did not POST value true to cf1 (got {cf1[-1]!r})"

    # 2) Enter on the short_text field opens the value editor seeded with the current value; edit + Enter
    #    POSTs the new string (the seed survives in the body, proving a round-trippable prefill).
    assert select_field("Ticket ref"), f"could not select the 'Ticket ref' row:\n{visible()}"
    send(ENTER, 1.0)
    v = visible()
    assert "Edit Ticket ref" in v, f"Enter did not open the value editor for 'Ticket ref':\n{v}"
    assert "OPS-4271" in v, f"the value editor was not pre-filled with the current value:\n{v}"
    # Move the cursor to the end (collapsing Terminal.Gui's select-all-on-focus) so the keystroke appends
    # to the seed rather than replacing it — then the POST body carries the round-tripped "OPS-4271".
    for _ in range(len("OPS-4271") + 2):
        send(RIGHT, 0.05)
    send(b"9", 0.6)           # make it dirty so Save actually writes
    send(ENTER, 1.0)
    cf2 = [w for w in writes() if w.startswith("cf2\t")]
    assert cf2, f"editing the short_text field wrote nothing to cf2:\n{writes()}\n{visible()}"
    assert "SET" in cf2[-1] and "OPS-4271" in cf2[-1], \
        f"the edited value POST did not carry the round-tripped seed (got {cf2[-1]!r})"

    # 3) Enter on the drop_down field is the deferred option path: a flash, and no write for cf3.
    assert select_field("Severity"), f"could not select the 'Severity' drop_down row:\n{visible()}"
    send(ENTER, 1.0)
    assert not [w for w in writes() if w.startswith("cf3\t")], \
        f"the deferred drop_down field must not write (got {writes()})"
    assert "ClickUp" in visible(), \
        f"Enter on the deferred drop_down field did not flash a notice:\n{visible()}"

    print("ok — checkbox Space POSTs true (cf1); short_text Enter opens the seeded editor and edits POST "
          "(cf2); drop_down Enter is deferred (no cf3 write)")
    print("OTHER TAB EDIT E2E: PASS")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
    try:
        os.unlink(LOG)
    except Exception:
        pass
