---
name: No tail pipe on build/test output
description: Do not pipe build/test output through tail — show full output
type: feedback
---

Do not use `| tail` on build or test output.

**Why:** The user wants to see the full output, not just the last few lines. It also risks cutting off relevant errors or warnings.

**How to apply:** Run `dotnet build`, `dotnet test`, and similar commands without piping through `tail` or `head`. Show the complete output.
