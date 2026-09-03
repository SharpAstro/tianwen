# TODOs

Checks that only a real device or a real night can answer live in ONE place, indexed by the gear they need:
[docs/todo/hardware-validation.md](docs/todo/hardware-validation.md) (the bench queue; tick there, not here).

## High Priority

- [x] **Finalise never stopped tracking; it only checked** (SHIPPED 2026-08-29). The step logged
  "Finalise: stopping tracking..." and then read `IsTracking` -- `SetTrackingAsync(false)` was called
  nowhere in the shutdown path, the only such call in `Session` being the sky-flat routine's.
  **It was invisible on any mount that can park**, because ASCOM's `Park` stops tracking by definition
  and `SkywatcherMountDriverBase.ParkAsync` halts both axes on its way home, so the motor did come to
  rest and the shutdown report's "Tracking stopped" line happened to be true by the end. On a mount
  with `CanPark == false` -- the iOptron SkyGuider Pro is the one in the shipped device list -- nothing
  in the whole finaliser ever stopped the motor, so a completed session left it tracking until the
  battery died or the payload reached the tripod.
  Now it commands the stop when the mount has a tracking switch and then reports **what the mount
  says**, so a tracker that cannot be commanded is never credited with a stop it did not make; that
  case logs a warning naming the mount and telling the operator to stop it at the hand controller.
  **The test needs `CanPark = false` to mean anything** (`FakeMountDriver.CanPark` became settable for
  it): with park available the fake's own `ParkAsync` clears the flag and the test passes with the bug
  still in place. Seen to fail against the original expression before being committed.
  Still open from the same review: **there are no hour-angle or altitude safety limits anywhere** --
  grep finds no `HALimit` / `LimitReached` / `SafetyLimit` -- so a failed flip has no backstop that
  stops a mount tracking into the pier. That is a feature, not this fix, and wants its own entry.

- [x] **Keep the measurement frames, and stamp each light with how well it was guided**
  (SHIPPED 2026-08-29). Two changes from one conversation about where paired blurry/sharp training
  data could come from.
  **`SessionConfiguration.SaveIntermediates`** (default OFF) keeps the frames a session takes to
  MEASURE something and would otherwise release unseen, under
  `Intermediates/<date>/<filter>/<frame type>/`: `FrameType.Focus` for every auto-focus V-curve rung
  plus the verification exposure at best focus (one folder per run), and `FrameType.Scout` for the
  FOV-obstruction probe frames, kept whatever the star count because a zero-star scout is the
  interesting one. It replaces `SaveAutoFocusFrames` and the never-read `SaveScoutFrames`, which had
  been documented as a real feature in six places including as a *precedent to follow*.
  - **Why an AF run is worth keeping at all:** it already sweeps 9 positions across 200 steps and then
    exposes once more at the fitted best focus, so each run is a labelled defocus ladder of ONE field,
    minutes apart, under one sky at one temperature, with `FOCUSPOS` on every frame and the in-focus
    anchor at the end. **The archive cannot supply this and never could** -- per-sub FWHM there runs
    p05 1.96 / p50 2.10 / p95 2.55 px with an intra-session p90/p10 ratio whose median is **1.04**, and
    a scan of all 245,213 indexed files found **zero auto-focus frames**, because N.I.N.A. and TianWen
    both measured the V-curve and threw the pixels away. Costs no exposure time. **Those FWHM figures
    are from the 2026-08-15 store, i.e. the detector BEFORE the deblending work.** Measured on 12
    sessions: the current detector finds 4% more matched stars and reads them 2% wider (+0.039 px on a
    ~2.2 px median), so the spread conclusion survives easily (1.04 against the 1.5 a training set would
    need) but the digits want a `--force-psf`.
    [docs/plans/ai-denoise-deconv.md](docs/plans/ai-denoise-deconv.md) 2.1b carries the measurements.
  - **Each kind gets its OWN `FrameType` rather than one `Intermediate`**, because path is cosmetic in
    this codebase and headers are truth: collapse them and the only way to tell an AF rung from a
    scout is the folder. A scout needs the card most -- it is in focus and points where the lights
    point, differing only in exposure, so nothing in the pixels would stop a scan ingesting it.
  - **A run is a DIRECTORY, not a filename convention.** The first cut encoded the run key in the
    name; writing the test found that the timestamp format contains underscores (`:` being illegal in
    a path), so splitting on `_` silently yields an *hour*-granularity key that merges two runs of one
    evening.
  **Per-light guiding statistics** (`ImageMeta.Guiding` / `GuidingStats`; `GUIDERMS` / `GUIRMSRA` /
  `GUIRMSDE` / `GUIDEPK` / `GUIDEN`, arcsec) reduce `Session.GuideSamples` over the frame's OWN
  exposure window. A survey of 41 N.I.N.A.- and SharpCap-authored archive headers found **zero**
  guiding keywords, so there was no convention to match and these are ours.
  - **Never a rolling session RMS** -- that answers "how is the rig doing tonight", a different
    question, and is actively misleading stamped on a sub taken during the other hour.
  - **Settling and dither samples inside the window are INCLUDED.** A live guiding display excludes
    them because a dither is a commanded move rather than an error; that reasoning inverts for a sub.
    If the guider had not settled while the shutter was open the frame IS smeared, and filtering would
    make the worst frames of the night report the cleanest numbers.
  - **Null is not zero.** An unguided rig writes no cards at all; `GUIDERMS = 0` would claim perfect
    guiding. `GUIDEPK` is carried because RMS is worst at describing the failure that actually ruins a
    sub -- one gust -- and averaging is exactly what erases it.
  - The session stamps `ICameraDriver.GuideStats` just before `GetImageAsync`, since the statistic is
    only complete once the shutter closes and that call is the one place an `ImageMeta` is built. A
    guiding concept on a camera interface is a layering compromise, taken because that is the
    established seam for session-stamped header facts (`Telescope`, `Filter`, `FocusPosition`,
    `Target`) and the alternative buys a tidier interface at the cost of a second way to get a card
    into a header.
  Pinned by `SessionAutoFocusFrameCaptureTests`, `GuideStatisticsTests` and end-to-end cases in
  `SessionScoutAndProbeTests` / `SessionImagingTests`; the last was SEEN to fail with the stamp
  removed, because a wiring no-op here is silent (empty windows, absent cards, perfectly good frames).

- [x] **Star detection: two tight stars are reported as two** (SHIPPED 2026-08-28). Detection used to
  report one star per blob and put it at the blob's centre of MASS, which on a pair is exactly where
  no star is. It now offers such a measurement to a deblender (`Image.StarDeblend.cs`) that fits a
  several-component model of a COMMON point-spread function to the aperture's pixels and reports the
  components. Measured on `RGGB_frame_bx0_by0_top_down`: the closest accepted pair falls **5.10 px ->
  2.57 px**, pairs closer than the wider star's suppression radius go **0 -> 18**, counts 2,983 ->
  3,065 at SNR 10 and 2,724 -> 2,769 at SNR 30, with HFD p50 unmoved (2.40 -> 2.39) and p95 TIGHTER
  (3.28 -> 3.14). Against planted ground truth (`StarPairDeblendGroundTruthTests`), 4 px pairs go from
  3 of 6 components recovered to **6 of 6** and 6 px pairs from **0 of 6** -- the merged blob was
  refused outright -- to 6 of 6, with components landing within 0.05 px of the planted position.
  **The three things that made it work, after two radius attempts failed:**
  - **It is a SHAPE test, not a distance test.** Two maxima are two objects when the dip between them
    falls below 85% of the fainter one. A radius asserts where a companion may be; a saddle asks
    whether one is there. It also disqualifies a saturated flat top for free (its saddle equals its
    peak), which every previous attempt needed a special case for.
  - **The fit is expectation-maximisation over a shared-width Gaussian mixture**, not
    Levenberg-Marquardt: no Jacobian, no matrix inverse, no line search, cannot diverge, fixed cost.
    The width is shared deliberately -- give each component its own and a bright one swells until it
    absorbs its neighbour.
  - **A phantom is structurally impossible this time**, which is what the reverted attempt could not
    say: the deblender never re-runs `AnalyseStar` from a new pixel (that is what fed a shoulder pixel
    a 29x29 box containing both stars and produced a midpoint phantom). It only ever REPLACES one
    accepted measurement with components inside its own aperture, or declines and leaves it alone.
  **Two things it deliberately does not do.** Below about 2*sigma separation (~2.6 px on that frame)
  two Gaussians have only ONE maximum, so no peak-based method resolves them and none is claimed; the
  synthetic curve reports that band rather than pinning it. And a deblend never COSTS a detection: if
  the components all fall below the SNR floor the merged measurement is reported instead (without that
  fallback the 28-star fixture went 89 -> 88, and it has no pair closer than 11.8 px to deblend).
  **Still open, unchanged by this:** 463 of 3,065 stars (15.1%) have above-threshold pixels reaching
  outside their `1.5 * HFD` mask, because HFD is a FLUX radius and understates a saturated star's
  footprint. That is the DUPLICATE half of the one-radius-two-jobs problem, not the merge half;
  duplicate pairs are pinned at 0, so it is currently costing nothing.
  Provenance note: the mask plus HFD scheme is the ASTAP method (LGPL-3.0). The deblender is not
  ASTAP's -- ASTAP does not deblend -- but it sits inside that method, so a rewrite of the surrounding
  code still needs the same licence care the original import did.
- [x] Own AI denoise/deconv training dataset (**P0 SHIPPED 2026-07-12**); `tianwen dataset build` runs end-to-end: single archive scan -> discover sessions + archive-wide header-matched calibration (`CalibrationResolver`, dark/bias libraries shared across sessions, masters build-once via fingerprinted `MasterCache`) -> star-count-led quality gate (`SessionFrameAnalyzer`; on a fast refractor star count is the discriminator, HFD *inverts* under transparency loss) -> register + integrate the session master **unnormalised** (`SessionRegistrar`, reuses the stacker's quad-match + Float16Staged integrator) -> structure-biased 256px cells -> **zero-skew** fp16 N2N tiles + JSONL manifest (`DatasetTileExporter`; every frame through the *same* `ChunkedNafnetRunner.ApplyInputStretch` inference pre-stretch) -> PSF/noise field-radius report (`DatasetPsfNoiseReport`) -> pinned **by-session** split (`DatasetSplitWriter`) -> in-run parity gate (`VerifyParityAsync`, maxDiff 0). License-clean (N2N sub-pairs + synthetic-PSF degradation, **no** RC-Astro outputs anywhere in the ML loop). `DatasetBuildRunner` (in `TianWen.AI.Imaging`) orchestrates; 22 tests, all validated on the synthetic RGGB fixture. **Real-archive run DONE 2026-07-15**: `D:\Astro-Dataset\2025-2026` holds 45 sessions / 4,958 subs / 121,500 tiles + 5 pinned test sessions. Two follow-ups before P1 trains on it: those tiles predate the master-flat pedestal fix (2026-08-03), and § 2.3b of the plan records the root order plus the two session groups (BAD LIGHT EXAMPLES, QHY294PROC) that no header gate excludes. Then P1 (NAFNet-32 N2N training on RunPod).** **Blocker for narrowband archives CLEARED 2026-08-02:** `SessionDiscovery.GroupSessions` keyed on `(SessionDir, Instrument, Target)` with **no filter**, so a mono Ha+OIII night collapsed into one session; the MAD star-count gate rejected the OIII frames as a left tail (they legitimately detect far fewer stars) and `SessionRegistrar` stacked both filters into one meaningless master, silently. The key now carries the filter. The obvious fix was insufficient: `Filter.FromName` is anchored, so `Ha 3nm` / `Antlia ALP-T` all canonicalise to one `Filter.Unknown`, and keying on the canonical name (what `MasterGroupKey` compares on) would have re-merged the lines; the key falls back to raw header text. That also disproved the plan's assumption that narrowband dispatch is a free `Bandpass` bit test, since `FILTCLAS` is TianWen-written and a N.I.N.A. frame's bandpass comes from the same anchored parse. See [docs/known-limitations.md](docs/known-limitations.md). The same sweep is also how [narrowband-colour](docs/plans/narrowband-colour.md) gets validated (and how we could **measure** our own dual-band crosstalk coefficients instead of sourcing a published table). See `docs/plans/ai-denoise-deconv.md`.
- [x] MiniViewer: optional lightweight mode that skips storing UnstretchedImage, for live preview where we never re-stretch, just keep stats + GPU texture. Saves ~140MB per displayed frame
- [x] Cache altitude chart as texture, only re-render the mouse follower overlay on hover, not the entire chart. Currently 20% GPU on mouse hover due to full chart redraw per frame
- [x] TianWen.Hosting remote API: ASP.NET Core Minimal API + WebSocket for headless Raspi operation. Multi-OTA native routes (`/api/v1/ota/{index}/camera/info`) with ninaAPI v2 compatibility shim (`/v2/api/*` → OTA[0]) so Touch N Stars works for single-scope setups. All 4 phases complete: read-only state, control, ninaAPI shim (equipment info/control, sequence, images, WebSocket, device lifecycle, guider graph, move-axis), profile CRUD + pending target queue. `tianwen-server` headless executable published as AOT binary for all platforms
- [ ] **Viewer memory footprint**: a 13228x9354x3ch 8-bit TIFF costs ~2.5 GB (19 B/px, measured and fully decomposed), and the Vulkan staging buffer's 472 MiB of that is high-water-marked for the PROCESS LIFETIME -- `EnsureStagingBuffer` is grow-only and freed only on dispose, so every small FITS opened afterwards still carries it. Three items: M1 explicit `TrimStagingBuffer` from the document-load path (not inside the upload -- the live path uploads a ~104 MB channel per frame); M2 a decode-into API in `SharpAstro.Tiff` so strips land straight in the float planes instead of via a whole raster (-354 MiB peak); M3 (design only) let the VIEWER hold the source bit depth -- `Imaging` is rightly float throughout, but the shader is indifferent, since `texture()` on `R8Unorm` returns [0,1] exactly as `R32Sfloat` does. See `docs/plans/viewer-memory-footprint.md`.
- [ ] PlayerOne Astronomy / ToupTek / SVBony native drivers; these vendors use ZWO-compatible SDKs with different library prefixes (PlayerOne: `PlayerOneCamera`, ToupTek: `toupcam`/`starshootg`, SVBony: `SVBCameraSDK`). Investigate sharing `ZWODeviceSource`/`ZWOCameraDriver` infrastructure with a pluggable SDK shim rather than duplicating per vendor. NINA uses a `ToupTekAlike` pattern for this family. Cameras, filter wheels, and focusers where applicable
- [x] Catalog cold-start Phase 2 (pre-bake init state) -- **CLOSED 2026-09-03; 2A, 2B and both halves of 2C are in, and nothing actionable remains**; see `docs/plans/catalog-binary-format.md` § Phase 2. **2A SHIPPED 2026-05-05:** `hd_hip_cross.bin.gz` snapshot (~350 ms saved). **2B SHIPPED 2026-05-05:** `simbad_merge.bin.gz` snapshot (~180 ms saved). **2C: the Tycho-2 bulk-load half is DONE** (measured 2026-08-31 at **0.3 ms** of init -- `ExpandTycho2` plus the background task closed it; `tyc2.bin` already carries a per-GSC-region offset table). **2C's BFS half was SUPERSEDED, not skipped** (`a7c7f9a2`, 2026-08-09): the plan offered pooled frontier buffers (~0 B/call, walk unchanged) or transitive closures pre-computed at init (dict hit, +~50 ms of init), and `_crossIndexClosures` -- a lazy `ConcurrentDictionary` memo on `TryGetCrossIndices` -- delivers the second option's outcome without its init cost, because the closure is a fixed function of an append-only-then-frozen table. Found by measuring rather than by this plan: the sky map's full-sky overlay gather allocated 78.1 MB a pass and `GC.GetAllocatedBytesForCurrentThread` deltas put **53.89 MB of it in `TryGetCrossIndices` alone**; memoising it (plus a `RaDecIndex` cell-merge cache and dropping a duplicate ask per object) took the gather to **22.98 MB and 105.7 -> 84.8 ms, Gen0/1000 8833 -> 1833**. The early-out for a missing row shipped with it and answers only a quarter of calls -- 113k of the 151k objects a sweep visits DO have a row -- so the cache, not the early-out, is what did the work. Cost stated: up to ~20 MB resident once a session has swept the whole sky, against 54 MB of churn per pass; revisit if this ever runs somewhere small. **`ReadTycho2CrossRefArrays` FIXED 2026-08-31:** it still lzip-decompressed `hip_to_tyc`/`hd_to_tyc` (274.7 ms) because only `tyc2.bin` had been given the build-time expansion; `ExpandTycho2CrossRef` now expands both into `obj/` (LFS-neutral, the committed `.lz` untouched as fallback). **Init 587 -> 343 ms, the blocking join phase 269.9 -> 0.0 ms.** The per-record base91 string round trip is ALSO fixed (`CatalogUtils.Tyc2CatalogIndex`): it allocated a string AND a boxed enum per star, **33.7 MB of Gen0 garbage and 11 collections -> 3.84 MB and 4**, where 3.84 MB is exactly the output arrays. Note `AbbreviationToEnumMember<T>`'s `Enum.ToObject` boxing affects every other catalog-parse caller too and is deliberately untouched. Largest remaining item is `hd-hip-cross` at 121.8 ms, and that IS the 2A fast path -- deserialise-and-apply, not the 330 ms recompute the snapshot replaced -- so it is the phase's intended end state rather than an outstanding item. Phase 2 as written targeted 280-400 ms and init went 729 -> 343 ms.

## Flaky CI Tests

- [x] **`SessionImagingTests.GivenCloudsRollingInWhenStarCountDropsThenConditionDetected` -- NOT flaky;
  the pump's budget was measuring the CI runner** (red on `3f870333`, run 33687279158, 2026-09-02;
  fixed 2026-09-03). It failed `imagingTask.IsCompleted` after spending its whole 4-hour fake-time
  budget in ~4 s of wall clock on a 30-minute observation. `PumpUntilCompletedAsync` paced on
  `WaiterCount`, which is **global**: a fake guider's capture loop and a fake camera sit parked in
  `SleepAsync` more or less permanently, so "is anyone waiting?" answered yes whether or not the loop
  being driven had caught up. Meanwhile the imaging loop's tick is a `PeriodicTimer`, which registers
  no waiter **and coalesces** -- a tick firing while its continuation is still queued is dropped, not
  queued behind the last one. So every advance the loop did not observe was budget spent for nothing,
  and how many of those there are is a property of the thread pool, not of the session.
  **Measured, one machine, one test, nothing but scheduling changing: 30 minutes of observation cost
  33-50 minutes of budget** (idle 37-50 min, under 16-thread CPU load 33 min -- load made it *better*,
  because it slows the pump too; the full functional suite 41 min). CI needed ~8x and was never
  reproduced here, so the mechanism is measured but the CI red itself is not a red-to-green repro.
  **The budget now bounds a STALL, not the run**: `PumpUntilCompletedAsync` takes
  `progress: () => ctx.Session.ImagingLoopTicks` (new `Session.ImagingLoopTicks` seam) and resets the
  budget whenever the loop moves, so a starved runner merely takes longer while a loop that has
  genuinely stopped still trips it; a real hang stays bounded by `[Fact(Timeout)]`. It now **throws**
  with the counters instead of returning quietly into a downstream `IsCompleted.ShouldBeTrue` that
  cannot tell a stalled loop from a starved one. All 11 session-loop call sites pass the probe; the
  scout site keeps the run-bounding fallback deliberately. Pinned by `FakeTimePumpTests`, where the
  **no-probe case is the old pump kept green as the shape of this failure** -- the two tests differ
  only in whether the probe is supplied, and the probe case was seen to FAIL against the old
  semantics before being committed.
- [x] **The same clouds test detected no condition at all, and finding out why turned up three more
  bugs** (2026-09-03). It asserted only that it had SET `CloudCoverage`; measured, it produced **0
  "Condition deterioration detected" events** over 59-60 frames and never entered the recovery path.
  Four separate causes, each found by measuring the one before it:
  - **The cloud window was one pump iteration.** Clouds go in once the baseline exists (pump 9-39
    depending on the runner) and were cleared on `iteration > 10`, already true by then. Now keyed on
    `Session.ConditionDeteriorationCount > 0`, so the sky clears once the loop has actually noticed.
  - **`CloudCoverage` was not monotonic in obscuration.** The opacity ramp divided by `1 - threshold`,
    which IS the coverage, so the ramp FLATTENED as coverage rose. Measured on the imaging path
    against a 41-star clear baseline: 0.5 -> 19 stars, then 0.6 -> 25 and 0.7/0.8/0.9/0.95 all -> 26,
    with a cliff to 0 only at exactly 1.0 (a different branch, which renders no stars at all). The
    test's 0.8 sat on that 0.634 plateau, comfortably above the 0.6 gate. Fixed with a fixed edge
    softness.
  - **The glow carried no shot noise**, and the cloud is applied to an image whose noise is already
    baked in, so a uniform multiply-plus-constant scaled signal and noise together and left SNR --
    and the star count -- untouched. Only PATCHY cloud ever cost a detection. Fixed; uniform overcast
    at 0.9 went 38 -> 28 stars on its own.
  - **Extinction was capped at 90%**, i.e. 2.5 magnitudes, so the brightest stars punched through any
    overcast -- exactly what two guider tests' comments described before reaching for a hard 1.0 to
    get a starless frame. Replaced with Beer-Lambert (`exp(-6 * opacity)`). Final curve: 0.0 -> 41,
    0.3 -> 25, 0.5 -> 13, 0.8 -> 0. Pinned by
    `ConditionDeteriorationTests.StarCount_IsMonotonicallyNonIncreasing_InCloudCoverage`, seen to fail
    against the old model (`0.60->41, 0.80->41, 0.95->41`) before being committed.
- [x] **`fetchImagesSuccessAll` was a constant `true` on every single-OTA rig** (found 2026-09-03 by
  the repaired clouds test, fixed with it). `BitVector32`'s `int` indexer is a bit **MASK**, not an
  index, so `imageFetchSuccess[0]` is mask 0: it reads false forever and writes nothing. The vector
  was also seeded `new BitVector32(scopes)` (data = the OTA count, not zero), and
  `BitVectorExtensions.AllSet(n)` masked on `n - 1`, which is **zero for one OTA** -- and
  `(Data & 0) == 0` is unconditionally true. So the per-frame gate never worked, and the whole
  metrics block (focus-drift trend AND condition deterioration) ran on EVERY 5 s tick against
  whatever `_lastFrameMetrics` last held, instead of once per 30 s frame. Under cloud that is a
  pause/recover thrash: **297 deteriorations and 297 recoveries over 322 ticks, 4 frames written**,
  against 1/1 and 59 frames after the fix. Masks are now `1 << i` and `AllSet` uses
  `(1 << bitCount) - 1`.
- [x] **Dithering was expressed in ticks and only ever fired because that gate was broken** (same
  sweep). `tickCount % ditherEveryNTicks == 0` against a frames-to-ticks conversion: with the gate
  fixed, this block runs only on ticks where a frame completed, and those land on one phase mod
  `subExposure/tick` (1, 7, 13, ... for a 30 s sub on a 5 s tick), so a modulus keyed to multiples of
  6 coincides with them only if the phase happens to be 0 -- otherwise it never dithers at all, which
  is how `GivenDitherEveryNthFrame...DitheringTriggered` went red the moment the gate started
  working. Now counts the frames the block actually sees, which is what `DitherEveryNthFrame` says.
- [x] `SessionImagingTests.GivenHighAltitudeTarget...HighUtilization`: fixed: cooperative time pump (`ExternalTimePump + Advance`)
- [x] `SessionImagingTests.GivenDitherEveryNth...DitheringTriggered`: fixed: same root cause (SleepAsync pump race)
- [x] `SessionImagingTests.GivenFocusDrift...AutoRefocusTriggered`: fixed: same root cause
- [x] `SessionPhaseTests.AbortDuringCooling_StopsRampAndWarmsBack`: fixed: removed wall-clock CancellationTokenSource timeouts
- [x] `SessionObservationLoopTests.GivenAcrossMeridianTargetWhenHACrossesDeadbandThenFlipAndContinueImaging` -- fixed: root cause was `PlateSolveAndSyncCoreAsync` (`Session.Focus.cs`) being the only `StartExposureAsync` call site with no Idle/abort precondition. During a meridian flip (`PerformMeridianFlipAsync` -> `CenterOnTargetAsync` -> 5s plate-solve frame) the prior science sub-exposure could still be `Exposing` under the two-thread time-pump interleaving, so the driver rejected the solve frame with `InvalidOperationException: camera state being Exposing` (which also surfaced as `TotalFramesWritten=0` when it aborted the flip). Now aborts any in-progress exposure and lets `Download->Idle` settle before the solve exposure, mirroring the condition-recovery / obstruction-scout guards. Verified 20/20 green.
- [x] `SessionFilterTests.GivenSingleFilterPlanWhenImagingThenFramesCapturedWithoutFilterSwitch`: fixed: the LAST hand-rolled pump of eleven sites, migrated to `PumpUntilCompletedAsync`. Same root cause as the three above. This file had never picked up either half of the convention: `ExternalTimePump` was never set, so `SleepAsync` took its auto-advance branch and the test loop AND the session loop both called `_fake.Advance` concurrently (precisely the race that flag exists to prevent), while 30 x `Advance(30s)` outran a 3-minute, 6-tick observation window on roughly 100 ms of real budget per iteration. Measured on an idle box, n=8 per arm: the old pump captured 4 frames of the 6-tick window on all 8 runs, the shared pump 5 on 7 of 8; the old pump dropped to 3 when the box was busier, which is the load-sensitivity the mechanism predicts. **NOT reproduced on demand** (12/12 green under CPU load, 6/6 full-functional-suite runs), and the original assertion message was never captured, so the red-to-green transition is unproven and this rides on CI to confirm. Both async tests also gained the `[Fact(Timeout = 120_000)]` that every other session test carries: `PumpUntilCompletedAsync` waits indefinitely for the loop to re-park, so a genuine hang had no bound, including in this file's sibling test which already used the helper correctly but carried a bare `[Fact]`.

## Next Up

- [x] **SkyWatcher driver: `RaToSteps`/`DecToSteps` only ever produce the Normal-state axis solution.**
  **DONE 2026-08-30 -- `SkyToSteps(ra, dec, PointingState)`**: a goto chooses the solution from
  `DestinationSideOfPierAsync` once and keeps it for refinement passes, a sync keeps the half the Dec
  encoder is in, `StepsToRa` reads the half off the Dec encoder, home boundary inclusive (Normal). Six
  `FakeSkywatcherMountDriverTests` cases, five seen to fail first; an unflipped fake now really flips on
  a re-slew. Hardware validation is queued in
  [docs/todo/hardware-validation.md](docs/todo/hardware-validation.md) items 1-3; `SetSideOfPierAsync`
  became the forced flip later the same day. Original finding: there was no through-the-pole branch (GSServer chooses one from the destination's hour angle), so a
  goto or sync to an EASTERN target lands the encoder model in `Normal` -- counterweight-UP in the
  driver's own convention (home = HA 6 h, counterweight down) -- and a session "flip" re-slews to
  identical encoder targets, i.e. moves nothing. Found 2026-08-30 while fixing the mount-limit
  pointing-state bug: the limit reads the driver's state, so on this driver it fires right after a slew
  to an eastern target and never on the west-tracking case. The port is GSS `origin/master`
  `Axes.RaDecToAxesXy`'s `if (axes[0] > 180) { X += 180; Y = 180 - Dec }` branch (Dec sign mirrored
  south FIRST), chosen per goto from the target's hour angle; `SyncRaDecAsync` picks the solution
  nearest the CURRENT encoder half (a sync says where the mount IS, it must not teleport the model
  across the pier). GSS itself never flips while tracking -- the flip IS the
  next goto landing on the other solution -- which matches how `Session` already re-slews.
  [docs/plans/mount-safety-limits.md](docs/plans/mount-safety-limits.md), "the pointing state".
- [x] **A COMPUTED pointing state must not feed the mount limit as if measured** (LX200 base, SGP,
  `FakeMountDriver`). **DONE 2026-08-30**: `IMountDriver.PointingStateSource` (None/Computed/Measured,
  default Computed) + `MountLimits.TrustedPointingState`; the flip gate keeps the computed answer. `MeadeLX200ProtocolMountDriverBase.CalculateSideOfPierAsync` and the fake derive
  pier side from HA (`>= 0 -> Normal`), SGP answers a constant `Normal`: the state a mount WOULD be in
  if its firmware always flipped. West of the meridian that reads as post-flip, so the meridian limit
  can never fire on those drivers -- wrong on any LX200-protocol mount that tracks past the meridian
  until the next goto. OnStep (`:Gm#`), SkyWatcher (Dec encoder), ASCOM/Alpaca report the mechanical
  state and are fine. Decide: computed answers reach `MountLimits.Evaluate` as `Unknown` (HA
  approximation, fires past the meridian), or split "measured vs computed" on `IMountDriver` -- the
  flip gate wants the computed one and has `DestinationSideOfPierAsync`. Same plan doc, same section.
- [ ] **LAN.Lib 2.0 pin bump, once nuget.org has it** (LAN.Lib branch `feat/discovery-port-and-bind-degradation`,
  `7572b68`): the discovery port moved 52821 -> 38821 (out of Windows' dynamic range, where Hyper-V/WSL
  exclusions killed tianwen-gui at DI resolution with WSAEACCES 10013 on 2026-08-30) and a failed bind now
  degrades to announce-only with `ILanTransport.Degradation` instead of throwing. When the package is
  published: bump `LAN.Lib` in `src/Directory.Packages.props` to 2.0.*, log `lanDiscovery.Degradation` once
  from `TianWen.UI.Gui/Program.cs` after `StartAsync` (the server gets it from `LanDiscoveryHostedService`),
  and replace the four `52821` literals in `docs/plans/remote-profile.md`. Until then a local build already
  uses the sibling source (`UseLocalSiblings`); CI still builds against 1.2. Old and new nodes on one LAN do
  not see each other -- update every node together.
- [x] **Mount safety limits: P3's GUI half, P4, P5, and the P1 editor UI**
  ([docs/plans/mount-safety-limits.md](docs/plans/mount-safety-limits.md)).
  **ALL DONE 2026-08-30** -- editor (`PanelSection.MountLimits` + "Meridian Flip" config group with the
  clamp caveat), GUI watcher (`Program.cs`), P4 surfacing (telemetry -> wire -> mirror -> Home board ->
  feeds), P5 (`MountLimitKind.DriverEnforced`), plus: only a MEASURED pointing state drives the limit
  (`IMountDriver.PointingStateSource`), the watcher matches profiles by `Uri.DeviceKey`, SkyWatcher
  `SetSideOfPierAsync` is the forced flip. Still open (plan doc, "What is still open"): hardware
  validation of the SkyWatcher axis-solution port, the tier label in the editor, OnStep's axis angle, a
  limits editor row in the TUI equipment tab (the TUI has the config group + caveat, the watcher and the
  feed hook, not the editor), a session-less verdict surface on the server. Verified live in the GUI
  2026-08-30 (plan doc, "Live verification"): the watcher's verdict now reaches the Home card and the
  feed with no session (`MountLimitWatcher.VerdictFor`), and the start-up wedge that had blocked the
  live check was a profile scan probing every COM port (fixed in `DeviceDiscovery`, plus bounded serial
  writes/closes and per-port give-up in `SerialProbeService`).
  Original entry follows.
  **P0 + P1 + P2 shipped 2026-08-29, so a configured limit now actually stops a mount during a run.**
  The config persists on `ProfileData.MountLimits` (nullable = never configured = disabled) and is
  projected onto `Setup` by `SessionFactory`; enforcement is in `PollDeviceStatesAsync` (the poll,
  NOT the imaging tick -- it is what every slew wait and focus routine already calls) and routes to
  the new `ImageLoopNextAction.LimitReached`. Altitude comes from the new geometric
  `SiteContext.AltitudeDegrees`, unrefracted on purpose. 6 session tests + 9 altitude tests, two
  sabotages verified.
  **What remains, in rough order of value.**
  (a) **The P1 editor UI** -- the config persists and enforces, but nothing lets a user set it
  except editing profile JSON. Design notes from the 2026-08-29 session are in the plan doc ("P1
  editor UI: design notes"): a `PanelSection.MountLimits` after `Site` modelled on `BuildSite`, and --
  the user's ask -- the FLIP settings get their first UI in the same editor and are validated against
  the limit (a flip deadline the limit would clamp is flagged, both in minutes).
  (b) **P3, the half GSServer gets for free and we do not:** enforcement with **no session
  running**, which needs a watcher respecting the hub lease -- observe while a run owns the mount,
  act when nothing does. This is also what would protect the rig during a manual 2am slew, the case
  P1's placement on the profile was chosen for.
  (c) **P4 surfacing** (notification feed, a `LimitAlarm` on `ISessionTelemetry` for the Home
  board's rig card, warn threshold as a countdown beside `MeridianFlipUtc`) -- `Session` already
  exposes `MountLimitVerdict` for this, and nothing reads it yet.
  (d) ~~P1b axis modelling~~ **DONE 2026-08-30 for SkyWatcher**: `IMountDriver.GetAxisAngleAsync`
  (degrees from the counterweight-down home, hemisphere-corrected, null elsewhere), `Evaluate` prefers it
  (`|angle| - 90` = counterweight above horizontal, no clock/site/sync), `MountLimitVerdict.Basis` labels
  the tier. OnStep exposes raw steps but was not modelled.
  (e) **P5**: observe a driver-enforced limit rather than duplicating it, so a GSS-managed rig does
  not read as a malfunction.
- [ ] **GSS parity: what is left of the pulse contract** (findings 1 and 2; finding 3, the
  unverified pulse-restore, is FIXED) ([docs/plans/gss-parity-audit.md](docs/plans/gss-parity-audit.md)).
  **Done so far:** the restore and both axis stops are verified with a retry and throw
  `SkywatcherDriverException`, which the guider already turns into a session-visible fault (the
  blocking shape never got this right "for free" -- a refusal only reached the log and a timeout
  reached nothing); and the pulse is now **two methods**, `StartPulseGuideAsync` (the primitive:
  command and return, `IsPulseGuidingAsync` carries progress) plus `PulseGuideAsync` (the composite
  on the internal guider surface: start AND wait). 82 references over 29 files, no behaviour change,
  and `GuiderCalibration`'s eight hand-written start-then-wait pairs collapsed to eight single calls
  with the wait hoisted into the composite.
  **Also done:** the composite has a **two-axis overload** plus a `CanPulseGuideSimultaneously`
  capability answered by all 13 implementations from the mechanism (SkyWatcher's
  `_pulseGuideInFlight` was ALREADY a counter for exactly this; DAL keys stop timers per direction;
  ASCOM and Alpaca answer false because the spec has no word for simultaneity and guessing wrong
  there throws mid-guide). The branch lives INSIDE the composite, never in the caller. And
  `GuideLoop` calls it once with both corrections -- **a bug fix, not the speedup**: overlapping
  only helps Synta hardware, while on every other family the loop **never waited for a pulse at
  all**, which the blocking SkyWatcher driver was accidentally covering up.
  **Also done: `SkywatcherMountDriverBase` holds the duration on a background task**, so every
  driver honours the primitive now. Split at *commanded* (four `TrySetResult` points, one per
  branch) so a mount that will not accept the pulse is still the caller's problem; the in-flight
  count rises before the first write and falls when the hold ends; and a failed restore, which no
  longer has a caller to throw to, parks in `_pendingPulseFault` and is re-thrown from the next
  start AND from `IsPulseGuidingAsync` -- the latter deliberately, since the guider polls it while
  waiting for the very pulse that failed. Done LAST on purpose: before the guide loop waited, this
  would have left a window in which NOTHING waits on any mount.
  **Slews DONE 2026-08-30** (`_slewCommandedAtTicks` + `SlewStartGrace` in `SkywatcherMountDriverBase`,
  pinned on the fake SkyWatcher with its new `slewStartLatencyMs` knob; see the audit's finding 2).
  **What remains of finding 2:** audit the OTHER drivers for the same "flag observable before the
  starter returns" property. SkyWatcher and LX200 are right by construction; ASCOM/Alpaca inherit
  whatever the remote driver does and have not been checked.
  Also: cancellation must reach an unawaited pulse, and `_pulseGuideInFlight` must be set BEFORE the
  write or the conversion introduces finding 2.
  **A test for it must assert on fake time traversed per guide frame** -- `GuideLoopTests` builds
  `FakeMountDriver` so it never blocks, and where the blocking driver is driven the fake clock
  auto-advances `SleepAsync`, so the stall is real and costs no wall time.
  **24-bit rollover: CLOSED, no action.** GSS does nothing about it either -- its pointing path is
  the same three stateless lines ours is, and `6e6dba9` turned out to be a scripting-API diagnostic
  (only internal caller feeds PLOTTING), not machinery. +/-0x800000 is +/-0.93 rev from home at an
  EQ6's CPR, so a GEM meets its pier or cable wrap first: the real protection is the mount safety
  limits, not wrap tracking. Tracking it would need persistent per-axis revolution state that goes
  stale the instant anyone touches the hand controller, which mis-points by a whole turn and looks
  like a sync bug -- worse than the raw discontinuity it replaces.
  **Still unanswered:** whether to regenerate `gss-oracle-transcripts.json` against `origin/master`.
  It currently pins GSS's PRE-fix behaviour, and regenerating may legitimately turn
  `SkywatcherGssOracleTests` red. Note while answering the rollover question we found a probable
  copy-paste bug in upstream `SkyServer.GetRawStepsDt` (different command type per axis) -- GSS is a
  reference, not a specification.
- [ ] **Astro Photo Viewer, next release (P11-P21, from the user's notes 2026-08-22 and 2026-08-27)**.
  **Shipped:** the version now leads `--help` (plus `--version`, and the same line in the in-app `?`
  panel beside the AI-enhancer discovery status, which reports which backend resolved, which RC
  products are licensed and which SAS models are missing -- **without** undoing the deliberate
  deferral of the RC-vs-SAS license probe to the first `EnhanceAsync`); gain/ISO + offset render in
  the info pane; an EMPTY instance adopts any opened file instead of spawning a second window; and
  right-click on the image copies RA/Dec, the per-channel value or the position (which is also what
  found that no viewer dropdown had ever had a mouse hover state). **Remaining, in order:** a way to
  FETCH the missing SAS models, since `tools/tianwen-ai-models-fetch.ps1` is a repo script and a Store
  install cannot reach it (P11); **Save as seen on screen** + Save-As over the formats the codecs
  facade already writes, and iconising Open/Save to buy toolbar width (P18); **carry the display state
  across frames of the same shape, which is the enabler for a BLINK mode** over the file list -- the
  transport already exists for SER, and without the carry-over each frame solves its own auto-stretch
  so a sequence flickers in brightness rather than showing what moved (P19); and in-depth user
  documentation for the Store listing to point at, written last so it documents what the rest do
  (P13). See `docs/plans/viewer-prerelease-fixes.md` (phases G and H) and
  `docs/architecture/desktop-shell.md`.
- [ ] **Atlas planet detail** (tracked in the plan only, per the user): vmag sparkline + visibility curve + the date of the next opposition / greatest elongation for a selected planet. `SkyPathEventDetector` already computes the events and draws them as rings along the selection path, so the date is a text row over tested math -- except the 120-day path window is far shorter than a synodic period, so the event query needs its own coarse long sweep. Also fixes a planet's info-panel magnitude, which is currently the STATIC catalog `V_Mag` and so is off by magnitudes for most of Mars's cycle. See `docs/plans/atlas-planet-detail.md`.
- [x] RC-Astro enhancer integration: drive RC-Astro StarX/NoiseX/BlurXTerminator (encrypted ONNX, so via the `rc-astro` `--json` CLI, not in-proc ORT), preferred over the SETI Astro ONNX enhancers when the CLI is installed + the product is licensed. **Phase 1+2 SHIPPED:** `RcAstroCli` + NDJSON parser + FITS round-trip base, `RcAstroStarRemover`/`RcAstroDenoiser` (noise-adaptive `--dn`)/`RcAstroNonStellarDeconvolver`, deferred license-gated selector (`DeferredEnhancer` proxy, no subprocess at DI build/resolve), wired into `TianWen.Cli`. 13 tests. **Phase 3 SHIPPED (PR #59, 2026-06-30):** immutable threaded `EnhanceOptions`/`EnhanceTuning` (no mutable singleton) + shared `EnhanceOptions.TryParse`; CLI flags (`--ai-backend`/`--bxt-sharpen`/`--nxt-denoise`/`--nxt-iterations`) on `image sharpen` + `stack --enhance` (3a); per-step `EnhanceProgress` -> CLI printer (3b); interactive Enhance action in `tianwen-fits` (3c); `tianwen-server` `POST /api/v1/image/enhance` single-flight endpoint + `ENHANCE-PROGRESS`/`-COMPLETED` WS, presence-gated 503 when no pipeline (3d). Job-id/queue model deferred as a nice-to-have (`docs/plans/server-enhance-job-model.md`). See `docs/plans/rc-astro-enhancers.md`.
- [x] QHYCCD device support: native camera, filter wheel (camera-cable + standalone serial QHYCFW3), and QFOC focuser (Standard + High Precision) drivers. JSON-over-serial protocol for QFOC with typed records and AOT-safe `QfocJsonContext`. Three-phase discovery in `QHYDeviceSource`: cameras → serial probe → camera-cable CFW check
- [x] Weather overlay in planner: hourly forecast from Open-Meteo (free, no API key) with layered color emoji (rain/snow/thunder/fog/cloud/sun/moon), file-cached with 1h TTL + offline fallback. Weather as full device type (IWeatherDriver) with equipment/profile integration
- [x] Planner: show Moon phase + position; altitude curve on the chart with phase emoji (hemisphere-aware). Uses Meeus lunar ephemeris via VSOP87a pipeline
- [x] Moon penalty in target scoring: penalise targets within ~30° of a bright Moon (illumination × proximity factor). Compute angular separation per target in ObservationScheduler.ScoreTarget. **Shipped** (branch `feat/moon-avoidance`): per-bin `MoonGrid` (illumination × quadratic proximity, Moon-below-horizon gate); radius is an optional param (default 30, ON) on Schedule/TonightsBest/ScoreTarget. See `docs/plans/moon-avoidance.md`
- [ ] Live viewer: camera switching; allow selecting which OTA's camera to preview in both GUI MiniViewer and TUI Sixel preview (currently always shows first available). PARTIAL (verified 2026-06-02): GUI DONE (`MiniViewerState.SelectedCameraIndex` + `#1`/`#2` toolbar toggles, `LiveSessionTab.cs:373`); TUI Sixel preview still always takes first available (`TuiLiveSessionTab.cs:644`).
- [x] Guider graph: connect dots with lines (Bresenham or anti-aliased) instead of scatter dots; users expect smooth curves like PHD2
- [x] Guider graph: scrolling window (last N samples) with dynamic Y scale and grid lines at integer arcsec
- [x] Guider graph: reuse the existing LiveSessionTab guide graph widget; the guider tab should show a larger version of the same graph, not a separate implementation. Extract shared graph rendering
- [x] DIR.Lib: add `FillEllipse`/`FillCircle`/`DrawEllipse`/`DrawCircle`/`DrawLine` primitives to `PixelWidgetBase`; `DrawLine` and `DrawEllipse` on abstract `Renderer` with CPU-optimized overrides on `RgbaImageRenderer` (midpoint ellipse, scanline quad, Span.Fill); GPU-optimized overrides on `VkRenderer` (rotated quad via FlatPipeline, ring shader via EllipsePipeline). Benchmarks in `DIR.Lib.Benchmarks`
- [x] Guider graph: show applied correction pulses (RA/Dec duration bars) alongside error; log-scaled bars (blue RA / orange Dec) extending up/down from zero line
- [ ] SyntheticStarFieldRenderer: refactor 20-parameter methods into records/structs
- [ ] Sky map: GPU text labels; move constellation names, planet labels, and overlay labels into the GPU sky-map pipeline (glyph atlas + instanced quads, like Stellarium). Currently all text is CPU-drawn via `PixelWidgetBase.DrawText`. The 1-frame desync during fast pans was fixed by per-swapchain-image UBO (commit ee38783), but full GPU text would eliminate the CPU/GPU render-pass split entirely and enable projected text that follows the stereographic distortion.
- [ ] Sky map: `[R]`efraction grid; toggle a second coordinate grid drawn in JNow + refraction-corrected (apparent) coordinates on top of the existing J2000 grid. Shows where objects actually appear from the observer's current site right now vs. the catalog J2000 positions. Full `Transform.SetJ2000 → RAApparent/DECApparent` (refraction on, site pressure/temperature from profile) for each grid line, tessellated like `BuildGridBuffers`. Near-zenith shift is ~0.35° precession alone; near the horizon the refraction bend stacks on top, reaching ~0.6° at 0° altitude. Makes the mount reticle's J2000 offset intuitive; the JNow grid passes through the reticle by construction for a topocentric-reporting mount.
- [x] Sky map: Stellarium-style time adjuster; step the observation instant relative to now (e.g. press `+1h` / `+1d` and it becomes Thursday 23:04 etc.), not a pick-a-date. Stores an offset from wall clock (minutes, hours, days, weeks) so the user can scrub forward and back. **Shipped** (branch `feat/top-5-todo`): `SkyMapState.TimeOffset` stacks on the base instant in the single `viewingTime` derivation in `SkyMapTab.Render`, so it drives everything downstream automatically:
    - sky color (feeds `SkyMapState.GetSunAltitudeDegCached` with the adjusted instant)
    - LST so stars / crosshair / horizon rotate correctly
    - planet positions via `VSOP87a.Reduce`
    - horizon fill and below-horizon label dimming
  Keys (in `SkyMapTab.HandleKey`): Up/Down = +-1h (Shift = +-10m), Left/Right = -+1d, PageUp/PageDown = +-1w, `N` = jump to the current night's midnight, `0` = reset offset, `T` = full reset (clears planner date too). The arrows now step the sky-map-scoped `TimeOffset` instead of mutating `PlannerState.PlanningDate`, so scrubbing is purely visual and never triggers a planner recompute. HUD strip shows the scrubbed site-local instant in blue with a compact signed offset chip (`SkyMapState.FormatOffset`, e.g. `(+2d 03h)`); the global status-bar wall clock stays the live anchor. Verified live (inspector): Up x3 -> sky rotated 3h + `(+3h)` chip + no planner recompute; `N` from afternoon -> next-day midnight + night palette; `0` -> back to live grey `HH:mm:ss`.
- [x] Guider graph: show dither events (markers/shading); yellow dashed vertical lines at dither events, dim yellow settling shading
- [x] Guider tab: keep looping guide camera frames during centering/slewing; call `LoopAsync` when not guiding so the guide camera feed stays live. Currently the guide loop stops during centering and the tab shows "Waiting for guider"
- [x] Guider tab: show calibration frames; render guide camera during calibration phase with star position and profile. Remaining: star movement vectors, step count, and calibration progress overlay
- [x] Guider: adaptive image-ready polling; sleep until near the expected end of exposure (N − `ImageReadyPollInterval`), then poll every 10ms, and in the final ~10ms poll every 1ms. Avoids wasting CPU on long sleeps while minimising latency at exposure end. Applies to `BuiltInGuiderDriver.CaptureGuideFrameAsync` and any other image-ready poll loop. **DONE (2026-06-22):** shared `ICameraDriver.WaitForImageReadyAsync` extension (`CameraDriverExtensions.cs`) drives a pure `NextImageReadyPollDelay(remaining, leadMargin)` cadence; one long sleep to `leadMargin` (= `External.ImageReadyPollInterval`, 50ms) before predicted end, then 10ms coarse, then 1ms in the final ~10ms (and on overrun); always strictly positive (no busy-spin). Routed `BuiltInGuiderDriver.CaptureGuideFrameAsync` (no timeout, guide-loop token bounds it) AND `MainCameraCaptureSource` (polar-align, keeps its exposure+5s budget) through it. `NextImageReadyPollDelay` unit-tested across all regimes (13 cases); 31 guide-loop/coupling/polar integration tests green; 0-warning build. The session main capture loop is tick-scheduled (not a naive fixed-interval `GetImageReadyAsync` poll), so it's intentionally out of scope.
- [x] Fake camera: apply mount tracking drift as pixel offset to star positions; DONE (PR #15 + PR #19): guide cam self-resolves the coupled mount from `IDeviceHub`, snapshots J2000 pointing per exposure and renders the deviation as pixel offset (`MountDriftPixels`); ST-4 forwards to the coupled mount so corrections physically move the encoders; `GuiderCalibration` converges end-to-end
- [x] Guider tab: guide camera image + crosshair (done). Remaining: star close-up + 1D intensity profile
  - [x] Add to `IDeviceDependentGuider`: `Image? LastGuideFrame`, `(float,float)? GuideStarPosition`, `float? GuideStarSNR`, `float? GuideStarHFD`
  - [x] Surface on `ISession` via `LiveSessionState.PollSession`
  - [x] `BuiltInGuiderDriver`: expose from `GuideLoop`'s `GuiderCentroidTracker`
  - [x] `FakeGuider`: generate synthetic guide frames with star field
  - [x] GUI: guide camera Canvas + crosshair overlay + SNR + frame counter
  - [x] GUI: star profile panel with 1D H/V intensity cross-sections + Gaussian fits + FWHM
  - [ ] PHD2: no image (show placeholder), SNR/mass from event stream only. PARTIAL (verified 2026-06-02): placeholder DONE (`GuiderTabState.PlaceholderReason`); SNR/mass from PHD2 event stream still TODO (`OpenPHD2GuiderDriver` leaves `GuideStarSNR` null).
- [x] Live session: show dither state; guider header shows `[Settling 0.42px]` with live distance, `[Paused (Slewing)]` during slews, correction arrows `[Guiding →142ms ↑38ms]`
- [ ] Cooling graph: same scrolling window treatment
- [ ] VSOP87 vectorization: convert 43K lines of hardcoded `amplitude * Cos(phase + frequency * t)` into coefficient arrays, evaluate with `Vector256<double>` (AVX2). Process 4 terms per iteration. Requires source generator or one-time conversion of all planet files (EarthX/Y/Z, MarsX/Y/Z, etc.)
- [ ] CLI: `train-guide-model` command for offline epoch training of the neural guide model; connects to mount + guide camera, records guide data for N worm cycles, then runs `TrainEpoch` with real PE data as teacher signal. Produces a base `.ngm` model file for the optical train. Aimed at permanent setups where users can invest a one-time training session to get a high-quality starting model. The online trainer (`TrainOnBatch`) should eventually converge to the same quality; offline training just gets there faster by seeing many PE cycles upfront instead of learning incrementally
- [x] Equipment tab: fully data-driven profile panel; replace hardcoded `RenderProfileSlot` calls (mount, guider, guider cam/foc) with a declarative slot model that includes special sections (site editing, focal length input, device settings) as metadata. Goal: single loop over all slots with pluggable section renderers. **DONE (2026-06-17, branch `feature/layout`, commit `31dc4e3`):** `EquipmentContent.GetProfilePanelSections(ProfileData)` emits an ordered surface-neutral `PanelSection` list (header / slots / site / guide-FL / device-settings / telemetry / per-OTA loop with FW-gated filter table / Add-OTA); `EquipmentTab.RenderProfilePanel` now just walks the list and dispatches each section via `RenderSection`. Chose the pragmatic **section-driver** over a full single-`LayoutNode` tree (the panel is mostly interactive + variable-height, so the tree's static-arrange model added little for high cost, see [docs/plans/layout-engine.md](docs/plans/layout-engine.md) Phase 2D). The section list is surface-neutral, so a future TUI panel can consume it with its own dispatch.
- [ ] Equipment tab: generic per-device settings pane, each device type (camera, mount, guider, etc.) should declare configurable properties via URI query params (like BuiltInGuiderDevice), and the equipment tab renders them automatically. FakeDevice should carry PE amplitude, period, guide rate etc. as URI params so FakeCameraDriver initializes from them instead of hardcoded defaults
- [x] Store device secrets (API keys) in the OS credential store, not the profile URI; DONE (branch `feat/planner-weather-skymap`, commit `cd24b68`). The OpenWeatherMap `apiKey` used to live in `?apiKey=` on the device URI, so switching weather providers / re-discovery silently wiped it (replaced by the keyless discovered URI). New `ICredentialStore` keyed `{deviceId}/{settingKey}`: `WindowsCredentialStore` (Credential Manager via `LibraryImport`, visible in Control Panel) + `FileCredentialStore` (owner-only 0600 file fallback for Linux/macOS; libsecret/Keychain can drop in later behind the same interface). `OpenWeatherMapDevice`/`Driver` read from the store; masked `DeviceSettingDescriptor` edits route to the store (`AppSignalHandler.OnCommit`) and re-fetch weather; a leftover `?apiKey=` is ignored. Keyed per-device → shared across profiles (enter once). No migration (strip a stale `?apiKey=` by hand). **Follow-ups:** per-profile override (needs an active-profile-id provider at driver creation); equipment settings display reads the store so a stored masked value shows as set instead of `(empty)`; TUI parity check.
- [x] Fake camera: shift/change star field during slews. **DONE, and well past what the note asked.** `FakeCameraDriver` projects the field through the coupled mount's **true** pointing, not a fixed seed: the main camera stamps a per-exposure (true − believed) J2000 delta, the guide camera rides a live pointing snapshot at its own configurable sensor offset, and polar-misalignment drift, worm PE and guide pulses all move the projection centre (PE never appears in encoder reads, so it is visible only in the pixels, which is the point). The remaining determinism is the *seeing* draw, deliberately seeded so a coupled scenario replays identically. See the `FakeCameraDriver` header comments and the fake-misalignment-drift / fake-camera-mount-PE-sync work.
- [x] Fake camera: scale synthetic background noise with exposure duration in `SyntheticStarFieldRenderer`; long subs (≥60s) have unrealistically clean backgrounds, causing per-channel stretch to produce degenerate parameters. Real cameras accumulate sky glow + dark current + read noise over time. DONE (2026-06-02): sky background scales by exposure on all paths (`skyLevel = skyBackground * exposureSeconds`, `SyntheticStarFieldRenderer.cs:132,270,836`); star flux scales too, read noise stays fixed (correct).

- [x] Fake filter wheels should have pre-installed filters (realistic filter sets per device ID)
- [ ] Planner: disambiguate duplicate common names, when multiple catalog entries share the same display name (e.g. NGC 4038 and NGC 4039 both named "Antennae Galaxies"), append the catalog designation in brackets: "Antennae Galaxies (NGC 4038)"
- [x] Planner: full rescan when site coordinates change significantly (>1°) instead of fast-path recompute; currently changing lat from -37 to 50 keeps southern-hemisphere targets with 0° altitude. DONE (2026-06-02): `AppSignalHandler.cs:489-510` runs full `ComputeTonightsBestAsync` when |Δlat| or |Δlon| > 1°, else fast-path `RecomputeForDate`.
- [ ] Extract VkImageRenderer UI layout to Abstractions; toolbar, file list, status bar, hit testing are renderer-agnostic; image rendering + texture upload stay Vulkan-specific in Shared
- [ ] Viewer tab renders at (0,0) ignoring contentRect; refactor ImageRendererBase.Render to accept a contentRect like PlannerTab/SessionTab/EquipmentTab so it works correctly when embedded in the tabbed GUI
- [x] Pinned items in planner should persist to disk; auto-save/load via `PlannerPersistence` keyed by profile+date, stored under `{OutputFolder}/Planner/{profileId}/{date}.json`
- [ ] Seed focuser `MaxStep` from hardware during ZWO EAF discovery (same `seedQueryParams` pattern as EFW slot count)
- [ ] Remember last focus position in profile URI after auto-focus (save after every auto-focus attempt, whether successful or not) so the focuser can start near the last known good position on next session
- [x] HFD drift detection via linear regression over last N frames (NINA uses `AutofocusAfterHFRIncreaseTrigger` with configurable `SampleSize` and `Amount` threshold); more robust than single-frame ratio comparison, reduces false refocus triggers. **DONE (2026-07-03):** the inline regression extracted into pure `FocusDriftDetector.EstimateTrendHfd` with its filtered-fit bug fixed (divisor was the window length, not the included-sample count, every skipped low-star/non-comparable sample biased slope + intercept); `FocusDriftSampleSize` (window, default 30) + `FocusDriftMinSamples` (default 5) on `SessionConfiguration` (`FocusDriftThreshold` = the Amount analogue); history cleared on drift-triggered refocus + target change (refocus-oscillation guard); `CircularBuffer<T>` rewritten lock-free (ImmutableArray + CAS `Snapshot`, `Session.GuideSamples` render-thread poll is now a free reference read instead of a 300-item lock-and-copy per frame). Pinned by `FocusDriftDetectorTests` + `CircularBufferTests`.
- [ ] Use IWeatherDriver ambient temperature for camera warm-up, when no hardware weather station or external temp sensor (Pegasus Astro) provides heat sink temp, pass ambient temp from weather driver as a denormalised property to the camera driver (via Session orchestration, not direct driver-to-driver coupling). Use as ambient target for `CoolCamerasToAmbientAsync` ramp
- [ ] SafetyMonitor integration: ASCOM `ISafetyMonitor` driver polling (5s interval watchdog) that can interrupt imaging and stop tracking when unsafe. Gate on safety in dither, meridian flip, and centering triggers. Park scope on unsafe condition.
- [ ] Rotator device type (per-OTA field rotation): no `IRotatorDriver`/`DeviceType.Rotator` today (only WCS PA math). ASCOM `IRotatorV4` + Alpaca wrap (same pattern as `CoverCalibrator`); framing-angle automation + post-meridian-flip re-rotate; the rotator slots into each `Setup.Telescopes[i]`, not the mount. See [docs/todo/drivers.md](docs/todo/drivers.md).
- [ ] Dome device type + telescope slaving (per-site); `IDomeDriver` + ASCOM `IDomeV3`/Alpaca; an azimuth-follow loop driven by the single `Setup.Mount`; park on finalise. See [docs/todo/drivers.md](docs/todo/drivers.md).
- [ ] Alt-az SkyWatcher mount support, Phase 1 (correct GOTO + tracking + position); today `?alignment=AltAz` is **report-only** (refused; Phase 0 shipped PRs #47/#48, see [docs/plans/altaz-mount-support.md](docs/plans/altaz-mount-support.md)). Phase 1 makes an AZ-GTi-class mount actually point/track in alt-az for visual / EAA / plate-solve (no imaging): Az/Alt↔encoder-step transforms in `SkywatcherMountDriverBase` (home = az0/alt0), `IMountDriver.BeginSlewToTargetAsync` pier-side-gate bypass when alignment is alt-az (an `IMountDriver`-layer change → also unblocks ASCOM/Alpaca `algAltAz` mounts), dual-axis predictor tracking (vs single-axis sidereal `:I`), and Az/Alt→RA/Dec position reads. Phase 2 (alt-az guiding) and Phase 3 (long-exposure imaging) follow; **Phase 3 is blocked on field-rotation handling; needs the Rotator device type above (no derotator → no long alt-az subs).** See [docs/todo/drivers.md](docs/todo/drivers.md).
- [x] Flat-frame acquisition automation: **Phases 1-3 SHIPPED**. Phase 1 (panel/calibrator): pure `FlatExposureSolver` + `Session.TakeFlatsAsync` (per-OTA/filter; close cover → calibrator on → auto-expose → write `FrameType.Flat` → off), opt-in `TakeFlatsOnSessionEnd`. Phase 2 (twilight sky-flats, dawn + dusk): pure `SkyFlatExposureSolver` (re-metered per frame, Capture/Adjust/Wait/Stop) + `Session.TakeSkyFlatsAsync`, opens covers, solar-altitude window gate (`VSOP87a`), anti-solar zenith slew (`BeginSlewToZenithAsync`, tracking off so stars average out), `FlatSource` dispatch at the end-of-session hook (dawn) + a new session-start hook (dusk, cooled first; cloud-insurance for a fogged dawn). Phase 3 (on-demand + manual panel): `ISession.RunFlatsOnlyAsync` (connect-only-flat-devices → cool → capture → finalise, no wait-for-dark/focus/guider) behind CLI `tianwen flats` + `POST /api/v1/session/flats` (shared `FlatRunParsing`). A manual hand-switched panel is a **device** (`ManualCoverDevice`/`ManualCoverDriver`, a degenerate `ICoverDriver` mirroring the manual filter wheel: cover `NotPresent`, calibrator `Ready`-on-demand) assigned to the OTA cover slot and captured through the **same** calibrator path; no `ManualPanel` source, no session branching; registered via `AddDeviceType` so it round-trips through `TryGetDeviceFromUri`. Frames land under `Flats/<date>/<filter>/Flat/`; `MasterFrameBuilder` consumes by FITS headers. 37 tests. **Deferred:** a GUI `LiveSessionMode.Flats` mode on the Live Session tab (like PolarAlign/Planetary; assign 💡 Manual Light Panel + source dropdown + interactive prompt). See [docs/plans/flat-frame-automation.md](docs/plans/flat-frame-automation.md).
- [x] TUI Sixel preview in live session tab: **DONE** (verified 2026-07-29): `TuiLiveSessionTab.RenderPreview` watches `LiveState.LastCapturedImages`, adopts a new frame via async `AstroImageDocument.AdoptImageAsync` on a background `Task` polled by the render loop (the lock-free Task-handoff pattern, the ownership-transfer semantics the item warned about are honoured, the tab never touches the `Image` after adoption), and the Sixel raster draws from `PaintHost` at the arranged pixel size with a text fallback on non-Sixel terminals. With Console.Lib 4.8's buffered rendering the blit also declares its cell region (`BeginRawOutput`/`MarkRawRegion`), so the diff breaks around the picture.
- [ ] `TuiCellRenderer<CellBuffer>` for live position view -- **LARGELY SUPERSEDED** (2026-07-29) by the `CellLayout` path: `Layout.Node` trees now render natively on the terminal (`CellLayout.Paint` + `CellMeasureContext.PixelAuthored`, corner glyphs, draw==hit via `CellLayout.HitTest`, mouse mapped), which is how the TUI home board shares the GPU tab's tree -- no `Renderer<TSurface>` needed for anything the layout DSL expresses. What remains is the **raster-drawing residue only**: widgets that call `Renderer` primitives directly (charts, sky map) would still need a cell-grid `Renderer<TSurface>` (block shading, midpoint-ellipse glyphs) to escape Sixel; today they fall back to text or require Sixel. **The second residue category -- `ScrollableList` rows as formatted strings -- is CLOSED (2026-07-30, Console.Lib 4.10):** `IRowFormatter` and `ITreeNode.FormatNodeContent` are gone, replaced by `IRowLayout.BuildRow(in RowContext)` / `ITreeNode.BuildNodeContent` returning a `Layout.Node`, so a row's inline buttons are clickable NODES resolved through `ScrollableList.DispatchRowHit` against the rect that was painted. That retired the hand-derived click columns (`EquipmentFieldItem.DeleteActionColumns`, `InfoRowItem.ButtonRegion`), the SGR-byte-counting pad compensation (`VisibleOverhead`/`StyleSegment`), and the reason the OTA `[X]` could not be right-anchored -- it now is, where the GUI's `[Remove]` sits. Ported across four repos (tianwen, Console.Lib, chess, LALR.CC) in one cut. Re-scope before picking up: implement `Renderer<TSurface>` over a terminal cell grid so the live-session forms widgets (target name, alt/az, tracking state, guider status, dither/settling indicators) render natively over SSH without Sixel. Box-drawing chars (`┌─┐│└─┘`) for frames, block shading (`█▓▒░`) for fills/bars, midpoint ellipse -> perimeter glyphs for circles, truecolor or 256-cube depending on `$TERM`. Map xterm mouse + kitty keyboard -> existing `InputEvent`. Sky map degrades to a non-spatial summary (current target, alt/az, next slew, visible-object count) driven by the same SignalBus data -- Sixel/kitty graphics protocol stays as the optional "I really want pixels" mode for terminals that support it. Unlocks headless scope-host operation without leaving the SignalBus + widget-tree abstractions.
- [ ] SDL window icon for non-Windows: `<ApplicationIcon>` only embeds in the PE for Windows. On Linux/macOS, need `SDL.SetWindowIcon` with a surface loaded via `SDL_image.IMG_Load` (requires adding SDL3_image package) or `SDL.LoadBMP` (requires BMP conversion). Also set `.desktop` file icon on Linux.
- [ ] **Hosted polar alignment**: `PolarAlignmentSession` runs outside `Session.RunAsync` and is GUI-driven only today, so a remote rig cannot be polar-aligned. Needs a lifecycle surface on `IHostedSession` (start/abort/state) + a phase/solve-result DTO; the imagery is free (the P2 preview endpoint already carries frames, reticle/rings are client-side). **Settle first:** whether the node grows a *run kind* (session / flats / polar-align) or a parallel endpoint group; lean run kind, since the P0 device lease already models "exactly one run owns the rig" and a second notion of "running" is the mistake P0 just undid. Likely-next (user, 2026-07-27). See [docs/plans/remote-profile.md](docs/plans/remote-profile.md) § Deferred.
- [x] **Guide-cam image stream over the hosted API** (**SHIPPED 2026-08-05**); `GET /api/v1/preview/guider` serves the live guide frame through the same `PreviewEncoder` and the same `X-Frame-Number` contract as the per-OTA previews, and `RemoteSessionMirror` fills `LastGuideFrame` / `GuideStarPosition` / `GuideStarSNR`, so the Guider tab renders a remote rig through the code that renders a local one. Its own route rather than an OTA index: one guider serves the whole rig, its frames arrive at guiding cadence rather than per sub, and it is wanted precisely while the science cameras are mid-exposure with nothing new to show. **The recorded blocker was already stale**; `ISessionTelemetry.LastGuideFrame` existed and `Session` already forwarded it. **The real hazard was sharper, and its primitive was unsound:** `GuideLoop` does `LastFrame?.Release(); LastFrame = frame;` every exposure, so a request encoding a JPEG across an await reads a buffer the camera has taken back (a valid JPEG of a flat grey rectangle, hence `GuidePreviewTests` modelling recycling as a clobber and asserting the star survives). `ChannelBuffer.AddRef` could not be used for it: it checked liveness and then incremented as two steps, so a borrower could resurrect a released buffer. Now `TryAddRef` (CAS, never resurrects) + `Image.TryLease` (all-or-nothing over the planes, distinct instance with its own one-shot `Release`, `false` when the race is lost). The change token also needed a **new** counter: `_guideFrameCount` counts frames the loop *corrected on* and sits past the star-lost `continue`, so it freezes during an outage while the camera keeps publishing; exactly when an operator wants to look. Both drivers funnel every publish site through one setter so the increment cannot be forgotten. Guide frames are a **separate opt-in** (`PreviewOptions.IncludeGuider`, default off) from the OTA thumbnails: the home dashboard shows science previews and never a guide frame. **Deferred:** the star-profile arrays + calibration overlay stay local-only; a per-poll array pair for an often-invisible panel, and it cannot be derived client-side (cross-sections from a stretched lossy preview give a confidently wrong FWHM rather than none), so it wants its own opt-in fetch like the frame got. See [docs/plans/remote-profile.md](docs/plans/remote-profile.md) § Deferred.
- [x] **Multi-rig dashboard / home screen** (**SHIPPED 2026-07-28**); `GuiTab.Home` is the landing tab (house icon, Ctrl+H, first in `TabOrder`), with one card per rig: local node plus every bound remote one, title = the rig and subtitle = the profile it runs. `HomeBoard.BuildCards` is the pure projection (unit-pinned) and `HomeTab<TSurface>` renders a per-frame snapshot published on `GuiAppState.HomeCards`; the tab never reaches into `RemoteRigRegistry`, mirroring how bound rigs already reach the equipment picker. All four invariants hold as designed: previews stay off, the board is read-only w.r.t. hardware (a card click posts the same `SelectRemoteRigSignal`/`SelectLocalContextSignal` the picker does), cards are built in the PRE-gate part of `PollPreviewTelemetry` and the board is **not** added to its `ActiveTab` gate, and the card section is content-sized (`WrapH` + trailing `Spacer`). Three prerequisites turned out to be missing and shipped with it: **`SessionPromptEventArgs.RaisedUtc` / `PendingPromptDto.RaisedUtc`** so the prompt badge ages from the node's own instant rather than from when a client noticed (an unknown age stays unknown, never filled in); **`GET /api/v1/session/profile`** so a node can report which profile it runs at all (`ActiveProfileId` had no way out of the node, and `/profiles` lists what exists without saying which is live), cached per connection and refreshed every 2 min; and **per-mirror poll backoff** (doubling to a 30 s cap, derived from a consecutive-failure count so one answer resets it, and a 404 counts as an answer). **Follow-ups shipped 2026-07-29** (branch `home-screen-dashboard`, PR #120): the card grew per-target progress (`target 2/3 · frame 23/100`, via `ScheduledObservation.PlannedFrameCount` on the wire so a mirror answers identically to a local session), cooling (worst camera from setpoint, freshness-gated), median HFD, guide RMS, last notification, and a meridian-flip countdown crossing as an INSTANT (`ISessionTelemetry.MeridianFlipUtc`); the board picks its shape (`Auto | Cards | Table` header selector: Auto swaps to a one-row-per-rig table when the cards' actual height doesn't fit, and the header says why); and the **TUI home board landed** (`TuiHomeTab` renders the *same* `HomeBoardLayout` tree via `CellMeasureContext.PixelAuthored`; the first tree genuinely shared across surface kinds, made livable by Console.Lib 4.8's diffing cell buffer: one cell per clock tick). **Next:** multi-night progress beside the cards, now its own plan, [docs/plans/multi-night-progress.md](docs/plans/multi-night-progress.md). See [docs/plans/remote-profile.md](docs/plans/remote-profile.md) § Deferred for the design record.

## More (full backlog by area)

The bulk of the backlog, the done-archive, and the unsorted inbox live under `docs/todo/`:

- [Sequencing & Polar Alignment](docs/todo/sequencing.md)
- [Devices & Drivers](docs/todo/drivers.md)
- [Imaging, Stretch & Colour](docs/todo/imaging.md)
- [Astrometry](docs/todo/astrometry.md)
- [UI & Rendering](docs/todo/ui.md)
- [Guider](docs/todo/guider.md)
- [Infrastructure, Quality & Testing](docs/todo/infra.md)
- [Inbox (unsorted Slack self-notes)](docs/todo/inbox.md): swept through **2026-08-02**; re-read the DM only back to that watermark, and check `imaging.md` too (one earlier pass filed notes straight there)

Root-cause notes for limitations/bugs: [docs/known-limitations.md](docs/known-limitations.md).
