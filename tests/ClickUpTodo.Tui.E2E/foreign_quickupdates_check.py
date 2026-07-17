#!/usr/bin/env python3
"""#232 end-to-end: Quick Updates edits a task that isn't the user's own work.

Runs the real app under a PTY against the opt-in E2E_FOREIGN=1 fake backend, which seeds:
  • a foreign subtask (fs1, "not assigned to you", #70) under an assigned parent, and
  • a context parent (cp1, "parent — not assigned to you", #46) pulled in for an in-view child,
plus a modelled PUT /task/{id} so a committed Status round-trips.

Asserts, on the pyte-rendered screen:
  1. both not-mine row markers render;
  2. Space OPENS Quick Updates on the foreign subtask (the #160 write-block is gone — the pre-#160
     build flashed "not assigned to you — unchanged" and never opened);
  3. committing a changed, active status settles the Status pane's ✓ on the new value — which only
     holds if the modelled PUT echoed the committed status (ApplyStatus reconciles ✓ to the
     server-confirmed value), i.e. the edit stuck;
  4. Esc returns to the list with the foreign row still present (it "stays in place", not dropped).

Exits nonzero with the offending screen on failure."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

FS1 = "Foreign teammate subtask ZZ"     # the foreign-subtask row / its Quick Updates title
CP1 = "Context parent PP"               # the context-parent row / its Quick Updates title
PT1 = "Assigned parent — my task AA"    # the assigned parent (a normal, mine row)
CT1 = "My nested subtask BB"            # the assigned child under the context parent
FOREIGN_MARKER = "· (not assigned to you)"
CONTEXT_MARKER = "· (parent — not assigned to you)"

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)

master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet", E2E_FOREIGN="1")
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
    return "\n".join(line.rstrip() for line in screen.display)


def lines():
    return [line.rstrip() for line in screen.display]


def require(cond, msg):
    if not cond:
        print("FAIL:", msg)
        print("----- screen -----")
        print(visible())
        try:
            os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        except Exception:
            pass
        sys.exit(1)


def send(seq, wait):
    os.write(master, seq)
    pump(wait)


def qu_open():
    v = visible()
    return "Quick Updates" in v and "Priority" in v and "Assignees" in v


SPACE = b" "
ESC = b"\x1b"
DOWN = b"\x1b[B"
UP = b"\x1b[A"
TAB = b"\t"
ENTER = b"\r"
CTRL_RIGHT = b"\x1b[1;5C"    # expand-all (bulk counterpart to the per-parent → fold)
F5 = b"\x1b[15~"             # manual refresh — never a delta, so it re-runs the foreign resolve


def open_qu_on(name, what):
    """Open Quick Updates on the row named `name`. Space opens QU on the selected row; if the open
    screen's title isn't that task, Esc + Down and retry (robust to row ordering). QU is a full-window
    screen, so while it's open `name` can only be the title — a reliable oracle for "on this row"."""
    for _ in range(12):
        send(SPACE, 2.5)
        if qu_open() and name in visible():
            return
        if qu_open():
            send(ESC, 1.5)
        send(DOWN, 0.8)
    require(False, f"could not open Quick Updates on {name!r} ({what}) — "
                   "a not-mine row should be editable since #160")


def marked(name):
    """True when some pane row shows `name` with the leading ✓ current-value marker."""
    return any(name in ln and "✓" in ln for ln in lines())


def fs1_row():
    return next((ln for ln in lines() if FS1 in ln), None)


try:
    pump(8.0)
    require("Personal Tasks" in visible() or "Assigned parent" in visible(),
            "app never rendered the seeded list")

    # 1. Both not-mine row kinds render. A normal parent (pt1) folds its subtasks collapsed by default,
    #    so expand-all (Ctrl+→) first to reveal the foreign subtask; a context parent (cp1) always shows
    #    its child, so its marker is present without expanding.
    require(CONTEXT_MARKER in visible(), f"context-parent marker {CONTEXT_MARKER!r} not rendered")
    send(CTRL_RIGHT, 1.5)
    require(FOREIGN_MARKER in visible(), f"foreign-subtask marker {FOREIGN_MARKER!r} not rendered after expand-all")
    print("MARKERS ok — foreign subtask + context parent both rendered")

    # 2. Opening Quick Updates on the foreign subtask is the #160 headline: the pre-#160 build flashed
    #    "not assigned to you — unchanged" and never opened.
    open_qu_on(FS1, "initial open")
    print("OPEN ok — Quick Updates opened on the foreign subtask (not blocked)")

    # 3. Commit a changed Status and Priority while the screen stays open (#207 apply-on-Enter):
    #    - Status pane preselects the current value ("to do", row 0); Down → "in progress" (row 1); Enter.
    #    - Tab to the Priority pane (preselected on "(no priority)"); Up×4 clamps to "Urgent" (row 0); Enter.
    #    The ✓ moving here is only an OPTIMISTIC reflection — ApplyStatus/ApplyPriority set it before the
    #    server responds (final = confirmed ?? committed) — so it does NOT by itself prove the PUT
    #    round-tripped; step 5 forces a model-sourced re-fetch to prove that. It does confirm the commit
    #    path fired and the screen stayed open.
    send(DOWN, 0.8)
    send(ENTER, 2.5)
    pump(1.0)
    require(qu_open(), "Quick Updates should stay open after status apply-on-Enter (#207)")
    require(marked("in progress"), "status ✓ did not move to 'in progress' after commit")
    send(TAB, 0.8)
    for _ in range(4):
        send(UP, 0.4)
    send(ENTER, 2.5)
    pump(1.0)
    require(marked("Urgent"), "priority ✓ did not move to 'Urgent' after commit")
    print("COMMIT ok — status 'in progress' + priority 'Urgent' committed (optimistic ✓)")

    # 4. Esc back to the list; the foreign row is still there (stays in place, not dropped), reflects the
    #    committed status in place — "in progress" → the (IP) abbreviation chip, unique to fs1 (pt1/ct1
    #    are (TD), cp1 is (IR)) — AND keeps its "(not assigned to you)" marker. The in-place update
    #    (UpdateTaskRow → ClassifyRowMarker → BuildRow) re-derives that flag exactly as the full Render
    #    path does (the #264 fix, PR #277 on main); #160 lifts only the write restriction, not the
    #    context labelling, so an edit must not drop the marker.
    send(ESC, 2.0)
    require(not qu_open(), "Esc did not close Quick Updates")
    row = fs1_row()
    require(row is not None, "the foreign-subtask row was dropped from the list after the edit")
    require("(IP)" in row, f"the committed status is not reflected on the foreign row in place: {row!r}")
    require(FOREIGN_MARKER in row,
            f"the in-place edit dropped the foreign row's '(not assigned to you)' marker (#264): {row!r}")
    print("STAYS ok — foreign row present, shows the committed status in place, keeps its marker")

    # 5. THE ROUND-TRIP PROOF. Force a manual refresh (F5 — never a delta) so the per-parent foreign fetch
    #    (GET /task/{id}?include_subtasks=true) re-serves fs1 from the fake's PERSISTED model
    #    (_foreignStatus/_foreignPriority), replacing every optimistic value. Had the modelled PUT not
    #    persisted the commit, fs1 would re-serve its seed ("to do", no priority) and these would fail.
    #    Re-expand (idempotent) in case the refresh reset the fold; the re-rendered row also carries the
    #    foreign marker again (full render path), and reopening QU reads the model-sourced current values.
    send(F5, 4.0)
    send(CTRL_RIGHT, 1.5)
    row = fs1_row()
    require(row is not None, "foreign row missing after refresh")
    require(FOREIGN_MARKER in row,
            f"foreign marker did not return on the full re-render after refresh: {row!r}")
    require("(IP)" in row,
            f"status did NOT round-trip: after a model-sourced re-fetch fs1 is not 'in progress': {row!r}")
    open_qu_on(FS1, "reopen after refresh")
    require(marked("in progress"),
            "status did NOT round-trip: reopened Quick Updates (seeded from the re-fetched task) "
            "does not mark 'in progress' as current")
    require(marked("Urgent"),
            "priority did NOT round-trip: reopened Quick Updates does not mark 'Urgent' as current")
    send(ESC, 1.5)
    print("ROUND-TRIP ok — status + priority persisted through the modelled PUT and re-served on refresh")

    # 6. The context parent is a not-mine row too (#46): Quick Updates must open on it as well (the #160
    #    guard is gone for both kinds). Then confirm the edits left every other seeded row in place.
    open_qu_on(CP1, "context parent")
    require(CP1 in visible(), "Quick Updates did not open on the context parent")
    send(ESC, 1.5)
    require(not qu_open(), "Esc did not close the context-parent Quick Updates")
    v = visible()
    for name in (PT1, CT1, CP1):
        require(name in v, f"a seeded row was dropped from the list after the edits: {name!r}")
    require(FS1 in v, "the foreign-subtask row was dropped from the list after the edits")
    print("CONTEXT-PARENT ok — Quick Updates opens on the context parent; no rows dropped")

    print("FOREIGN QUICK UPDATES E2E: PASS")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
