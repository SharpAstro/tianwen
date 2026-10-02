---
name: chrome-review
description: Check a branch's UI chrome changes for layout arithmetic the engine should own - a box summed by hand beside a declared node, a device-pixel size handed to a design-unit node, a test seam that measures instead of reading the painted region. Runs the read-only chrome-review agent (Sonnet) over the diff. Use before opening a PR that touches src/TianWen.UI.*, or when asked to review viewer/GUI layout code.
---

Run the `chrome-review` agent over this branch's diff and relay what it finds. It runs on Sonnet (its own
frontmatter), which is enough for a checklist review and leaves the main session's model for the fix.

1. **Launch it** with the Agent tool, `subagent_type: "chrome-review"`, and a prompt naming the base to diff against:
   the argument if one was given (a branch, a commit, or `pr <n>`, for which diff `gh pr view <n> --json baseRefName`),
   else `origin/main`. Nothing else goes in the prompt: the checklist lives in `.claude/agents/chrome-review.md`.
2. **Relay its findings as it wrote them**, file:line first. It is a cheap model reading a diff, so before calling any
   finding a bug, open the line and confirm it; say which you confirmed and which you only passed on.
3. **Fixing is a separate step.** Offer to fix the confirmed ones; the agent never edits.

The mechanical half of the same rules runs in CI, so this is not the only guard: `DeclaredLayoutTakesDesignUnitsTests`
(a device-pixel property named in a statement that builds a node) and `ChromeMeasuresThroughTheEngineTests` (the
`MeasureText` ratchet). The agent is for what a pattern cannot see: a device-pixel value carried through a local, a box
summed beside a node, two measurements of one thing, a test seam that measures.
