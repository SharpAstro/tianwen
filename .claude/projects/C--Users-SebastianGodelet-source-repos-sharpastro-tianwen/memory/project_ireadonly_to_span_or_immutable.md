---
name: Replace IReadOnlyList with Span/ImmutableArray
description: Backlog task to replace IReadOnlyList<> parameters with ReadOnlySpan<> and return types with ImmutableArray<>
type: project
---

Replace most `IReadOnlyList<>` usages in parameters with `ReadOnlySpan<>`, and return types with `ImmutableArray<>`.

**Why:** Better performance semantics — spans avoid allocations for parameter passing, immutable arrays make ownership and thread-safety explicit for return values.

**How to apply:** When touching code that uses `IReadOnlyList<>`, consider whether the parameter should be `ReadOnlySpan<>` (for input) or the return type should be `ImmutableArray<>` (for output). This is a gradual migration, not a big-bang change.
