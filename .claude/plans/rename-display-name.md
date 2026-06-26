# Plan: Rename app display name to "ClickUp Simple CLI" (#20)

## Problem

"To-Do" is a poor display name because **`to do` is also a common task status**, so the word
shows up twice and is confusing (e.g. the `─ TO-DO ─` section header sits right next to `[to do]`
status badges).

## Decision / scope

Implement the **user-facing display-name rename only** to **"ClickUp Simple CLI"**. The issue
flags the bigger identifier renames (CLI command, NuGet package, repo, namespace) as
"bigger / breaking — flag before doing" and itself **recommends keeping them**. So:

- **Change** (user-facing copy): window title, non-pinned section header, frame subtitle wording,
  setup-wizard banner, `--help` text, package `<Description>`/`<Product>`, README branding.
- **Keep** (code identifiers, per the issue's recommendation): CLI command `clickup-todo`,
  `AssemblyName` `clickup-todo`, NuGet `PackageId` `ClickUpTodo.Cli`, `RootNamespace` `ClickUpTodo`,
  repo name `clickup-todo-cli`, config dir `clickup-todo`, `PackageTags` (keep `todo` for search).

The breaking identifier-rename **decision is deferred to the maintainer** → tracked by a new
follow-up issue, linked from the PR.

## Implementation

1. **Centralize branding** in a new pure, unit-tested `AppBranding` static class
   (`src/ClickUpTodo/AppBranding.cs`): `DisplayName`, `TasksSectionLabel`, `WindowTitle(workspace)`,
   `SetupHeading`. Single source of truth so the name can't drift across the UI.
2. **`TodoApp.cs`**: window title → `AppBranding.WindowTitle(...)`; rename the `TodoHeaderPrefix`
   const → `TasksHeaderPrefix` sourced from `AppBranding.TasksSectionLabel` (`─ TASKS`); frame
   subtitle `… {n} to-do` → `… {n} other` (neutral, pairs with "pinned").
3. **`SetupWizard.cs`**: banner → `AppBranding.SetupHeading`; underline auto-sized to the heading
   length so it stays aligned.
4. **`Program.cs`**: `--help` description references the display name; drop "to-do" wording in the
   help/UI copy (keep the `clickup-todo` command name).
5. **`ClickUpTodo.csproj`**: `<Description>` leads with the display name + "task list"; add
   `<Product>ClickUp Simple CLI</Product>`. Identifiers untouched.
6. **`README.md`**: title/branding, the ASCII mock header, status-line/help copy → new name +
   `TASKS` section header.

## Tests

- New `AppBrandingTests` (xUnit): `DisplayName` is "ClickUp Simple CLI" and contains no "To-Do";
  `WindowTitle` composes `"ClickUp Simple CLI — {workspace}"`; `TasksSectionLabel` is `TASKS` and
  doesn't read like the "to do" status; `SetupHeading` derives from `DisplayName`.
- All existing tests must stay green; `dotnet format` clean; 0 warnings.

## Out of scope (deferred → follow-up issue)

Renaming the CLI command / NuGet package / repo / root namespace (breaking install + config-path
change). Decision belongs to the maintainer.
