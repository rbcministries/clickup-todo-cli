# Mention coverage: the mention→assignee automation prerequisite

The **Mentions & Comments feed** (open it with `Ctrl+E`) is built entirely from
the ClickUp REST API, which has **no inbox or notifications endpoint**. The app
synthesises the feed client-side by fanning out over the comments on the tasks
that are **assigned to you** and flagging the ones that `@`-mention you.

That has one consequence worth understanding: if a teammate `@`-mentions you on
a task you are **not assigned to**, that task never enters the assigned-tasks
pipeline, so the mention can't appear in the feed. ClickUp exposes no way to
query "tasks where I was mentioned," so the app cannot close this gap on its
own.

The reliable workaround is a small **ClickUp Automation** that turns a mention
into an assignment. Once a mention assigns you, the task flows into
`GetAssignedTasksAsync` like any other, and both the feed and mention detection
pick it up **from that point forward**.

This automation is a **user prerequisite**: the app cannot create it, enforce
it, or even verify it is enabled (there is no public automations API). This page
documents how to set it up and the trade-offs to weigh first.

## The automation

Create a per-Space automation with:

| Part          | Value                                    |
| ------------- | ---------------------------------------- |
| **Trigger**   | Comment added                            |
| **Condition** | Follower *is any of* `[you]`             |
| **Action**    | Update assignees → Add `[you]`           |

ClickUp adds a commenter/mentioned user to a task's **Followers**, so the
"Follower is any of you" condition fires whenever you are mentioned (or you
comment), and the action then assigns you.

### Setup steps

1. Open the **Space** you want covered → **Settings (⋯) → Automations**.
2. **Add Automation → Create custom automation**.
3. Set the **Trigger** to **Comment added**.
4. Add a **Condition**: **Follower** *is any of* → pick **yourself**.
5. Add an **Action**: **Update assignees** → **Add** → pick **yourself**.
6. Save and enable it.

### Validated evidence

This was confirmed against a live task: commenting on `EA-6737` added the user
to the task's **Followers** *and* assigned them. Because assignees are
API-queryable (and already fetched by the feed's assigned-tasks pull), the
mentioned task then appears in the feed and mention detection (see #113)
operates on a reliable substrate.

## Caveats (read before enabling)

- **Per-Space, not workspace-wide.** The automation lives in a single Space. You
  must create it in **each Space** whose mentions you want in the feed — there
  is no workspace-level equivalent.
- **Permission-gated.** Creating an automation requires automation permission in
  that Space. If you don't have it, a Space **admin** must create it for you.
- **Paid feature.** Automations are a paid ClickUp feature with **per-plan
  monthly execution limits**; heavy comment volume can exhaust the quota.
- **Not retroactive.** It only affects mentions made **after** it is enabled.
  Existing/older mentions won't back-fill into the feed.
- **Blast radius.** The condition fires on **every comment** to **any task you
  follow** — so it will assign you broadly. That pollutes your real ClickUp
  **"Assigned to me"** everywhere, not just inside this app. Narrow the
  condition (or scope the Space) if that's a problem.
- **Unverifiable via API.** There is no public automations API, so the app
  **cannot** detect whether you've enabled this, confirm it's still on, or warn
  you if it's missing. It is a documented prerequisite, not an enforceable gate
  — an empty or thin feed may simply mean the automation isn't set up in that
  Space.

## Where the app points here

- The feed's **empty state** (and its "mentions only" empty state) carries a
  short note that mention coverage depends on this automation, linking here.
- The in-app **Help** screen (`F1`) references the prerequisite next to the
  `Ctrl+E` entry.
