---
name: tick-todo
description: Mark a TODO item done in TODO.md and propagate updates to CLAUDE.md, PLAN-*.md files, and related memory entries. Use when the user asks to tick off, check off, mark done, or close out a TODO item.
---

Usage: `/tick-todo <search text>` or pass the search text as an argument. Examples: `DrawEllipse`, `Mosaic panel support`.

Steps:
1. Search `TODO.md` for the item matching the given text
2. Change `- [ ]` to `- [x]` for the matched item
3. If the item has a brief description, optionally expand it with what was done
4. Check if the item is mentioned in `CLAUDE.md` and update if needed
   (e.g. architecture docs that reference the feature)
5. Check if there's a related `PLAN-*.md` file and mark the corresponding
   phase/step as done
6. Check memory files in the `.claude/` memory directory for related project
   entries that should be updated (e.g. move from "todo" to "done")
7. Show the user what was changed across all files

Do NOT commit - let the user review the changes first.

If the search text matches multiple items, show all matches and ask the user
to clarify which one.
