---
name: tick-todo
description: Close a backlog item (a GitHub issue since 2026-09-24, when TODO.md and docs/todo/*.md became issues) and propagate the update to CLAUDE.md, docs/plans/*.md and related memory entries. Use when the user asks to tick off, check off, mark done, or close out a TODO or backlog item.
---

Usage: `/tick-todo <search text or issue number>`. Examples: `DrawEllipse`, `#361`, `Mosaic panel support`.

**The backlog is GitHub issues**, labelled by area (`area:astrometry`, `area:drivers`, `area:guider`,
`area:imaging`, `area:infra`, `area:sequencing`, `area:ui`), plus `bench` (needs a real device or a
real night), `needs-triage` (the old inbox), and `priority:high` / `priority:next`. `from-todo` marks
the 2026-09-24 migration. `TODO.md` and `docs/todo/*.md` hold only the DONE archive and never a new
open box: an open box written there is the drift this move removed.

Steps:
1. Find the issue: `gh issue view <n>`, or `gh issue list --state open --search "<text>" --limit 20`.
   Several matches: show them and ask which one.
2. **Prefer closing through the PR that did the work** (`Closes #n` in its body, so the merge closes
   it). Closing by hand is for work that landed without one, or for an item that is moot:
   `gh issue close <n> --comment "<what did it, as a PR number or commit subject in quotes, never a hash>"`,
   and `--reason "not planned"` for a moot or refuted item.
3. Check whether `CLAUDE.md` describes the feature and needs updating.
4. Check the related `docs/plans/*.md` and mark its phase or step done; update `docs/plans/summary.md`
   if the plan's status changed.
5. Check the memory directory for project entries to update.
6. Show the user what changed.

Do NOT commit; let the user review the changes first.
