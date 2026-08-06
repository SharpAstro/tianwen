# Frame Lifecycle (who owns an `Image`, and who is allowed to release it)

**Status: NOT STARTED** (raised by the user 2026-08-06, while adopting the pooled FITS read into
the tile-pipelined stacking strategies). This plan does not invent a new mechanism. The mechanism
already exists and works: `ChannelBuffer` refcounts, `Image.Release`, `Image.TryLease`,
`Array2DPool`. What is missing is a *stated* rule for which of them applies to a given `Image`, so
today the answer is reconstructed per call site from the call site's own knowledge.

The cost is already measurable. `Image.TryReadFitsFile` has 73 call sites outside its own file and
`.Release()` has 203 across the tree, and the correct pairing between them is not derivable from
any signature.

## The problem: five conventions coexist, and none of them is named

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

That idiom appears at six sites in `SharpenPipeline` (lines 163, 228, 240, 264, 701, 738), twice in
`Image.Masks.cs`, once in `MasterPreviewRenderer`, and now once in `RawLightDecoder`. **The
repeated guard is the missing policy, written out longhand at every site that needs it.** It is
also silent when wrong: releasing a frame you did not own recycles pixels another holder is still
reading, which surfaces as a corrupted stack rather than an exception.

### What this cost in the work that raised it

Two concrete outcomes from the pooled-read change, both consequences of the above:

- **The pooled read had to ship opt-in** (`pooled: false` default), purely because ownership across
  73 existing call sites could not be established. Convention 2 is load-bearing for several of
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

## Phasing

| Phase | What | Status |
|-------|------|--------|
| P0 | **Name the five conventions** in one place: an XML doc block on `Image` that the four producers link to, plus the vocabulary (own / borrow / consume) used consistently in member names. Documentation only, no behaviour change, and it is what makes the rest reviewable. | NOT STARTED |
| P1 | **Retire convention 5.** Make ownership of a return value static per method rather than per configuration. Two candidate shapes: make `Calibrator.Apply` always copy (simple, costs one frame allocation on a path that has no masters, which is the uncommon case), or return an explicit `OwnedImage`/`BorrowedImage` result the caller cannot ignore. Decide with the `SharpenPipeline` sites in hand, since they are the volume user. | NOT STARTED |
| P2 | **Debug-only leak detection.** A finalizer on `ChannelBuffer` (DEBUG builds only) that reports a buffer collected while still referenced, attributed to the producer. The pooled survey work found its leaks by watching memory; a counter finds them at the call site. Pairs with the existing `Array2DPool` accounting (`RetainedBytes`, `BudgetEvictionCount`). | NOT STARTED |
| P3 | **Make pooling the default** for bulk readers once P1 lands, and delete the `pooled:` parameter. The flag exists only because ownership is currently unknowable; it should not outlive that. | NOT STARTED |
| P4 | **Sweep the naming.** `AdoptImageAsync` already follows the convention (verb-form ownership transfer); apply it to the rest of convention 4, and audit the 203 `Release()` sites against the stated policy. | NOT STARTED |

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

## Open questions (decide at the phase, not now)

- Does P1 want a type (`OwnedImage`) or a discipline (always copy)? A type is checkable but touches
  every signature in the chain; always-copy is one line each and costs an allocation exactly where
  the pipeline is not doing any work anyway.
- Should `Array2DPool` participate in `TryLease` accounting, so a pooled frame's refcount and the
  pool's byte budget are one number rather than two? Currently the pool sees a return, and the
  refcount sees a release, and only `ChannelBuffer` knows they are the same event.
- Is convention 3 worth keeping distinct from 1 at all, or is "recycled by someone" the single
  useful category? The consumer rules are identical; only the recycler differs.

## Related

- [stacking.md](stacking.md), [ai-enhancement.md](ai-enhancement.md): the two pipelines with the
  most convention-5 sites.
- CLAUDE.md sections "Image Pipeline & Buffer Lifecycle" and "Image Mutability" hold the current
  written-down half of this; they describe conventions 1, 2 and 4 accurately and do not name 3 or 5.
- [docs/architecture/image-pipeline.md](../architecture/image-pipeline.md): the per-driver recycle
  coverage matrix, which is convention 1's status by backend.
