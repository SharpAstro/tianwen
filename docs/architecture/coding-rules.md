# Coding rules: style, clock, state, threads and code quality

What `CLAUDE.md` states in one line each, in full.

## Moved from CLAUDE.md, 2026-11-15

The text `CLAUDE.md` carried until 2026-11-15, where this document did not already hold it; `CLAUDE.md` keeps one line
per rule and points here.

### Coding Style (from CLAUDE.md)

Enforced via `src/.editorconfig` (it sits beside the solution, not at the repo root):

- 4 spaces, LF line endings. Namespaces are FILE-scoped (`namespace Foo;`) in three files of four
  (1,253 / 417 on 2026-09-11; `TianWen.Lib` 648 / 79, only `TianWen.UI.Abstractions` leans the other
  way at 62 / 106): write a new file file-scoped, match the file you are in when editing, and never
  churn a file from one form to the other. This line used to say the opposite.
- Primary constructors preferred for DI
- Target-typed `new(...)` where the type is apparent (a declared field, local, parameter, return or collection element type),
  `new SomeType()` where it is not (the owner relaxed "always `new SomeType()`" on 2026-10-09)
- Expression-bodied: properties yes; a method while it is small, one that fits on one line (`when_on_single_line` in
  `src/.editorconfig`) or a single switch expression, wrapped or not; a block body otherwise; constructors no
- Interfaces prefixed with `I`; PascalCase types/properties/methods; `_camelCase` private fields

### Logger, TimeProvider, and SleepAsync (from CLAUDE.md)

- `ILogger` and `ITimeProvider` resolved from `IServiceProvider` (not from `IExternal`)
- **`ITimeProvider.SleepAsync`** must be used instead of `Task.Delay(duration, timeProvider, ct)`.
  `FakeTimeProvider`'s `SleepAsync` auto-advances fake time; `Task.Delay` with `FakeTimeProvider`
  hangs waiting for external advancement. All code should be testable.
- **A deadline is `ITimeProvider.CancelAfter(delay)`** (also on a BCL `TimeProvider`), never `new CancellationTokenSource(delay)`
  nor `CancelAfter` on a plain source: those run on the wall clock whatever clock was injected, so a fake clock never ends
  them. `NoWallClockDeadlineTests` fails on either anywhere in `src/` outside the test projects; its only exception is the extension's own definition.
- `LoggerCatchExtensions` provides `ILogger.Catch/CatchAsync` for best-effort fallbacks

**`TIANWEN_NOW` startup clock anchor (dev/test):** set the `TIANWEN_NOW` env var to an ISO-8601
timestamp (ideally with an explicit offset, e.g. `2026-06-21T22:00:00+10:00`; no offset = machine-local)
to anchor the *entire* system clock to a simulated instant that then advances at real-time rate. This
lets you run a real night at the configured site while the machine clock says daytime, with **no fake-time
pump**. Single wiring point: the `ITimeProvider` registration in `AddExternal`
(`ExternalServiceCollectionExtensions.cs`) wraps `TimeProvider.System` in an `OffsetTimeProvider` when
`StartupTimeOverride.TryGet` returns an offset. Because planner, session loop, fake mount/camera, and
mount-reported UTC all resolve the clock from DI, they jump together. `StartupTimeOverride` (`Devices/`)
freezes the offset once at process start; the GUI logs a WARNING (`SIMULATED CLOCK ACTIVE`) when active.
Absent/unparseable → real system clock (previous behaviour). Pinned by `StartupTimeOverrideTests`.

### Signal Handler Pattern: Route, Don't Implement (from CLAUDE.md)

The lightweight `SignalBus` is our alternative to MediatR/MVVM. `AppSignalHandler.cs` subscribe
lambdas must **route only**: take signal payload, call one or two helpers, reflect results back into
UI state. No loops over domain state, no direct persistence, no URI manipulation, no multi-step
business logic.

Where business logic goes:

- **Pure profile/equipment transformations** → `EquipmentActions` in `TianWen.UI.Abstractions`
- **Device-model operations** (URI reconciliation, discovery) → extension methods in `TianWen.Lib/Devices/*Extensions.cs`
- **Persistence** → dedicated helpers (`PlannerPersistence`, `SessionPersistence`, `Profile.SaveAsync`)

**Red flag**: a `foreach` or multi-step `if`/`await`/`save` chain inside a subscribe lambda; extract it.

### Shared UI State: `ImmutableArray<T>`, not `List<T>` (from CLAUDE.md)

Any collection on shared UI state (`PlannerState`, `LiveSessionState`, `EquipmentTabState`,
`GuiAppState`) that can be touched by **both** the render thread and a background task must be
`ImmutableArray<T>` with atomic replacement. Writers build the new array (or use `array.Add(x)`,
`.RemoveAt(i)`, `.SetItem(i, x)`, `.Sort(cmp)`, all return new instances) and assign in one
reference update. Readers snapshot the property into a local. Pattern match on `.Length`, not
`.Count` (`ImmutableArray<T>` only exposes `Count` via explicit `IReadOnlyCollection<T>`).

`List<T>` here **will** produce `InvalidOperationException: Collection was modified` under load.
`Dictionary<K, V>` has the same hazard.

### Background-Task State in `AppSignalHandler` (from CLAUDE.md)

State that gates background tasks is mutated from two threads even when the source code looks
single-threaded: `bus.Subscribe<T>(async sig => ...)` runs the synchronous prefix on the UI thread,
but every continuation after `await` runs on a thread pool thread. Crashes show as
`IndexOutOfRangeException` inside `HashSet<T>.Add` / `Dictionary<K, V>` internals.

| Use case | Wrong | Right |
|---|---|---|
| Per-key in-flight set | `HashSet<TKey>` + `Add`/`Remove` | `ConcurrentDictionary<TKey, byte>` + `TryAdd`/`TryRemove` |
| Per-key value buffers | `Dictionary<TKey, T>` | `ConcurrentDictionary<TKey, T>` (T also thread-safe if mutated) |
| Single-flag in-flight gate | `bool _busy` | `int _busy` + `Interlocked.CompareExchange(ref _busy, 1, 0)` |
| Ring buffer / accumulator | unguarded `_ring`/`_count`/`_head` (or `lock` around them) | lock-free `CircularBuffer<T>` (ImmutableArray + CAS replace); readers take `Snapshot`, not lazy `IEnumerable` |
| Large `record struct` cross-thread | unguarded auto-property `set` | private field + `lock` (struct writes > pointer-size aren't atomic) |

**Telemetry-poll-only state** can stay non-concurrent if it is genuinely only written from the
per-frame poll method. Mark it clearly so a future edit doesn't move the write into a continuation.
Canonical example: `AppSignalHandler.PollCameraTelemetry` and `EquipmentTabState.PendingTransitions`.

### Concurrency (from CLAUDE.md)

- `SemaphoreSlim` / `DotNext.Threading` for resource locking
- `CancellationToken` propagated throughout
- `ValueTask` for allocation-free async paths
- **Never use `.GetAwaiter().GetResult()`**: make the method `async` and `await`
- **Prefer a lock-free hand-off over `lock {}` blocks.** For producer/consumer hand-off (a background
  task feeding a render or poll loop), return the result *through* the `Task<T>` and let the consumer
  poll it: `if (_task is { IsCompleted: true } t) { _task = null; if (t.IsCompletedSuccessfully && t.Result is { } x) use(x); }`.
  The Task is the synchronisation primitive, so no shared mutable field crosses threads; in a
  synchronous loop where you cannot `await`, that poll is the stand-in for `await _task`. For a single
  grab-and-clear reference, use `Interlocked.Exchange`. (Canonical example: `SkyMapTab`'s async Milky
  Way load, mirroring `TryApplyPendingStarBuild`.)
- **There is no `WhenAll` for `ValueTask`** -- not in the BCL, not in this org, and not in DotNext
  (it had a tuple `WhenAll` and dropped it after 4.x; 6.1.0 ships no combinators). Do not reach for
  `.AsTask()` reflexively: **start both, then await both**. Calling an async method runs it to its
  first await, so `var a = XAsync(); var b = YAsync(); await a; await b;` has both already in flight
  and allocates nothing. **Wrap it `try { await a; } finally { await b; }`** whenever abandoning the
  second one matters -- two bare awaits drop `b` if `a` faults, leaving an unobserved `ValueTask`
  and whatever `b` was cleaning up unfinished. Canonical use:
  `PulseGuideTargetExtensions.PulseGuideAsync`, where `b` is the pulse on the other mount axis and
  dropping it leaves that axis running.
- **Never build the value for a `CompareExchange` inside the call.** An argument is evaluated before
  the call it is passed to, so `Interlocked.CompareExchange(ref _task, Task.Run(Work), null)` starts
  `Work` on **every** racing caller, not just the CAS winner. The losers return the winner's task and
  look correct while their own copy runs on. In `FilterCurveDatabase.LoadAsync` that appended a second
  copy of every curve (180 filters became 360). Publish a `TaskCompletionSource` placeholder first, do
  the work behind it, and raise any "ready" flag only once the data is there -- a flag set by the CAS
  winner *before* the work runs answers true over empty state.
- **Standing rule for `lock () {}`** (any lock, anywhere): (1) it needs a strong justification as a
  comment at the lock site -- why a Task hand-off / `Interlocked` / ImmutableArray-CAS swap does not
  fit; (2) the locked path should not be reachable from a rendering thread (a contended lock there is a
  frame stall -- hand the render thread an immutable snapshot instead); (3) if the lock stays, it must
  be `System.Threading.Lock` (C# 13), never `lock` on an `object`, a collection or any other reachable
  instance (faster, self-documenting, compiler-enforced). None are left (swept 2026-09-24, #353); the
  one clause still unmet, `FileLoggerProvider` being reachable from a render thread, is #541. For a most-recent-N window polled by readers (guide
  samples, frame metrics), prefer the lock-free `CircularBuffer<T>` (`TianWen.Lib/Sequencing`):
  ImmutableArray + CAS replace, torn-free `Snapshot` reads, O(capacity) appends -- right when producers
  are low-rate (per exposure) and pollers high-rate (per frame).

### Code Quality Guidelines (from CLAUDE.md)

- **Reduced allocations**: prefer `MemoryMarshal`, `stackalloc`, `ArrayPool<T>`, `Span<T>` / `ReadOnlySpan<T>`
- **Never copy an array through a cast `Clone()`** (`(float[])x.Clone()`, the owner detests it; `NoCastArrayCloneTests`
  fails on one in `src/` or `tools/`). First ask whether the copy is needed: a reader takes the array or a
  `ReadOnlySpan<T>`, a routine that reorders its input (a median, a selection) reads a scratch from `ArrayPoolHelper`, and
  a solver whose caller is done with the system runs in place (`PlanetaryCeilings.SolveInPlace`). A copy that is needed is
  typed: `float[] copy = [.. source];`, or `Copy()` (`ArrayCopyExtensions`) for a 2-D array.
- **Immutability with controlled mutability**: types immutable by default; private mutable state with read-only views
- **Correct abstraction levels**: pure math/data in `TianWen.Lib`, UI state in `TianWen.UI.Abstractions`,
  Vulkan-specific rendering in `TianWen.UI.Shared` / `TianWen.UI.Gui`. Never put GPU calls in Lib or Abstractions.
- **No code duplication**: reuse single sources of truth (e.g., `Image.StretchValue()`)
- **No null-forgiving `!` in production code.** There are ZERO left across every shipping project
  (swept 2026-09-11); a new one is a regression, and the fix is almost always to STATE the invariant
  instead of asserting it: `[MemberNotNullWhen]` / `[NotNullWhen]` / `[MaybeNullWhen]` on the
  predicate or the `out` that decides it (`WCS.HasSip`, `PulseGuideRouter.UseCamera`,
  `DeviceBase.SameDevice`, `FrameCache.TryGet`), a pattern bind where a bool was carrying a value it
  could not (`is { } x`), or a `?? throw` naming what broke. Two compiler traps found doing it: a
  SECOND property read on a struct receiver DISCARDS the member-null state the first established (so
  `WCS.WriteToHeader` binds the SIP arrays instead), and `x!.M()` marks `x` non-null for the rest of
  the block, so removing one `!` can make a later line warn. Tests and benchmarks are deliberately
  exempt: `= null!` on a `[GlobalSetup]` field is the BenchmarkDotNet idiom, and `null!` passed to
  prove a guard throws is the point of that test.
- **No JSON type holds a nullable struct (`T?`), as a property or a sequence's element, whose last property can be null**
  (`JsonStreamReadTrapTests`, #1356; its list of exceptions is empty and meant to stay so). A stream read (`DeserializeAsync`,
  `ReadFromJsonAsync`) throws "could not be converted" when its buffer ends between that trailing `null` and the brace, while
  the same bytes parse whole: System.Text.Json's own bug, open as dotnet/runtime#110450 (upstream confirms the symptom; the
  trigger is measured here). **An element (`T?[]`, `ImmutableArray<T?>`) trips ten times as often as a property**; a root, a
  dictionary's value and one level down never did (#1414). One offset in about 350, and only past the first
  16 KiB, since the reader fills its buffer before parsing. **Make the TYPE safe, never the read**: end the struct on a
  value that cannot be null, a number or an enum where the data allows (`ColourCalibration.Source`, which was a string of
  two known values), or a last property saying what the nullable ones mean (`LimbFit.Ringed`, read off `RingLevels`; never
  a `[JsonIgnore]`d one, which writes nothing), or write it through a context that omits nulls or defaults
  (`HostingJsonContext`), which writes no null at all and which the guard counts as safe by itself, while nothing writes its
  types through other options (its chaining into ASP.NET's only binds requests; a response passes its own type info). Parsing from bytes
  (`PlanetaryCaptureStatistics.TryLoadAsync`) is a second protection, not a reason: nothing checks how a payload is read.
- **Directory walks go through `FileEnumeration` (`TianWen.Lib/IO`), never the `SearchOption`
  overloads of `Directory.EnumerateFiles`/`GetFiles`.** Those run with the legacy defaults: they ENTER
  every reparse point (the organized archive's `targets/` junction farm was scanned once per link, and a
  scratch junction into `D:\Astro-Pics` turned a scratch walk into an archive walk), abort the whole walk
  on the first unreadable directory, and match `*.fits` case-sensitively on Linux. `FileEnumeration` sits on
  `FileSystemEnumerable<T>` (the directory index, no per-file open: a million tiles in about a second),
  skips reparse points, ignores inaccessible directories, keeps hidden files, uses a 64 KiB buffer and
  matches extensions as an ordinal-ignore-case name SUFFIX (`.fits.gz` is one extension). Results are
  unordered; sort with `StringComparer.OrdinalIgnoreCase` where determinism matters. Pinned by
  `FileEnumerationTests`, including a real junction that must be neither listed nor entered.

### Package Management (from CLAUDE.md)

Centralized in `Directory.Packages.props`: version numbers go there, not in individual `.csproj` files.
