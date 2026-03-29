---
name: Abstract redraw flag propagation
description: The NeedsRedraw flag propagation pattern in TuiSubCommand should be abstracted instead of listing all state objects
type: feedback
---

The redraw propagation pattern (checking `plannerState.NeedsRedraw || sessionState.NeedsRedraw || liveSessionState.NeedsRedraw`) should be abstracted so we don't have to keep adding variables manually when new state objects are added.

**Why:** Adding a new tab/state shouldn't require touching the main loop's redraw check.

**How to apply:** Consider a pattern like registering state objects that implement `INeedsRedraw` in a list, then iterating to check/clear in one pass. Apply when next touching TuiSubCommand or refactoring the main loop.
