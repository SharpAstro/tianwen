# Frame Lifecycle (who owns an `Image`, and who is allowed to release it)

**Status: P0, P1, P2 and P4 DONE (2026-08-23), including the measurement P1 was waiting on. P3 is
PARTIAL: gap 4 closed and the first bulk reader pooled, with an audit showing that the rest needs a
`FrameCache` ownership model and that the "delete the `pooled:` parameter" half should be withdrawn.
Two things a reader should take from the phase write-ups rather than from this line: the P1
measurement REFUTED the shape the plan expected to adopt, and P3's blocker turned out not to be the
one it predicted.** (Raised by the user 2026-08-06, while adopting the pooled FITS
read into the tile-pipelined stacking strategies; re-surveyed 2026-08-23 with thread safety and
performance added, since P1 is chosen partly on both and neither was written down.) This plan does not invent a new mechanism. The mechanism
already exists and works: `ChannelBuffer` refcounts, `Image.Release`, `Image.TryLease`,
`Array2DPool`. What is missing is a *stated* rule for which of them applies to a given `Image`, so
today the answer is reconstructed per call site from the call site's own knowledge.

The cost is already measurable, and it is growing. `Image.TryReadFitsFile` has **105** call sites
outside its own file and `.Release()` has **217** across the tree, and the correct pairing between
them is not derivable from any signature. Those were 73 and 203 when this plan was written on
2026-08-06: **+32 read sites and +14 releases in seven weeks**, every one of them pairing the two by
hand. The cost of not having a policy is not static, which is the argument for P0 landing before the
next batch rather than after it.

## The problem: five conventions coexisted, and none of them was named

**Read this section as the diagnosis, in the tense it was written.** Convention 5 is gone (P1) and
the other four are named on `Image` (P0); it is left standing because the reasoning is what stops it
coming back.

Every one of these is individually correct and deliberately so. The defect is that an `Image`
carries no indication of which it is, so a consumer cannot be written against the type, only
against the producer it happened to be reading that day.

| # | Convention | Producer | Consumer must | `Release()` does |
|---|---|---|---|---|
| 1 | **Driver-owned, recycled** | camera drivers (DAL, Fake, Alpaca, ASCOM) | release, and never touch it afterwards; never hold it across an `await` without `TryLease` | hand the array back to the camera |
| 2 | **Self-owned** | file loads (default), debayer output, synthetic frames, tests | nothing | nothing (no-op) |
| 3 | **Pool-owned** | `TryReadFitsFile(..., pooled: true)` | release, and never touch it afterwards | return the array to `Array2DPool` |
| 4 | **Consumed input** | `ScaleFloatValuesToUnitInPlace`, `DebayerAsync(normalizeToUnit: true)`, `AdoptImageAsync` | not use the input again; the result shares its arrays and carries `Buffer = null`, so release stays with the original | (on the result) nothing |
| 5 | **Identity or copy, decided at runtime** | `Calibrator.Apply`, every `SharpenPipeline` step, `Image.MaskedBoost` | compare references before releasing | depends on which of the two it got |

Conventions 1 to 4 are real distinctions between real situations, and a policy should keep all
four. **Convention 5 is not a convention at all**, it is the absence of one, and it is the reason
this plan exists.

### Convention 5 in detail, because it is the load-bearing one

`Calibrator.Apply` opens with `var result = light` and each of bias, dark and flat is optional, so
with no masters configured it returns *the very instance it was handed*. Ownership of the return
value is therefore a function of runtime configuration, and the caller cannot know statically
whether it received a new image or its own input back. The only safe form is:

```csharp
var calibrated = calibrator.Apply(raw);
if (!ReferenceEquals(calibrated, raw)) { raw.Release(); }
```

That idiom appears at **13 sites** (re-counted 2026-08-23; the line numbers in the first version of
this plan have all moved, which is itself the point -- a policy written out longhand at 13 sites is
13 things to keep in step):

| where | sites |
|---|---|
| `SharpenPipeline` | 191, 256, 268, 292, 729, 766 |
| `Image.Masks.cs` | 232, 240 |
| `OnnxBackgroundExtractor` | 124, 159 |
| `MasterPreviewRenderer` | 360 |
| `RawLightDecoder` | 46 |
| `Session.Flats.cs` | 305 |

**The repeated guard is the missing policy, written out longhand at every site that needs it.** It is
also silent when wrong: releasing a frame you did not own recycles pixels another holder is still
reading, which surfaces as a corrupted stack rather than an exception.

Two things the re-count turned up that the original list did not have. **`Session.Flats.cs:305` is a
second SHAPE of convention 5**, and worth naming separately: it is not "did the transform copy?" but
"did the producer hand me the same frame twice?" -- a slot swap releasing the previous frame unless
it is the identical instance. The guard is the same three lines and the hazard is the same, but no
amount of fixing `Calibrator.Apply` touches it, so P1 must not be scoped as "make transforms honest"
alone. And three further `ReferenceEquals(Image, Image)` matches in the tree are **not** release
guards at all (`LiveSessionTab` 306 and 323, `TuiLiveSessionTab` 741 -- display-identity checks
asking "is this a new frame to upload?"). Anyone auditing by grep will find 16 and must not
mechanically convert the three, which is exactly the kind of near-miss a stated policy prevents.

### What this cost in the work that raised it

Two concrete outcomes from the pooled-read change, both consequences of the above:

- **The pooled read had to ship opt-in** (`pooled: false` default), purely because ownership across
  the 73 call sites of the day (105 now) could not be established. Convention 2 is load-bearing for several of
  them: they release an image and keep reading it, which is well defined only while file loads own
  their arrays outright, and `FitsPooledReadTests` now pins that asymmetry so it cannot be
  "tidied" away. A stated policy makes the flag unnecessary.
- **Neither tile-pipelined strategy released its raw frame at all** before this change, and both
  were correct anyway, because convention 2 made the omission free. The same code became a real
  leak the moment the read was pooled. Nothing in the type system noticed.

### What already points the right way

`Image.TryLease` (shipped 2026-08-05) is the first piece of this named properly. It is the **borrow**
primitive: a reader that does not own the frame takes a ref on every plane, all or nothing, and
gets back a distinct `Image` with its own one-shot release. It also demonstrates the standard to
hold the rest to, because it had to state explicitly that a null buffer array and a released image
are different things. Convention 2 and a spent convention 1 are indistinguishable without that
flag, which is precisely the ambiguity this plan is about.

## Thread safety: the refcount layer is sound, and the gaps are all somewhere else

Re-read 2026-08-23, because "is this thread-safe?" is a question a policy has to answer and this plan
did not address it at all. The refcount layer is the part that has been thought about, and it holds up:

- **`ChannelBuffer.TryAddRef` is a CAS loop, and its doc comment states exactly why** -- the obvious
  `if (!_released) Interlocked.Increment(...)` lets a borrower pass the liveness check on one thread
  while the last holder takes the count to zero on another, recycling the array before the increment
  lands. A zero refcount is terminal, so comparing against the observed count closes that window and
  the loser of the race learns it lost.
- **`Image.Release` sets `_released` BEFORE `Interlocked.Exchange(ref _channelBuffers, null)`**, so
  exactly one caller ever gets the array to release, and a racing `TryLease` cannot read a null buffer
  array and then a stale "not released yet".
- **`TryLease` is all-or-nothing with an unwind**, so a partially-referenced multi-channel frame is
  never handed out.
- **Plane residency (D1') publishes with `ImmutableInterlocked.InterlockedCompareExchange`** on one
  field, readers work off a snapshot, and a restorer that loses the race discards its own build.
  Pinned by `ImagePlaneResidencyConcurrencyTests`.

**There WAS one gap in the refcount, found 2026-08-23 and now closed as far as a shared count can
close it.** `Release` was documented idempotent and clamped a negative count back to zero, which
guards the benign case -- a lone holder releasing twice, where the array was going back anyway -- and
is silent on the dangerous one. Probed: two holders at count 2, one of them releasing twice takes the
count to zero and fires the recycle callback with the other still reading. The hazard sat behind all
239 `Release()` call sites without ever firing, because the only production holder is an `Image`,
whose `Release` is one-shot.

The fix is a throw, and its limit is worth stating because it is the same limit the typed-ownership
section below runs into. **A shared count can DETECT a double-release; it cannot prevent one.** The
offending call is byte-identical to a legitimate last release -- same method, same resulting count,
no holder identity anywhere -- so it is served, and the throw lands on the *next* release, the
innocent one. That is strictly better than silent absorption and it is not prevention. Prevention
needs per-holder identity, which is what `Image` already supplies and what a `ChannelBuffer` handed
out raw does not. Both halves pinned by `ImageLeaseTests`.

**And a second find the same day, in a PRODUCER rather than the count: the published-pointer
dangle.** `GuideLoop.RunAsync` and `FakeGuider`'s capture loops all did release-then-AWAIT-then-publish,
so for the duration of every capture the published frame pointer (`LastFrame` / `_lastLoopFrame`)
aimed at a frame whose ownership was already spent, and any borrower in that window lost its
`TryLease` to pure scheduling. Invisible while `SaveImageAsync` read the bare reference (it "won" by
writing a spent frame's pixels); the moment that reader became honest, the guider-focus functional
test started failing under suite load -- 4 of 6 runs -- while passing in isolation and in CI. The
invariant, now stated at every publisher: **a published frame pointer always points at a live frame
-- capture, swap, THEN release the superseded frame** (and unpublish before releasing on the teardown
path). With swap-first publishers a failed lease has exactly one meaning, "superseded between read
and lease", so the reader re-reads and converges in one step.

Otherwise the refcount holds. There are four gaps elsewhere, and they are what a policy has to cover:

1. **Convention 4 has no runtime enforcement whatsoever, and it is the only one that MUTATES.**
   `ScaleFloatValuesToUnitInPlace`, `DebayerAsync(normalizeToUnit: true)` and `AdoptImageAsync` write
   *into* the plane arrays. No refcount, CAS or snapshot helps: the hazard is a write through the
   array, not a swap of the array, so residency safety is simply not on this path. It is a
   single-owner contract and nothing checks it. **A policy that types ownership and stops there will
   have typed the safe half** -- conventions 1 and 3 already fail loudly (a released `ChannelBuffer`
   throws `ObjectDisposedException`), while convention 4 fails silently and in the pixels.
2. **`Image` had two meanings of "released" and they were one word apart. CLOSED by P0.**
   `Image._released` means ownership is spent; the residency predicate meant the float planes were
   dropped and would be rebuilt on the next read. Unrelated facts with opposite implications for a
   caller, and a guard written against the wrong one is silent, which is why the rename was P0 work
   rather than P4 cosmetics. Residency now says **evict / restore / resident**
   (`TryEvictFloatPlanes`, `IsEvicted`, `PlanesResident`, the existing `RestorePlanesFromRaster`,
   `ImagePlaneEvictionTests`) and "released" means ownership and nothing else. Evict/restore was
   chosen over the alternatives because the inverse operation was ALREADY called *Restore*, and
   because the pool next door already counts `BudgetEvictionCount`.
3. **Convention 5's guard asks the wrong question, and concurrency is where that begins to matter.**
   `ReferenceEquals(result, input)` answers "is this a different instance?", which is not "do I own
   this?" -- ownership is a property of the handoff, not of reference identity. The two coincide only
   while one thread owns the whole chain, which is an *undocumented precondition* of all 13 sites. It
   is also a precondition the codebase already leans on elsewhere: `EnhanceOptions` was made immutable
   specifically so parallel enhances cannot tear, and `SharpenPipeline` -- six of the 13 sites -- is
   the thing that would be parallelised.
4. **`Array2DPool<T>.Enabled` is a mutable, non-volatile, process-wide static** (a plain
   `{ get; set; }`), flipped by `FakeExternal` during tests while every one of the pool's counters is a
   `Volatile.Read`. Harmless while pooling is opt-in and test-driven; **P3 makes the pool load-bearing
   in production**, and a process-wide switch with no barrier is the wrong shape to promote.

## Performance: the budget, and what P1 actually trades

**The budget is set by a measurement rather than a guess, and the measurement was re-attributed
before it could be trusted.** `WarpBenchmarks` prices the residency check on the bilinear resample
loops -- the only place in the library that touches a plane per DESTINATION PIXEL, 12.6M samples for
a 2048-square colour pass. Its first reading billed the whole 8-20% band to D1'. Splitting the
ablation into four variants instead of three shows that is wrong:

| | pre-D1' | D1' shipped | thread-safe | hoisted |
|---|---|---|---|---|
| Mono 2048 (AOT) | 41.66 | 41.55 | 46.51 | 41.16 |
| Color 2048 (AOT) | 121.26 | 121.70 | 146.37 | 129.05 |

**D1' as shipped cost nothing** -- it put one predicted-not-taken bool check ahead of the same field
read pre-D1' already did, and seven of eight cases land within 1.3%. **The band belongs to the
thread-safety fix**, the one this plan's Thread-safety section praises for DERIVING residency from
the plane array instead of keeping a flag beside it: that derivation is a SECOND 72-byte `Channel`
copy plus a dependent `.Data` load and a length check, 12.6M times, and it costs +8.7% to +20.3%.
Both facts stand -- a torn read of a half-restored array is not a cost worth saving -- and the
resolution is `Image.ResidentPlanes()`, which hoists the resolution out of the loop and returns to
parity under AOT, which is what ships.

**So the standard here is: ownership work is per-FRAME work and must never appear per-pixel or
per-sample.** The existing invariant ("never make a hot path pay for the policy") now has a number
behind it, and a worked example of the correct cure -- hoist the resolution to a scope, do not try to
make the per-sample check cheaper. It also carries a methodological warning worth more than the
number: **a before/after pair spanning two commits is a band, not an attribution.** Three of these
four columns were needed before the cost landed on the right change.

**P1's "always copy" is a trade, not an addition, and `RawLightDecoder` is the proof.** That method
currently has to PREDICT convention 5's runtime answer in order to choose whether to pool at all:

```csharp
var willCopy = calibrator.Bias is not null || calibrator.Dark is not null || calibrator.Flat is not null;
if (!Image.TryReadFitsFile(source.Path, out var raw, out _, pooled: willCopy))
```

The pooling decision is *coupled to whether `Apply` will copy*, because pooling a frame that comes
straight back out would hand a recycling buffer to a caller that never releases it. Make `Apply`
always copy and `willCopy` is constantly true: the read is unconditionally pooled, the release is
unconditional, and both the prediction and the guard at line 46 disappear. So the copy is paid for by
the large-object churn the pooled read then stops producing on the no-masters path -- one memcpy per
frame against one LOH allocation set per frame that is currently never recycled.

**Which way that trade goes is unmeasured, and the instrument already exists.** `FitsPooledReadBenchmark`
compares pooled against unpooled over a file set; extending it with an always-copy calibrator is the
measurement that decides P1's shape. Until then, note that the P1 row's "the uncommon case" is an
**assumption**: a stack run with no calibration folder takes the no-masters path for *every* light, so
it is the case a first-time user hits, not a rare one.

**P1's "type" costs nothing per frame, but it cannot be a `ref struct`** -- worth stating because the
residency work reached the opposite conclusion about a very similar-looking invariant. A residency
guarantee must NOT outlive the operation that established it, which is exactly why a `readonly ref
struct` is right there: it cannot be stored in a field. An ownership obligation is the reverse -- a
frame is deliberately held across an `await` and stored in a field, so the obligation MUST be storable.
**Same-shaped invariant, opposite tool.** P1's type is therefore a class or a wrapper: one small
allocation per call plus forwarding on the read surface, acceptable only while no per-pixel path goes
through the wrapper. Check that before committing to it, because the `SharpenPipeline` plates and the
stacker frames are the same objects the resample loops run on.

**P2's finalizer is the wrong instrument.** A finalizer on `ChannelBuffer` puts *every* buffer on the
finalizer queue in DEBUG, which changes GC timing in precisely the tests that assert pooling behaviour
(`FitsPooledReadTests.Pool_StopsRetainingOnceTheByteBudgetIsReached` reads `RetainedBytes`). Prefer
explicit tracking -- a static live-buffer table plus an end-of-test assertion -- or suppress
finalization on the normal release path so only genuine leaks ever reach the queue.

## Readiness

| Phase | Ready? | Waiting on |
|-------|--------|------------|
| P0 | **DONE 2026-08-23.** | -- |
| P1 | **DONE 2026-08-23.** The measurement refuted always-copy and the answer turned out to be neither candidate. | -- |
| P2 | **DONE 2026-08-23**, reshaped as planned: explicit tracking, no finalizer. | -- |
| P3 | **PARTIAL 2026-08-23.** Gap 4 closed, master building pooled, audit done. The rest needs a `FrameCache` ownership model. | A `FrameCache` redesign (its weak tier cannot hold pooled frames), plus releases at the `LoadFullAsync` consumers. |
| P4 | **DONE 2026-08-23.** | -- |

**So: yes to starting, no to starting at P1.** P0 and P2 are independent and low-risk, and between them
they name the rules and add the instrument that says when one is broken -- which is the right position
from which to take the P1 measurement. Both have now landed, so the measurement is the next thing.

## Phasing

| Phase | What | Status |
|-------|------|--------|
| P0 | **Name the five conventions** in one place: an XML doc block on `Image` that the four producers link to, plus the vocabulary (own / borrow / consume) used consistently in member names. Documentation only, no behaviour change, and it is what makes the rest reviewable. | **DONE 2026-08-23** -- see "What P0 shipped" below |
| P1 | **Retire convention 5.** Make ownership of a return value static per method rather than per configuration. | **DONE 2026-08-23** -- neither candidate shape; see "What P1 shipped" below |
| P2 | **Debug-only leak detection.** Explicit live-buffer tracking in DEBUG builds (NOT a finalizer -- see Performance above) that reports a buffer collected while still referenced, attributed to the producer. The pooled survey work found its leaks by watching memory; a counter finds them at the call site. Pairs with the existing `Array2DPool` accounting (`RetainedBytes`, `BudgetEvictionCount`). | **DONE 2026-08-23** -- see "What P2 shipped" below |
| P3 | **Make pooling the default** for bulk readers once P1 lands, and delete the `pooled:` parameter. | **PARTIAL 2026-08-23** -- first bulk reader pooled + gap 4 closed; the deletion half is withdrawn with reasons. See "What P3 shipped" |
| P4 | **Sweep the naming.** `AdoptImageAsync` already follows the convention (verb-form ownership transfer); apply it to the rest of convention 4, and audit the 235 `Release()` sites against the stated policy. | **DONE 2026-08-23** -- see "What P4 shipped" below |

## What P0 shipped (2026-08-23)

Documentation and one rename; **no behaviour change**, and the solution builds with zero warnings.

- **The policy lives in the `<remarks>` on `Image`** (`src/TianWen.Lib/Imaging/Image.cs`) and nowhere
  else: the own / borrow / consume vocabulary, all five conventions with their producers and consumer
  obligations, the "ownership is a property of the HANDOFF" rule, the `Adopt*` / `*Into*` naming
  clause, and the released-vs-evicted separation. CLAUDE.md keeps a pointer plus the rules that bite,
  not a second copy.
- **Every producer names its convention and points back**, so the answer is reachable from the call
  site rather than only from the type: `ICameraDriver.GetImageAsync` (1),
  `Image.TryReadFitsFile` (2, and 3 on the `pooled` overload), `Image.ScaleFloatValuesToUnitInPlace` /
  `Image.DebayerAsync` / `AstroImageDocument.AdoptImageAsync` (4), and `Calibrator.Apply` /
  `SharpenPipeline` / `Image.MaskedBoost` (5, each saying outright that it is the shape P1 retires and
  that no new producer should be written in it). `Image.Release` is documented as the ownership verb
  and `Image.TryLease` as the borrow primitive.
- **Gap 2 is closed by renaming the residency half**, not the ownership half: `TryEvictFloatPlanes`,
  `IsEvicted`, and evict/restore/resident wording throughout, with `ImagePlaneReleaseTests` renamed to
  `ImagePlaneEvictionTests`. Ownership keeps `Release`, so the 217 existing sites are untouched. The
  public surface moved by exactly one member, which is the whole reason this was cheap to do now and
  will not be after P3 widens the pooled path.

Two things the pass turned up that were not on the list, both fixed:

- **`Calibrator.Apply`'s summary said "Returns a calibrated copy"**, which is false on precisely the
  path convention 5 is about -- the no-masters path returns its own input. The single most load-bearing
  producer in this plan was documented as if the problem did not exist, which is a fair measure of how
  much the policy was being carried by folklore.
- **`TryGetSourceRaster`'s doc block had come adrift** and was sitting above the `_planes` field, so
  the field carried two `<summary>` tags and the method carried none. Moved onto the member it
  describes.

## What P2 shipped (2026-08-23)

`ChannelBufferLeakTracker` (`src/TianWen.Lib/Imaging/`): a table of every `ChannelBuffer` created and
not yet released, keyed so a survivor names the code that produced it.

- **Two answers, because a buffer fails to be released in two ways.** `LiveCount` is what is
  outstanding right now -- normal mid-session, a leak when read where nothing should still be held --
  and it is deterministic, needing no GC. `LeakCount` is buffers the collector took while they were
  still outstanding, which is never anything but a bug; it needs a collection, hence `collectFirst`.
- **No finalizer, and no strong reference either.** The finalizer objection was in this plan already.
  The strong-reference one is the same argument one step further: a table that holds its subjects
  alive has changed what it measures, turning a buffer the collector would have reclaimed into one it
  cannot. Entries hold a `WeakReference` to the **array** rather than to the buffer -- the array is
  what the pool and the camera are waiting for, and it is available in a field initialiser where
  `this` is not, which is what kept `ChannelBuffer` on its primary constructor.
- **Attribution is `[CallerMemberName]` + `[CallerLineNumber]` at the `ChannelBuffer` constructor**,
  giving `WrapPooledPlanes:437` and the like across the five producing sites. `[CallerFilePath]` is
  deliberately excluded: it would bake the build machine's absolute source paths into a shipped
  package to buy a detail the member and line already give.
- **Release cost is nothing.** Every method has an empty body outside DEBUG, so the calls inline away
  and only the literals remain at the call site -- per frame-channel, on a type wrapping a
  multi-megabyte array. Verified by building `TianWen.Lib` in both configurations.
- **Wired where the plan says it would have paid: `StackingPipeline.RunAsync`** logs a warning at
  `[end]` when anything is still outstanding, with `Array2DPool.RetainedBytes` and
  `BudgetEvictionCount` on the same line -- the pairing this phase asked for, and it earns its place:
  a shortfall against a pool sitting at its ceiling is a different diagnosis from the same shortfall
  against an empty one. The sweep there does NOT force a collection, because a dropped frame reads as
  outstanding whether or not the collector has got to it, so the cheap answer is the same answer.

**The suite is `ChannelBufferLeakTrackerTests`, and it was seen to FAIL first.** Sabotaging
registration failed all five; sabotaging unregistration failed the three that assert release clears
the table. That check is not optional here, because of the next point.

**CI's main leg builds Release (`BUILD_CONF: Release`), where tracking is compiled out**, so every
test opens with `Assert.SkipUnless(ChannelBufferLeakTracker.IsActive, ...)` and reports *skipped*
there rather than asserting zero against zero and passing green with the feature deleted. That is
honest but it is not cover, so **`test-unit` gained a DEBUG leg**: it builds `TianWen.Lib.Tests` in
Debug after the Release run and executes `--filter "Category=DebugOnly"`, reusing the same job's
checkout, LFS pull and catalogs. Both matrix architectures run it; the tests are arch-independent but
the Debug compile is its own cover, and the job sits at ~6m against a 25m budget.

**The leg asserts that it ran something, and that guard is not decoration.** `dotnet test` with a
filter matching NOTHING prints "No test matches the given testcase filter" and **exits 0** (verified),
so a renamed trait would turn the whole leg into a green no-op -- the same vacuous pass the
`SkipUnless` prevents one level down. The step reads the executed count back out of the TRX and fails
at zero. Both branches were exercised before committing: the real trait runs 5, a deliberately
mistyped one exits 0 from `dotnet test` and is caught by the guard.

Selection is by **trait, never by class name**, so the next DEBUG-gated suite joins by carrying
`[Trait("Category", "DebugOnly")]` and the workflow needs no edit. The alternative considered and
rejected was promoting tracking out of `#if DEBUG` behind a published switch, which would first need
gap 4's objection answered -- a process-wide mutable static is the shape being criticised there.

## The P1 measurement (2026-08-23): always-copy is REFUTED

`CalibrationOwnershipShapeBenchmark.NoMastersCalibrationShapes`, env-gated like its neighbours, over
four real frames x 25 on win-arm64, Release. The two shapes differ by exactly one term: today reads
unpooled and `Apply` returns its own input; always-copy reads pooled, copies, and hands the rented
arrays back.

```
                        reads    wall      allocated    gen0/1/2
today (identity)          100    3.17 s     5645 MiB    51/50/50
always-copy + pooled      100    3.71 s     5756 MiB    47/46/46
today (identity)          100    4.24 s     5645 MiB    20/19/19
pool: 97 hits, 3 misses, 100 returns
copy in isolation: 3840x2160x1 (32 MiB), 7.2 ms each (4.3 GiB/s), 32 MiB allocated per copy
```

**The allocation delta is +2.0 %, which is to say a wash, and that is the finding.** The plan argued
the copy would be "paid for by the large-object churn the pooled read then stops producing". It is
not paid for at all. The pool does its job perfectly -- 97 hits in 100 reads -- but what it recycles
is the READ's destination, and on the no-masters path that array is today *already* the frame the
caller keeps. Always-copy recycles it and then allocates a second one to copy into, so the count of
never-recycled large arrays per light is one either way. **The copy is a pure addition of 7.2 ms and
32 MiB per light**, not a trade.

**Do not read the wall-clock column as the result.** Today's two passes came in at 3.17 s and 4.24 s,
a 25 % run-to-run spread that swallows the 17 % the candidate appears to cost -- three passes over
the same files ride whatever the OS file cache is doing. That is why the copy is priced separately,
off disk, where 7.2 ms per frame is a clean number. In absolute terms it is small; the point is that
it buys nothing.

**So P1's real choice was never "always-copy against a wrapper".** The premise that made always-copy
attractive does not hold, and the wrapper touches every signature in the chain. See the phase entry
for what shipped instead.

## What P1 shipped (2026-08-23)

**Neither candidate.** Always-copy was refuted by measurement (above) and the wrapper touches every
signature in the chain to encode something the code already knew. The fourteen guards came out by a
third route, and the reason it works is worth stating once:

> **In every case the answer was already in hand one branch earlier.** `ReferenceEquals(result, input)`
> was a runtime re-derivation of a condition the same method had just tested. Ask THAT condition.

| site | the guard was really | now reads |
|---|---|---|
| `SharpenPipeline` x4 (deblur, sharpen-stars, deconv, denoise) | "did the blend allocate?" | `if (step.Blend < 1f)` -- `Lerp` always allocates, so the branch IS the answer |
| `SharpenPipeline` x2 (GHS pass loops) | "is the accumulator still the caller's?" | a `currentIsOurs` flag set by the assignment that made it ours |
| `Image.MaskedBoost` x2 | "did an optional stage replace `processed`?" | a `processedIsOurs` flag, same shape |
| `MasterPreviewRenderer` | "did `MaskedBoost` no-op?" | `if (!boost.IsNoOp)` -- the options, not the reference |
| `OnnxBackgroundExtractor` x2 | "did the mono/RGB conversion allocate?" | `if (channels == 1)` |
| `PlanetaryCaptureController` | "did the CFA split allocate?" | `if (stream.Layout == SplitCfa)` |
| `RawLightDecoder` | "did `Calibrator.Apply` copy?" | deleted -- Apply now consumes |

**`Calibrator.Apply` is the one that needed a contract change rather than a predicate**, because it
has no condition a caller can see. **It now CONSUMES its light and the caller owns the result**
(convention 4), so each step releases what it just consumed without asking whether it may: the answer
is always yes. That release is a no-op for an unbuffered intermediate and the real handback for a
pooled or camera-owned input, which is exactly the behaviour wanted in both cases. No copy, no
allocation change. Pinned by `CalibratorOwnershipTests`, which asserts through a `ChannelBuffer`
release counter rather than the DEBUG-only tracker so it runs in CI's Release leg too.

**`Session.Flats.PublishFlatPreview` keeps its reference check, and that is the point of having
named it separately.** It is not the retired idiom: it asks whether the PRODUCER handed over the
frame already in the slot, not whether a transform copied. The answer is no by construction, but the
cost of being wrong is asymmetric -- releasing `previous` when it *is* `image` recycles a frame the
GUI is drawing -- so one comparison stays as insurance, with a comment saying which question it asks.

**Two identity checks in `SharpenPipeline` also survive and are not ownership questions**: one logs
that the non-finite sanitiser did something, the other detects an enhancer DECLINING the plate (an
unlicensed deblurrer hands its input straight back), which is control flow. Anyone auditing by grep
finds them plus the display-identity checks the plan already listed; none of the seven should be
mechanically converted.

**What P1 did NOT unlock, contrary to the plan's expectation.** `RawLightDecoder` still predicts
whether calibration will consume the raw, because pooling is safe only where somebody releases -- and
on the no-masters path the raw frame IS the returned frame, which the tile strategies cache for the
whole run and never release. Making the read unconditionally pooled is therefore P3's job and needs
those strategies to release first. The prediction is no longer an ownership guard, though: it is a
pooling decision, which is a different and legitimate question.

## What P3 shipped, and the audit that changed its second half (2026-08-23)

**Gap 4 is closed.** `Array2DPool<T>.Enabled` is a `volatile` field behind the property instead of a
plain auto-property, matching the `Volatile.Read` every counter beside it already used. A
publish-and-observe flag needs ordering, not `Interlocked`.

**The first bulk reader is pooled: master building.** `MasterFrameBuilder.CombinePooledAsync` loads
every bias / dark / flat frame with `pooled: true`, combines, and releases them in a `finally`. It is
the right one to start with because its ownership was never in doubt: `IFrameSource` has always
documented that a consumer "releases the returned Image as soon as they're done with it", and
`BuildFlatMaster` normalises its inputs IN PLACE precisely because they are throwaway. Tens of
same-shape frames in one pass means every rent after the first is a hit. Pinned by
`FitsPooledReadTests.MasterBuild_RentsEveryFrameAndHandsThemAllBack`, which asserts the pool's RETURN
and HIT accounting rather than only the master's pixels -- a build that silently stopped pooling
would still produce a correct master -- and which was seen to fail with the flag reverted.

### The audit: 110 sites, and what actually blocks the rest

| group | sites | verdict |
|---|---|---|
| Master building | 3 (`MasterFrameBuilder`) | **pooled** |
| `LoadFullAsync` -> `Apply` -> use | 6 (`SessionFrameAnalyzer`, `SessionRegistrar` x2, `StackingPipeline` x3) | blocked: nothing releases the calibrated frame |
| Warped-scratch re-readers | 3 (`SessionRegistrar`, `DatasetTileExporter` x2) | blocked: the frame is yielded or stretched onward and no consumer releases |
| `RawLightDecoder` (tile strategies) | 1 | blocked twice over, see below |
| One-shot readers | ~18 (CLI, MCP, hosting, viewer, plate solve, polar align, `MasterCache`) | pooling buys nothing: one read per invocation, and several keep the frame for the life of a document |
| Tests | 39 | own concern |

**The blocker is not what the plan assumed.** It expected convention 2's "release an image and keep
reading it" sites to be in the way. A scan of the whole tree for a release followed within eight
lines by a read of the same variable finds **no production instance** -- every apparent hit is a
variable reassigned in between. The only real one is
`FitsPooledReadTests.UnpooledRead_CarriesNoBuffer_SoReleaseStaysANoOp`, which exists to pin exactly
that. (The scan is a heuristic: it cannot see a release in one method and a read through a field in
another.) The actual blocker is the opposite and duller: **nothing releases at all**, so pooling
those sites would buy nothing -- the array would never come back and every rent would be a miss.

**`RawLightDecoder` is blocked twice**, and the second one is structural. Its calibrated frame goes
into `FrameCache` for the whole run and is never released; and `FrameCache` has a **weak tier**, so a
frame past the strong cap is held only by a `WeakReference` and a later `TryGet` can resolve it. A
pooled frame that was released and re-lent would still resolve there, and the strategy would read
somebody else's pixels. Pooling that path needs the cache to own its frames -- release on eviction
AND clear the weak entry so a miss re-decodes -- which is a redesign of `FrameCache`, not a flag.

### The `pooled:` parameter stays, and the plan's reasoning for deleting it does not survive

> "The flag exists only because ownership is currently unknowable; it should not outlive that."

Ownership IS knowable now -- P0 wrote it down. But that was never what the flag encoded. It encodes
**whether this caller will release**, which is a per-caller fact and stays one however well the
policy is documented. Deleting it makes `Release()` destructive for every reader in the tree,
including the one-shot readers that hold a frame for the life of a document and the tests that
deliberately read after releasing. The failure mode is silent pixel corruption, the justification
would be a heuristic scan, and **`ChannelBufferLeakTracker` cannot catch it** -- P2 detects frames
that were never released, not frames released too early. That is not a trade to make on a scan.

So `UnpooledRead_CarriesNoBuffer_SoReleaseStaysANoOp` **stays**, and the readiness note that "that
test changing is the signal P3 is really happening" is withdrawn: the asymmetry it pins is a
deliberate feature of convention 2, which the Invariants below have protected all along. What the
flag's documentation now says is not "we cannot tell" but "this caller owns the frame for a bounded
scope" -- a legitimate, permanent distinction, and the one `FrameInfo.LoadFullAsync(bool pooled, ...)`
spells out at the point of use.

**Remaining P3 work, in dependency order:** (1) make the `LoadFullAsync` consumers release their
calibrated frames -- safe to do first, since release is a no-op until the read is pooled; (2) give
`FrameCache` an ownership model and drop or fix the weak tier; (3) pool `RawLightDecoder`
unconditionally, at which point its `willConsumeRaw` prediction goes too.

## What P4 shipped (2026-08-23)

### The audit: 235 `Release()` sites, and the one shape worth looking for

A per-site table of 235 rows would be unreadable and out of date within a week, so the audit asks the
question the policy actually cares about: **does anything release a frame it did not own?** The
dangerous shape is a callee releasing its CALLER's frame, so every production `Release()` was
classified by what it releases -- a local the method produced, a field the type owns, or a parameter.

**No production site releases a parameter.** Eleven release a field the declaring type owns, the rest
release locals they produced themselves, and the handful the classifier could not place are
`SharpenPipeline` plate locals declared far above their release (plus one `SemaphoreSlim.Release()`
that is not this kind of release at all). The one place a frame arrives as a parameter and is
released is `Calibrator.Apply`, which P1 made a documented consuming transform. So the tree conforms
to the policy, and the audit's value is the shape it establishes for next time rather than the count.

### The naming sweep

| member | consumes | verdict |
|---|---|---|
| `AstroImageDocument.AdoptImageAsync` | a parameter | already conforms; it is the precedent |
| `Image.ScaleFloatValuesToUnitInPlace` | its receiver | conforms -- `*InPlace` is the signal for an instance method that spends `this`, and is stronger than `*Into*` would be |
| `Image.DebayerAsync` | **no longer anything** | fixed, see below |
| `Calibrator.Apply` | a parameter | **deliberate exception**, recorded below |

**`DebayerAsync` was convention 5 on the INPUT side, and that is the find of this phase.** Its
mono / full-colour branch called `ScaleFloatValuesToUnitInPlace`, so with `normalizeToUnit` set it
mutated the caller's pixels and returned a view of them -- but only for those sensor types, and only
when the samples were not already unit-scaled. Ownership of the ARGUMENT therefore depended on two
runtime facts invisible at the call site, which is exactly the defect P1 retired for return values,
sitting undetected on the other side of the signature. It now scales into a fresh image and the
method never consumes anything. **Nothing in the tree ever reached the consuming path**: no
production caller passes `normalizeToUnit` at all, and all three test callers that do are RGGB, which
takes the non-consuming branch. So the copy it now costs is paid by nobody today.

**`Calibrator.Apply` keeps its name, deliberately.** By the letter of the rule a method that consumes
a parameter says so in its name, and every candidate (`AdoptAndApply`, `ApplyAdopting`,
`ApplyInPlace` -- which would be a lie) reads worse than the domain verb while saying less than the
doc and `CalibratorOwnershipTests` already say. The rule is therefore stated as covering names that
are otherwise NEUTRAL about ownership -- `CreateFrom*` had nothing to say, `Apply` does -- and this is
logged as the single exception so that it stays one rather than becoming the precedent.

## Invariants (set now, before any code moves)

- **One owner per `Image`, and the owner is whoever the producer handed it to.** Everyone else
  borrows through `TryLease` or does not hold it past the current frame.
- **`Release()` stays idempotent and stays a no-op for self-owned frames.** Convention 2's "release
  and keep reading" call sites are correct and must not be broken by a policy that makes release
  destructive across the board.
- **A rewrap that shares arrays sets `Buffer = null`.** Release responsibility stays with the
  original image; carrying the ref would double-release a refcount-1 buffer. Already stated at
  `ScaleFloatValuesToUnitInPlace` and pinned by `ImageChannelCtorTests`.
- **Ownership transfer is visible in the name.** `Adopt*` and `*Into*` take ownership; a neutral
  `CreateFrom*` or `Get*` does not. This is the existing rule from CLAUDE.md, promoted from a
  naming note to a policy clause.
- **A pooled or driver frame is never held across an `await` without a lease.** The guide loop
  releases the previous frame on every exposure, so the reference is valid only within the frame it
  was read on.
- **Never make a hot path pay for the policy.** The refcount is already a CAS; the policy is about
  what the compiler and the reader can see, not about adding indirection to the imaging path.
- **Ownership is a property of the HANDOFF, not of reference identity.** Never derive "may I release
  this?" from a reference comparison: it answers a different question that merely coincides with the
  right one while a single thread owns the whole chain.
- **The policy must cover convention 4, the only one that mutates.** Typing the own/borrow obligation
  while leaving the consume case as a doc comment types the half that already fails loudly.
- **One word, one meaning: "released".** It meant both "ownership spent" and "float planes dropped"
  on the same type. Settled by P0: ownership is `Release`, residency is evict / restore / resident.
- **Per-frame, never per-sample.** The residency measurement priced a per-sample second struct copy
  plus a dependent load at 8.7-20.3% of the resample loops -- and note WHICH change that was: the
  thread-safe derivation, not D1' itself, which cost nothing. Anything the policy adds belongs at
  frame granularity, and where a per-sample check is unavoidable the cure is a hoist to a scope
  (`ResidentPlanes()`), not a cheaper check.

## Open questions (decide at the phase, not now)

- ~~Does P1 want a type (`OwnedImage`) or a discipline (always copy)?~~ **SETTLED: neither.** The
  measurement refuted always-copy (it removes no allocation, so the copy is a pure addition), and the
  wrapper turned out to encode something the code already knew -- in every case the predicate was one
  branch earlier. See "The P1 measurement" and "What P1 shipped". The `ref struct`-vs-class analysis
  stands and is still the right answer to the narrower question of how to TYPE an ownership
  obligation, if that is ever wanted for `TryLease`'s return.
- **Does the whole stacking pipeline want an ownership model?** Raised by P3's audit rather than
  answered by it. Nothing there releases a frame: not the `LoadFullAsync` consumers, not the warped
  scratch re-readers, and not `FrameCache`, whose WEAK tier structurally cannot hold a recycled frame.
  That is free today and is the single thing standing between the pipeline and pooled reads.
- Should `Array2DPool` participate in `TryLease` accounting, so a pooled frame's refcount and the
  pool's byte budget are one number rather than two? Currently the pool sees a return, and the
  refcount sees a release, and only `ChannelBuffer` knows they are the same event.
- Is convention 3 worth keeping distinct from 1 at all, or is "recycled by someone" the single
  useful category? The consumer rules are identical; only the recycler differs.

## A leased frame could be its own TYPE (raised by the user 2026-08-22)

Out of a review of D1: *"maybe a LeasedImage subclass that has the fast access methods."* The idea
splits into two, and they land differently -- worth recording both, because the appealing half is the
one that does not work.

### Adopted in principle: type the OWNERSHIP obligation -- SHIPPED 2026-08-23 as `ImageLease`

**Shipped the day after the paragraph below resolved the design question.** `ImageLease` is a public
`readonly struct : IDisposable` wrapping the leased image; `TryLease(out Image?)` became
`TryLease(out ImageLease)` in one wave (two production consumers -- `GuidePreview`, the
`FakeGuider.SaveImageAsync` retry -- plus the test suite; no compatibility overload left behind, a
BREAKING public-API change on the package). `default(ImageLease)` is inert: `Dispose` no-ops,
`Image` throws. The own-side counterpart stays `Release` on `Image` itself, mirrored privately by
`ArchiveSolveSurvey.OwnedFrame`.

**The bespoke analyzer is deliberately NOT built, and the reasoning is worth keeping.** Two rules
were on the table. "Never release a parameter" (own-side): audited universally true in P4, but the
naive form only catches the direct spelling -- `Calibrator.Apply` launders through a local, so a real
guard needs dataflow, and the runtime tripwires (the recycled-read throw, the over-release throw, the
DEBUG leak tracker) already make a violation loud in tests. "A lease must be disposed" (borrow-side):
**CA2000 cannot do this -- it ignores value-type disposables** (measured: a forgotten `ImageLease`
beside a forgotten `FileStream`, only the stream fired), so it would need a bespoke analyzer too --
for a pattern with two production call sites, both `using`. A dropped buffered lease surfaces in the
DEBUG leak tracker instead. Revisit if either mistake actually appears in a PR; the repo also carries
36 latent CA2000 warnings on CLASS disposables (measured 2026-08-24), a separate cleanup if wanted.

**The cut also closed a latent poison: the bufferless lease is now a DISTINCT image.** The old
bufferless branch returned `this`, so the borrower's release marked the SOURCE released and every
later `TryLease` of the same published frame refused -- a repeat-polling preview got one frame and
then 404s until the frame swapped. The lease is now always a fresh `Image` sharing the planes (the
harvest finds no buffers, so disposing it is a self-owned no-op); pinned by
`TryLease_OnAnImageWithNoRecycledBuffers_Succeeds_AndTheSourceStaysLeasable`, seen to fail against
the `this`-wrapping form.

`TryLease` hands back an `Image` whose "you MUST `Release()` this" lives only in a doc comment, which
is convention 3 of the table above in its purest form: a rule that exists, is real, and is enforced
by nobody. A distinct returned type would make it compile-time-checkable -- the borrower cannot lose
track of an obligation the type carries. It is orthogonal to performance and is the genuinely useful
version of the suggestion.

Two things to settle when it is picked up. **Where it goes in the hierarchy:** a subclass inherits
every mutator, so a `LeasedImage` still exposes `TryEvictFloatPlanes`; a *wrapper* that exposes the
read surface and its own `Dispose` is the shape that can actually constrain a borrower, at the cost of
forwarding. And **whether `Release` becomes `Dispose`**: a `using` is the only thing that makes the
obligation hard to forget, but `Image.Release` is refcount semantics rather than disposal, and
conflating the two is how a double-release gets written.

**2026-08-23 -- the second question is now half answered, by the double-release above.** `Dispose` on
`Image` itself is the wrong move and for a sharper reason than "the semantics differ": `Release` is a
no-op on a self-owned frame and several call sites read after it, so `Image` fails the one clause
that matters (`ObjectDisposedException` afterwards), while `IDisposable` would point CA2000 at all
239 sites including every borrowed frame and make `using` appear on frames the code does not own --
the exact bug, blessed by the compiler. `Dispose` on a **per-holder wrapper** has none of that: it is
one obligation, one holder, one shot, and it is `Interlocked`-exchange-shaped exactly as
`Image.Release` already is.

That is also the whole difference between detecting and preventing a double-release. A shared count
cannot see which holder is calling; a token IS the holder. So the wrapper is not merely a nicer way
to spell the obligation -- it is the only shape that closes the hazard, and the refcount's throw is
the runtime backstop for code that bypasses it. When this is picked up, the producers to convert are
the ones that hand out an obligation: `TryLease` first (it already returns this thing without naming
it), then the camera `GetImageAsync` path.

### Rejected: a subclass carrying FAST accessors

The performance motivation was real (see the measured numbers on the `SubpixelValue` hoist), but a
class cannot hold the guarantee it would be asserting:

- **The promise is falsifiable after the fact.** `LeasedImage` would assert "my planes are resident"
  in its type while residency remains mutable state on the base -- and `TryEvictFloatPlanes` is
  public and inherited, so one call makes the type's claim false while every holder goes on believing
  it. A per-call check is worse than a correct type and *better* than a type that lies.
- **It would bind on the wrong images.** The hot readers are `Image.Transform` / `Image.StarDetection`
  instance methods where `this` is an `Image`, so a subclass's non-virtual fast methods never resolve
  without a cast or a duplicated loop; making them `virtual` swaps a struct copy for a virtual
  dispatch in a 12.6M-call loop, which is likely worse.
- **The two sets barely overlap.** Leases are camera frames; the resample loops run on stacker images.
  And `TryEvictFloatPlanes` refuses any channel carrying a `Buffer`, so a leasable frame is never
  released and a released image has no buffer to recycle -- the invariant `LeasedImage` would enforce
  already holds for free.
- **It needs `protected` access to the plane array**, widening the surface that must respect residency,
  which is the opposite of the goal.

**What was done instead, and the rule it generalises to:** hoist the residency resolution out of the
loop and hand the loop the `float[,]` planes (`Image.ResidentPlanes()`). A *scope* rather than a type.
If the invariant should ever be expressed in the type system, the right shape is a `readonly ref
struct` -- it cannot be stored in a field, so a residency guarantee physically cannot outlive the
operation that established it, which is exactly the property a class lacks.

## Related

- [stacking.md](stacking.md), [ai-enhancement.md](ai-enhancement.md): the two pipelines with the
  most convention-5 sites.
- CLAUDE.md sections "Image Pipeline & Buffer Lifecycle" and "Image Mutability" hold the current
  written-down half of this; they describe conventions 1, 2 and 4 accurately and do not name 3 or 5.
- [docs/architecture/image-pipeline.md](../architecture/image-pipeline.md): the per-driver recycle
  coverage matrix, which is convention 1's status by backend.
