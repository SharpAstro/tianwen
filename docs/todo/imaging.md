# TODO -- Imaging, Stretch & Colour

Part of the TianWen TODO set. See [TODO.md](../../TODO.md) for the index and the active/high-priority list.

## Calibration + integration gaps vs Siril / APP / PixInsight WBPP

Filed 2026-08-03 from a stage-by-stage comparison of `StackingPipeline` against the three tools,
prompted by finding that the master flat was never calibrated. Ordered by value per unit of work.
The flat gap itself is **fixed** (see [known-limitations](../known-limitations.md)); these are what
the comparison turned up alongside it.

- [ ] **Gate or weight subs on their own guiding statistics, now that they carry them.** Lights written
  since 2026-08-29 carry `GUIDERMS` / `GUIRMSRA` / `GUIRMSDE` / `GUIDEPK` / `GUIDEN` in arcsec, measured
  over each frame's OWN exposure window (`ImageMeta.Guiding`, `GuideStatistics.OverExposure`), so the
  stacker can reject or down-weight the smeared ones from the header alone -- no pixel read, and it
  composes with the per-frame weighting item below. **Prefer `GUIDEPK` over `GUIDERMS` for the reject
  decision**: a trailed sub is usually one gust against an otherwise clean exposure, which is precisely
  what an RMS averages away, and the two cards exist separately for that reason. Two gotchas before
  anyone wires it: **an absent card means UNKNOWN, never good** (an unguided rig writes no cards at all,
  by design, so a missing-is-fine default silently exempts every frame it should be judging), and
  **`GUIDEN` bounds how much the number is worth** -- a two-sample RMS from a short sub or a truncated
  ring buffer should not gate anything. Nothing consumes these yet.
- [ ] **Per-frame weighting in the combine.** We gate binary (`FrameQualityFilter` keeps or rejects)
  and then combine with an unweighted mean. Siril weights by wFWHM / star count / noise, PixInsight
  by `SubframeSelector` output, APP by its own quality measure. This is free SNR on any session with
  variable transparency, and **the seam already exists**: `MeanCombiner.Combine` computes
  `sum += v * k; cnt += k` over the keepMask, so a keepMask entry carrying the frame's weight
  instead of 1.0 is a correct weighted mean with no change to the math. The work is threading a
  per-frame weight from `FrameMetrics` (which already carries median HFD, FWHM, ellipticity and star
  count) down through the strategies to where the mask is built. Cheapest real win on this list.
- [ ] **Local normalisation.** `Normalizer` applies one global per-channel affine
  (`(x - min) * target/median`). A gradient that changes SHAPE between subs (rising moon, drifting
  light pollution) cannot be followed by a single scalar pair. PixInsight has `LocalNormalization`,
  APP has LNC to degree 4. This is also the blocker for mosaics: APP pairs LNC with multi-band
  blending to kill panel seams, and we have no answer for that at all, which matters because the
  Vela project is a 20-panel mosaic.
- [ ] **Cosmetic correction is narrower than it looks.** `BadPixelDetection.BuildMaskFromDark` builds
  a good iterative-MAD hot-pixel mask, but only the drizzle strategies consume
  `IntegrationJob.BadPixelMask`; everything else leans on sigma-clip across frames, which does
  nothing for a 10-frame group. Siril (`-cc=dark`) and PI (`CosmeticCorrection`) apply it per frame
  BEFORE registration, which also cleans up star detection. We also have no cold-pixel path.
- [ ] **Registration has no distortion model.** Star-quad match solves an affine. PI offers thin-plate
  splines and APP several projective models. Matters most on wide fields, which is what the Samyang
  135 f/2 shoots, and for mosaic panel joins.
- [ ] **Dark optimisation is deliberately absent; keep it that way or do it properly.** Siril `-opt`
  and PI both least-squares-scale a mismatched dark, which requires a bias-subtracted dark that we
  do not build. We instead refuse anything outside 0.5x to 2.0x exposure. That strictness is load
  bearing: it is what keeps the 2,220 mislabelled dark-flats from ever being selected as a light's
  dark. Anyone implementing scaling needs to read that note first.
- [ ] **Prefer an exposure-matched dark-flat over bias as the flat pedestal.** Worth single-digit ADU
  (784 vs 788 measured), so this is a refinement, not a fix. Blocked on the `IMAGETYP` labelling
  above being resolvable without guessing from exposure.

## Archive filter inference (committing a validated method)

Filed 2026-08-04. The method is validated on 48 sessions / 7,161 frames and the *write* end is
committed (`dataset tag-filter` + `FitsHeaderEditor`), but every measurement lives in scratch scripts
plus a `_provenance` folder on `D:`, so a fresh checkout re-derives nothing. Full method, the
reference bias table, the resolution limits and four recorded negative results:
[docs/plans/filter-inference.md](../plans/filter-inference.md).

- [ ] **F1 `dataset bias-library`.** Group `IMAGETYP=BIAS` on `(camera, gain, offset, temperature)`
  and emit **per-channel** medians plus n. Per-channel is not optional: one era ran with ZWO white
  balance on, so its bias is `649/516/649` rather than grey, and the pipeline undoes WB from the bias
  frame's own channel ratios. Everything downstream keys on this artifact.
- [ ] **F2 frame-scale detection.** GCD of pixel values tells N.I.N.A.'s times-four 14-to-16-bit
  recording scale from an unscaled writer. Today this is an assumption in a comment, and it is why
  every SharpCap session was set aside wholesale. Must be per frame and reported, never inferred
  from `SWCREATE`.
- [ ] **F3 `dataset measure-filter`.** Per session, sample frames from the middle of the run, resolve
  bias from F1, and emit sky rate (e-/px/s / airmass) plus background B/G. Read-only.
- [ ] **F4 band derivation.** Match F3 rows against a committed reference band table and propose a
  `FILTER` per session with its basis and deviation. Keep it a **proposal a human locks in**: B/G
  cleanly separates 3 nm from quad-band (43 sd) but cannot separate two dual-bands from each other.
- [ ] **F5 `archive organize`.** The copy-to-a-new-root tool: verify both sides, never write to the
  source, collapse hard-linked frames to one copy, file calibration by what it is. Proven once at
  5,750 files / 96.95 GiB with 0 failures; needs to become code. Two layout rules it must keep:
  flats belong under **their own** `DATE-OBS` (filing them under the session date left 10 of 18
  session dates with no flats folder), and calibration folders must key on **temperature** (without
  it a bias folder merged two sets seven months and 5 C apart, and daylight dark-flats at +22 C hid
  inside `DARK`).
- [ ] **Groups C and beyond are unmeasured**: 16 SV605CC + SH61 EDPH sessions (same IMX533, so the
  bands transfer, but they need their own bias frames), the 18,354 frames with `TELESCOP='?'`, and
  the Newtonian, which appears exactly once as `SWQ8`. Askar D1/D2 and IDAS D3 have **zero** textual
  presence anywhere, so only pixels can place them.
- [ ] **Settle the times-four claim for the 12-bit ASI585.** CLAUDE.md's "the vendor SDK does not
  left-shift" was established on the 14-bit ASI533. A 16-ADU comb is measurable (53% of values on an
  exact 16-ADU grid against 6.25% expected flat), which fits the SDK left-shifting RAW16 for a 12-bit
  sensor. That is inference, so it wants a live capture rather than a doc edit.

### Do NOT discard the SharpCap era on the focus assumption (measured 2026-08-04)

The working assumption was that only N.I.N.A. sessions are worth keeping, because SharpCap will not
refocus on temperature drift without hassle. **The first half is true and the conclusion does not
follow.** Measured with `tianwen image stats` on the one rig that spans both programs (ASI533MC Pro
at FL 130, the Samyang 135, so identical 5.97 arcsec/px), 23 distinct sessions, `FOCUSPOS` and
`FOCTEMP` read from every frame:

| | focuser moved mid-run | HFD drift start to end | mid-session HFD |
|---|---|---|---|
| N.I.N.A. (12) | **11 of 12** | -3.2% to +3.4%, median **-0.1%** | median **2.45 px** |
| SharpCap (11) | **0 of 11** | -8.3% to +10.5%, median **+6.2%** | median **2.71 px** |

- **The mechanism is confirmed by direct evidence, not inference.** Zero SharpCap sessions moved the
  focuser, across every run, while `FOCTEMP` fell 0.7 to 3.8 C. That is the focuser's own reported
  position, so it needs no argument from image quality.
- **But the quality distributions overlap, so a blanket discard is wrong.** The best SharpCap session
  (2024-07-06 Rim Nebula SII, 2.42 px, drift -0.3%) is **sharper than 9 of the 12 N.I.N.A. sessions**.
  Discarding by program label would throw away frames better than most of what is already kept.
- **There is a free triage signal.** SharpCap sessions split cleanly on thermal drift: all 4 with
  `|FOCTEMP delta| <= 1.1 C` held focus, and 6 of the 7 above 1.4 C degraded by 6 to 10%. That is
  readable **from headers alone**, with no pixel reads, so all 45,134 SharpCap frames can be triaged
  cheaply before any measurement. The categorical split is much stronger than the linear fit
  (Pearson r is only +0.31, dragged down by the two anomalies below), so use a threshold, not a slope.
- **A N.I.N.A. session fails the same way.** `HIP 80609` 2026-04-21 is the **softest** session in the
  whole sample (2.89 px) and its focuser never moved. Judge per session, never per program.
- **Two SharpCap sessions improved through the night** (-8.3% and -7.8%). Both have healthy star
  counts (1,316 and 1,005), so this is real and not noise; a manual mid-session refocus would explain
  it and the data cannot distinguish that.

**Two limits on how far this generalises.** Star counts were 791 to 1,710 everywhere, so no session
was noise-limited, but (1) at 5.97 arcsec/px an HFD of 2.4 px is 14.3 arcsec and the rig is heavily
undersampled, so HFD is partly floor-limited and the differences above **understate** the true focus
error; and (2) this covers the ASI533-at-FL130 years only. The 2021 to 2022 era was **not** tested,
because 511 of its directories carry no `IMAGETYP` card at all and were skipped by the session gate.

- [ ] **Triage the SharpCap era on `FOCTEMP` delta + measured HFD** rather than discarding it, and
  give the 511 no-`IMAGETYP` directories a gate that does not depend on that card.

## Archive + FITS interop backlog (recovered 2026-08-20)

Filed from the `feat/ai-enhancements` handover. **Provenance matters here:** these were tracked as a
numbered task list that lived only inside a chat session, so the numbers referenced nothing durable
and are dropped; the old number is given once per entry only so the handover history stays greppable.
Several entries carry a DECISION already made, which is the part that was actually at risk.

- [ ] **QHY294 gain-1600 dark library.** The only real coverage hole in the 2025-2026 archive.
  Capture spec (read off the stranded sessions' own lights, so it is a spec and not a guess) in
  [../plans/astro-archive-survey.md](../plans/astro-archive-survey.md) section 10.1. Shooting it
  un-drops **193 lights across 3 targets**. (was #10)
- [ ] **Bake the older archive years, behind CALSTAT / FLIPSTAT read guards.** (was #11) Two decided
  points:
  - **`CALSTAT` is a read guard, and it is no longer theoretical.** Its letters (B/D/F) accumulate
    per applied correction, so a light that already reads `CALSTAT` must not be calibrated again.
    `C:\temp\test-data` is entirely pre-calibrated (`CALSTAT='BDF'`) and a stack run would happily
    re-calibrate it if matching darks were present.
  - **`FLIPSTAT` is raster-transform provenance, NOT pier side** (the raster was flipped or rotated
    at recording). The nom-tam-fits javadoc gloss conflates the two, and an earlier reading of this
    repo's own notes made the same mistake. Support it for READING only; never write it.
- [ ] **Write `CALSTAT` + `DARKTIME` on our own calibration outputs.** (was #39) Partially superseded
  by the shipped `SWCREATE` + `DATE-BEG`/`DATE-END` master provenance, so check what is already
  written before starting.
- [ ] **Mono narrowband, end to end.** (was #37) The M42 Ha + OIII set under `C:\temp\test-data` is
  the real fixture and M33 LRGB exercises mono broadband; both parse correctly now that the SBFITSEXT
  `IMAGETYP` spellings land (they used to read as `FrameType.None`, i.e. invisible).
- [ ] **`TILEXY` + panel identity for mosaics.** (was #50) **The convention is decided, and this is
  the only place it is written down:** MaxIm publishes no orientation convention (theirs is
  scan-order dependent), so `TILEXY` is a **grouping key only** and all geometry stays on
  `OBJCTRA`/`OBJCTDEC` plus the solved WCS. Ours is **X grows with RA, Y grows with Dec, (0,0) at the
  min-RA / min-Dec corner** -- chosen for self-consistency, not because anyone else does it that way.
  N.I.N.A. names mosaic panels through `OBJECT` as "Target Panel N". Existing files can be recovered
  retroactively from `OBJCTRA`/`OBJCTDEC` + a solved WCS, so this is not capture-time-only.
- [ ] **Panel level in the organized archive.** (was #38)
- [ ] **Re-organise `D:\Astro-Organized` where several sessions share one folder.** (was #49) Scope
  measured in [../plans/astro-archive-survey.md](../plans/astro-archive-survey.md) section 10.2:
  **1,073 lights with no organized counterpart**, every one from a non-ASI533 / non-SV605CC camera.
- [ ] **Filter identity for the dual-band sessions.** Either sidecars (`.tianwen-meta.json`) or a
  `dataset tag-filter` verb. The coverage census makes the gap explicit: `filter_source=none` on all
  60 sessions, because **no session in the source archive carries a `FILTER` header at all**. Related:
  the archive filter-inference section above, and [../plans/filter-inference.md](../plans/filter-inference.md).
- [ ] **ML gradient fields.** (was #15) Belongs to
  [../plans/ai-denoise-deconv.md](../plans/ai-denoise-deconv.md) P5 rather than here; listed so the
  handover's backlog is fully accounted for.
- [ ] Optional: run `dataset coverage` over `D:\Astro-Organized` too, for a filter-aware report.

Interop facts hit on real data during that review, kept because they are not written down anywhere
else and each cost time to establish:

- **`PEDESTAL = -100` means +100 ADU was re-added after calibration** (MaxIm's sign convention), so
  the number is not a value to subtract as it reads.
- **A CFA pattern is measurable from the pixels alone**, which is how `BAYERPAT='VALID'` was resolved
  without trusting any header: the two green subplanes' medians agree to ~0.2% (that names the green
  diagonal), and of the two remaining subplanes the brighter one is red on any real sky.

Closed from the same review, recorded so nobody re-litigates them: `SNAPSHOT` (the SBFITSEXT twin of
`STACK_N`) and `SWMODIFY` (modifying-software card) were adopted and shipped; `READOUTM` folded into
the calibration temporal/tolerance work and shipped with it.

## Imaging

- [x] **DONE 2026-08-21. Document-open traversal cost, and a correction to how it was first
  reported.** The original entry here quoted `Statistics(c)` x3 = 1,028-1,195 ms and called it the
  dominant cost. **Those were DEBUG numbers.** `dotnet test` defaults to Debug, and this library's
  inner loops are ~7x slower there, so the figure described the test configuration rather than the
  product. Re-measured in Release on the same 6000x4000x3 probe (planes pre-touched so page faults
  are not charged to the first stage), the ranking inverts:

  | stage | Debug (as filed) | Release before | Release after |
  |---|---|---|---|
  | `Statistics(c)` x3 | 1,028-1,195 ms | 130-177 ms | 120-150 ms |
  | `GetStarMaskedMedianAndMADScaledToUnit(c)` x3 | 557-755 ms | 412-632 ms | **23-70 ms** |

  So the real hot spot was the star-masked median, not the histogram, and its cause was algorithmic
  rather than micro: **two full `Array.Sort` calls over ~1.5 M samples per channel to extract two
  medians.** A median needs selection, not a sort. Fixed by `StatisticsHelper.NthSmallest` (a public
  wrapper over the private `QuickSelect` that was already there), reusing one buffer for both
  passes. `Normalizer.MedianViaQuickSelect` had already made exactly this trade for the stacking hot
  path -- and `Image.Histogram.cs` already had `using static StatisticsHelper` while still sorting.

  The histogram loop was improved too, 50 -> 32 ms per 24 MP channel, by two changes that a code
  reading would rank the wrong way round: a flat span instead of `float[,] [h, w]` indexing (50 ->
  36) and a float-domain clamp instead of `Math.Clamp`, whose `(float, int, uint)` call binds the
  **double** overload and so ran float -> double -> clamp -> double -> int per pixel (36 -> 32).
  Every change is bit-identical, pinned by `HistogramSelectionParityTests` (which reimplements the
  pre-change algorithm as an oracle) plus `NthSmallestTests`.

  Two lessons worth keeping: **quote the configuration with any timing**, and note that the parity
  test could NOT distinguish `MedianFast` from the upper median on any real fixture (swapping it in
  left every assertion green), because a quantised background ties the two middle samples. That
  convention is pinned on synthetic distinct values instead.
- [x] **DONE 2026-08-21. Time AND allocation for the stats paths, in BenchmarkDotNet:
  `StatsPathBenchmarks`.** Run with
  `dotnet run -c Release --project TianWen.UI.Benchmarks -- --filter '*StatsPath*'`. Each change
  sits against the implementation it replaced as an explicit `[Benchmark(Baseline = true)]`, so the
  Ratio and Alloc Ratio columns are computed rather than asserted. At Size=3008 (an ASI2600 sub,
  and the size at which these buffers cross into the LOH):

  | path | before | after | ratio | alloc before | alloc after | alloc ratio |
  |---|---|---|---|---|---|---|
  | star-masked median+MAD (x3 ch) | 136.0 ms | 25.6 ms | **0.19** | 13,141 KB | 6,645 KB | 0.51 |
  | `Normalizer.ComputeStats` whole | 79.4 ms | 75.3 ms | 0.95 | 28,089 KB | 28,089 KB | **1.00** |
  | `Normalizer.ComputeStats` box | 77.3 ms | 72.4 ms | 0.94 | 18,727 KB | 14,046 KB | 0.75 |
  | histogram bin buffer | 154.8 us | 7.0 us | **0.05** | 512 KB | 256 KB | 0.50 |

  **Read the two Normalizer rows with the error bars in view**: at `[ShortRunJob]` those means
  carry a 99.9% CI half-width of 5-35 ms, so a 0.94-0.95 ratio is not distinguishable from 1.0.
  The Normalizer change is justified by the duplication it removed and by its EXACT allocation
  numbers (MemoryDiagnoser counts, it does not sample), not by its timing. The star-masked and
  histogram rows are far outside the noise.

  **This replaced two hand-rolled probes, and getting the tool right corrected them.** They timed
  "best of 3" around `GC.GetTotalAllocatedBytes` after a forced collection, and reported the box
  path as **19.20 -> 0.00 MB/call**. That zero was an artefact of measuring five reps with a
  perfectly warm pool; over BDN's steady state the honest figure is 0.75. The probes also each
  carried a private copy of the pre-change implementations, which is how one wrong label ("2
  rents" on a path that had one) came to be written twice. They are deleted; the baselines live
  once, in the benchmark.

  What survived from the probe work: **the whole-image `Normalizer` path allocates exactly what it
  did** (alloc ratio 1.00 -- the old one had ONE rent, the min pass rented nothing), and
  `ArrayPool<float>.Shared` **does** pool a 34 MB array, so the guess that it capped near 1 MB was
  wrong. And the benchmark found a cost nobody had suspected: `Image.Histogram` built its bins in
  an `ImmutableArray<uint>.Builder`, paying for the builder's backing array AND a second one
  because `ToImmutableArray()` on a Builder copies. Now a plain `uint[]` wrapped by
  `ImmutableCollectionsMarshal.AsImmutableArray`: half the memory, **20x** less time, the 64-call
  zero-fill loop deleted (`new uint[]` is already zeroed), and bit-identical bins. A document open
  makes 10-12 of those calls.

  `MemoryDiagnoser` also reports Gen0/1/2 collections per 1000 ops, which the probes could not
  produce at all (five reps never triggered a GC): the histogram buffer goes 14.77 -> 7.20
  collections per 1000 ops at Size=1280.
- [ ] **`Image.Histogram` still has one measured lever left: parallel row bands, 32 -> 8 ms per
  24 MP channel.** Deliberately not taken. It needs per-band bin arrays plus a merge, and it
  reorders the `total_value` double summation, which feeds `hist_mean` -> `Background()`'s mode
  search -> the star-detection threshold. The reordering is almost certainly invisible at float
  precision, but "almost certainly" is not the bit-identical claim the other three changes carry,
  so it wants its own change with its own evidence. Reproduce with
  `TIANWEN_HISTOGRAM_PROBE=1 dotnet test -c Release --filter HistogramCostDecompositionProbe`
  (note `-c Release`).
- [ ] **`FindStarsAsync(maxStars:)` caps nothing -- delete the parameter.** Its only effect is to
  default `minStars`, so a call passing it alone reads as "cap the list" and means "rescan until you
  find this many". The doc now says so (2026-08-21) but the name still lies. Deleting it is
  behaviour-preserving at every site (callers that pass both just drop it; callers that pass only
  `maxStars` rename it to `minStars`), and it is ~50 call sites incl. tests, so it wants its own
  wave rather than riding on an unrelated branch.
- [ ] Not sure if `SensorType` LRGB check is correct (`SensorType.cs:54`)
- [ ] Find bounding box of non-NaN region in `Image.cs` (for stacked images with NaN borders)
- [ ] Star detection noise robustness: `FindStarsAsync` with `snrMin: 5` picks up false positives from shot noise halos around bright stars (e.g. M42 synthetic field: 49 rendered stars → 64 detected). Consider deblending or a minimum star separation filter to reject noise peaks near bright stars.
- [ ] **Row-by-row odd/even amplifier (banding) correction.** CMOS sensors with per-row-parity amplifiers leave horizontal odd/even banding that calibration frames don't fully remove (it varies frame-to-frame). Classic fix (Siril's banding reduction, PixInsight CanonBandingReduction): per-row median (or odd/even-row-pair delta) minus the global background, subtracted per row, optionally protected by a star/signal mask so nebulosity isn't flattened. Should slot into the stacking pipeline as an opt-in per-light calibration step (post dark/flat, pre register); a standalone `image` verb variant is a cheap byproduct. (2026-07-07)
- [x] `RollingWindowStacker.BuildMasterAsync`: reuse a persistent sum scratch; **DONE (2026-07-06, same-day as the audit)** with a twist the audit missed: `PlanetaryMaster.NormalizeInPlace` *wraps* its input arrays into the returned `Image` and `MergeAndDemosaicAsync` passes mono/RGB masters through, so for mono/RGB the old `Clone()` **was** the master's backing store (not reducible, the previous master may still be displayed / cached for wavelet re-sharpen). Fix shipped as the fused `PlanetaryMaster.NormalizeInto(src, weight, dst, meta)` (single read-sum/write-dst pass replaces Clone-then-normalise for **both** paths, halving memory traffic) + a persistent `_sumScratch` used **only** on the split-CFA branch, where the normalised sub-planes are transient (merged + demosaiced into a fresh master). Scratch is shape-checked (reference/ROI can change). Pinned by `RollingWindowStackerTests.Published_mono_master_stays_valid_after_the_next_publish` (guards against ever routing mono through the scratch).
- [ ] Decide the fate of `Image.DebayerIntoAsync`: the write-into-caller-`Channel[]` debayer variant has **zero callers** (viewers upload the raw mosaic and the GPU debayers; batch paths use the allocating `DebayerAsync`). Either wire it into a real consumer or delete it; CLAUDE.md no longer cites it as "the viewer path". (Buffer-utilization audit 2026-07-06; see `docs/architecture/image-pipeline.md` § Driver Coverage.)
- [x] `Image` constructor should take `Channel`s, not raw `float[][,]`; **DONE (2026-07-06, same day as filed)**. The primary ctor is now `Image(ImmutableArray<Channel> channels, BitDepth, pedestal, meta)`: per-channel `Filter`/`MinValue`/`MaxValue`/`Index` live on each `Channel` (readable via `Image.GetChannel`), the image-wide `MaxValue`/`MinValue` are **derived extrema** across the channels, shapes are validated same-dimension, and the ref-counted camera buffer travels ON the channel (new `internal Channel.Buffer` init-prop, harvested by the ctor, `WithChannelBuffers` and the `ICameraDriver.ChannelBuffer` side-channel property are deleted). The legacy raw-array signature survives as a delegating overload that stamps the image-wide values on every channel, so all ~164 existing construction sites compiled untouched (derived extrema == the passed values by construction). `ICameraDriver.GetImageAsync` is the single typed hand-off (`new Image([channel], …)`); all four buffer-recycling drivers (DAL, Fake, Alpaca, ASCOM) attach the buffer at `Channel` creation, and `FakeCameraDriver.ReleaseImageData` strips the buffer from its retained channel so a second `GetImageAsync` cannot harvest an already-transferred ref. `ScaleFloatValuesToUnitInPlace` rewraps per-channel min/max scaled but deliberately drops `Buffer` (release responsibility stays with the original, double-release guard). Pinned by `ImageChannelCtorTests`.
- [ ] **AHD debayer: SIMD via output-tile chunking**. Phase 3 (homogeneity comparison, ~70% of AHD's cost) is currently scalar with `Unsafe.Add` (commit 958e42e). To vectorise, process 8 output pixels per `Vector<float>` lane: the 5×5 neighbourhoods of consecutive x positions overlap heavily, so each dx offset becomes a single AVX2 load that serves all 8 pixels. Realistic landing: AHD 298 ms → ~140-150 ms (another ~2× on top of what we have). Non-trivial: the `if (diffH < diffV) homH++ else homV++` branch needs `Vector.GreaterThan` + masked accumulate, the direction-select tail needs `Vector.ConditionalSelect`, and `Vector.Sum`'s tree-add will likely change FP rounding order vs scalar sequential add → `DebayerRegressionTests` hashes will need repinning. Code complexity ~3-5× current scalar+unsafe path. Worth taking on if/when AHD perf dominates wall-clock for big groups (SoL 60s and similar 200+ frame stacks). See `Image.Debayer.cs:559-643` Phase 3 + the discussion in commit 958e42e for design context.

## Codecs facade (read + write)

The `SharpAstro.Codecs 3.6.*` facade (sniff → dispatch: PNG/JPEG/TIFF/JXR/EXR/JXL, plus the
`SharpAstro.Jpeg.GainMap` Ultra HDR member) is consumed for **read** in tianwen as of the Phase-5
fallback (`Image.Import.TryReadViaCodecs`; full arc in [`../plans/image-codecs-facade.md`](../plans/image-codecs-facade.md)).
Open gaps:

- [ ] **We ship a JBIG2 decoder we can never use, and trimming cannot remove it.** `SharpAstro.Codecs`
  is a `static readonly` dispatch array that literally names `Register<Jbig2ImageDecoder>()`, so the
  decoder is ROOTED -- the trimmer must keep it, in every AOT binary. JBIG2 is bilevel fax/PDF
  compression; it appears nowhere in this repo (no extension in `Image.Import.cs`'s codec list, no
  call site, no mention) and no astro tool emits it. Cost: **97,792 bytes of IL**, more once
  AOT-compiled, across the four shipped binaries. Small against a 58 MB MSIX -- under a percent -- so
  this is tidiness with a number on it, not urgency.
  JXL (168,960 B) and JXR (220,672 B) are NOT in the same category: `Image.Import.cs:57` routes
  `.jxl`/`.jxr`/`.wdp` deliberately, so both are paid for on purpose.
  Preferred fix is upstream and non-breaking: give the facade a per-decoder trim feature switch
  (`ILLink.Substitutions.xml` + `RuntimeHostConfigurationOption`, the BCL's `EventSourceSupport`
  pattern), so a consumer opts out and the decoder is substituted away. That needs no API change, no
  package split, and leaves every existing consumer byte-identical. Rejected alternatives: splitting
  the facade into core + `.All` (changes its contract), and having tianwen reference individual codecs
  (the facade owns the sniff table, which is real shared logic -- though note the ORIGINAL reason for
  the facade, version skew from cherry-picking, is now handled by the single
  `$(SharpAstroCodecsVersion)` property, so that argument is weaker than it was).
- [x] **CMYK / Separated TIFF renders as a negative (DONE 2026-08-20).** A `Photometric = 5` TIFF with 4 samples per
  pixel (a print export, e.g. GraXpert's `..._printer.tiff`) has its C/M/Y read as R/G/B with K
  dropped, and since a high CMYK value means MORE ink the polarity inverts: white sky, dark stars,
  cyan cast. `SharpAstro.Tiff.TiffImageDecoder` already declares this out of scope
  (`page.Photometric is not (MinIsBlack or Rgb)` -> refuse), but `Image.Import.cs` calls
  `TiffReader.Read(bytes)` **directly** and bypasses that guard. Two options: honour the guard so the
  file fails to open with a clear message (consistent with a decision the codec layer already made),
  or convert CMYK->RGB. Converted, in `Image.Import.cs`: accurate conversion needs the embedded ICC
  the naive `R = (1-C)(1-K)` form is what most viewers do and would at least fix the polarity.
  Backlogged and then done the same day, out of [`../plans/viewer-prerelease-fixes.md`](../plans/viewer-prerelease-fixes.md)
  (was P4): a printer proof is a print export, not a working frame, so it does not gate a release.
  Note it only became *visible* once the Predictor 2 fix made the file decode to structure at all.
- [x] **Gain-map JPEG export during stacking / rendering (DONE).** Emits Ultra HDR (hdrgm 1.0 / Android
  Ultra HDR v1) gain-map JPEGs from the stacking/render preview path; a broadly-supported HDR delivery
  format (Android / Chrome / Adobe) alongside the existing cICP-PQ PNG HDR previews. Unlike the PQ preview
  (a uniform re-map of the already-clamped SDR raster), the gain map performs **per-pixel highlight
  recovery**: `Image.RenderHdrLinearRgb` renders a display-referred *linear* rendition (1.0 = SDR white)
  from the master's PRE-MTF signal, the value the midtones transfer function flattens to a white plate in
  SDR, so a bright core (star / nebula / galaxy) that the stretch over-blew keeps its structure and gradient
  on HDR viewers while the faint background matches SDR exactly (gain ~0 below the clip). Wiring:
  `MasterPreviewRenderer.RenderAsync(ultraHdrPath:)` builds the SDR base + HDR-linear pair, `JpegGainMap.Compute`
  fits the map, `JpegEncoder.Encode` encodes both renditions, `JpegGainMap.Assemble` splices GContainer XMP +
  MPF. Selected via the `MasterRenderOutputs` `[Flags]` enum on `StackingOptions.RenderOutputs`
  (`stack --output-format uhdr`, `--hdr-peak-nits`) and `ImageOutputFormat.UltraHdr` (`image render/sharpen
  --output-format uhdr`, headroom from `--png-pq-peak-nits`). Headroom = peak nits / 203-nit BT.2408 SDR
  reference white; cores roll off smoothly toward the cap. Stretched display raster only, never the linear
  FITS/EXR masters or split-plate TIFFs (same rule as `MaskedBoost`). Pinned by `MasterPreviewUltraHdrTests`.
  **Caveat:** a gray gain map recovers highlight *structure/luminance*, not saturation (the recovered core
  keeps the SDR base's hue), per-channel re-saturation would need an RGB gain map (a later refinement),
  and it can only recover headroom the linear master actually holds (a sensor-saturated core stays flat).
- [ ] **Honour `IDecodedImage.ColorEncoding` on facade read.** `TryReadViaCodecs` ingests `ToFloats()`
  verbatim as `[0,1]` (container-only), so a PQ/HLG or non-sRGB HDR raster (incl. tianwen's own cICP-PQ
  PNG previews) is read as if linear; wrong for display. Linearise / tone-map per `ColorEncoding` on
  ingest instead of trusting the `[0,1]` convention (correct only for the scene-linear TIFF/EXR/JXR
  masters tianwen writes). Bespoke TIFF / CR2 / CR3 / FITS readers never route through the facade.
- [ ] **No gain-map reconstruction on read.** A gain-map JPEG decodes to its base SDR image only; the
  gain map is not applied to recover HDR. Follows the export work + the JPEG encoder.
- [ ] **Phase 6: FC.SDK → facade.** FC.SDK still references the individual codec packages; pointing it at
  the facade removes the last version-skew source (`../plans/image-codecs-facade.md` phase 6).

## AI Enhancement

Shipped on branch `ai-enhancement` (Phases 0-6 of `docs/plans/ai-enhancement.md`): `IStarRemover` + `IStellarSharpener` + `INonStellarDeconvolver` atomic enhancers, `SharpenPipeline` orchestrator (additive + screen modes), shared `ChunkedNafnetRunner`, MTF helpers on `Image.Stretch.cs`, `ChunkedInference` tile/stitch, `HfdPsfEstimator`, `tianwen image {sharpen,remove-stars}` CLI. Items below are deferred follow-ups.

### Deferred CLI verbs (image group)

Each verb maps to an enhancer / classical implementation that hasn't been wired yet. CLI shape mirrors the shipped `tianwen image sharpen` (input FITS, `-o output`, default `<input>_<verb>.fits`).

- [ ] `tianwen image denoise` -- wraps `deep_denoise_{color,mono}_AI4.onnx`. New `IDenoiseEnhancer` interface + ONNX impl following the same shape as the three shipped enhancers.
- [ ] `tianwen image denoise-walking` -- specialised walking-noise variant via `deep_denoise_*_AI4_1w.onnx`. Could be a flag on `denoise` rather than a separate verb.
- [ ] `tianwen image upscale 2x|3x|4x` -- wraps `superres_{2,3,4}x.onnx`. New `IUpscaleEnhancer`. Output dimensions are scale * input.
- [ ] `tianwen image remove-trails` -- `satelliteRemovalAI4.onnx`. Per `docs/plans/stacking.md` this logically belongs in the stacking pipeline as a pre-rejection filter; standalone single-image verb is also useful.
- [ ] `tianwen image correct-aberration` -- optical aberration correction (coma, astigmatism, off-axis distortion). Models hosted in `riccardoalberghi/abberation_models` (different repo + release cadence than AI4); needs a separate fetcher branch in `tools/tianwen-ai-models-fetch.ps1` + runtime self-bootstrap. **Not the same fix as raising SIP order** -- SIP corrects centroid position, never PSF/star shape; see `docs/plans/astropy-parity.md`'s SIP design note.
- [ ] WCS-driven image undistort + mosaic reprojection (use `WCS.PixelToSky`/`SkyToPixel` generatively, to resample pixels, not just report a header). See [docs/plans/wcs-reprojection.md](../plans/wcs-reprojection.md).
- [x] `tianwen image flatten` -- ABE gradient removal (classical, no AI). **Core SHIPPED 2026-09-02** as
  `ClassicalBackgroundExtractor` (`docs/plans/background-extraction.md`, "Implementation": robust degree-2
  polynomial + optional inpainted surface, linear, level preserved per plane, CFA per photosite), and
  `flatten` runs it whenever GraXpert's weights are absent (`FallbackGradientCorrector`). RBF was dropped by
  the reference review, not deferred.
- [ ] `tianwen image flatten` options for the classical fit (background-extraction Phase 4): `--degree`,
  `--surface`, `--divide`, exclusion polygons, and a way to force classical over GraXpert; then the GUI
  preview. **The two structure thresholds were measured 2026-09-03 (G1, 118 masters) and neither is a
  knob**: 3 is safe anywhere in 2 to 6, 10 is inert across 5 to 40 because a real surface residual never
  reaches five sigma. Do NOT expose either on the CLI; expose `--surface`, which is the switch that
  actually moves the model (0.40 sigma RMS p50, kept 0.795 to 0.581). See
  [background-extraction.md](../plans/background-extraction.md), "The two thresholds, MEASURED".
- [x] **Gradient-distribution report over the retained masters** (gradient-remover-training.md G1).
  **DONE 2026-09-03**: `tianwen dataset gradient-report --masters <bake>/session-masters --out <bake>`
  (`DatasetGradientReport`, `tools/run-gradient-report.ps1` for the detached run), append-only
  `stats/gradient-masters.jsonl` + a rewritten `stats/gradient-report.md`. 118 masters over both bakes:
  amplitude, shape, principal direction joined to altitude, azimuth, airmass, parallactic angle and Moon
  geometry, plus a threshold sweep. The H1 verdict is in the plan.
- [x] **Read Siril's gradient-correction scripts as a reference for the above** (user, 2026-08-18).
  **DONE 2026-09-02**: all three read in full and distilled into
  [background-extraction.md](../plans/background-extraction.md) "Reference review". Two of them
  change that plan: `AutoGradientRemoval.py` needs NO sample points (robust pixel-set rejection +
  a masked low-pass inpainting surface, which also retires the RBF solver question), and
  `AutoBGE.py` (the SAS v2 AutoDBE port) fits AND corrects in a STRETCHED domain, which we must not copy;
  SAS Pro's current `abe.py` (read from GitHub the same day) calls moving the correction back to linear
  its "KEY FIX", seeds its sampler and defaults RBF on. Also
  found: GraXpert is GPL-3.0 code + CC-BY-NC-SA-4.0 models, not the "MIT" two of our comments said.
  Siril's background extraction is one of the three prior-art implementations
  [background-extraction.md](../plans/background-extraction.md) already names (with PixInsight
  ABE/DBE and GraXpert), but only GraXpert has a concrete interop question recorded against it (its
  open question 4, reading GraXpert's exported background images). Siril's are readable scripts
  rather than a binary, which makes them much the cheapest of the three to learn the sample-placement
  and rejection heuristics from -- exactly what `image flatten` has to get right and what the plan
  currently leaves open. **The note carried no link**; these were located afterwards in the same
  `free-astro/siril-scripts` repo the narrowband work already sources
  ([narrowband-colour.md](../plans/narrowband-colour.md)), so there is nothing further to hunt for:
  - [`processing/AutoGradientRemoval.py`](https://gitlab.com/free-astro/siril-scripts/-/blob/main/processing/AutoGradientRemoval.py)
    -- the closest match to the note's own words.
  - [`processing/AutoBGE.py`](https://gitlab.com/free-astro/siril-scripts/-/blob/main/processing/AutoBGE.py)
    -- automatic background extraction; the sample-placement half, which is the part `image flatten`
    most needs and the part the plan leaves open.
  - [`processing/GraXpert-AI.py`](https://gitlab.com/free-astro/siril-scripts/-/blob/main/processing/GraXpert-AI.py)
    -- **not** asked for, but it is Siril driving GraXpert, so it answers
    background-extraction.md's open question 4 (how a GraXpert workflow interoperates) from a working
    implementation rather than from the docs.
  **The licence rule from the narrowband work applies verbatim**: the Siril script repo is GPL-3.0
  against our AGPL-3.0, so reimplement from the recorded maths, never vendor.
- [ ] `tianwen image stretch` -- apply MTF stretch for display / PNG export.
- [ ] `tianwen image debayer` -- Bayer raw → RGB.
- [ ] `tianwen image calibrate` -- apply master bias/dark/flat (wraps the calibrator types from `docs/plans/stacking.md`).
- [ ] `tianwen image stats` -- HFD/FWHM/background/SNR.
- [ ] `tianwen image info` -- print FITS headers.
- [ ] `tianwen image histogram` -- text or PNG output.
- [ ] `tianwen image crop`, `tianwen image resize`, `tianwen image convert <fits|tiff|png|jpg>` -- existing IO methods on `Image`; just need CLI surfacing.

### Other deferred AI work

- [ ] **Deployment / runtime self-bootstrap of model files.** Today the AI enhancers depend on `%LOCALAPPDATA%\TianWen\models` being populated by the dev script `tools/tianwen-ai-models-fetch.ps1`; shipped binaries can't expect that. Need (a) in-app first-launch fetch with progress UI, (b) `tianwen models fetch` CLI sub-command (programmatic equivalent of the pwsh script). Hardlink-from-SAS-Pro fast path stays as a power-user optimisation.
- [ ] `tianwen models list` -- show which models are present under `%LOCALAPPDATA%\TianWen\models` and which are missing per the expected manifest. Complement to `tianwen models fetch`.
- [ ] **Classical (non-AI) fallbacks** via `AddTianWenClassicalEnhancers()` extension, `TryAddSingleton` so `AddTianWenAi` wins when models present. Lucy-Richardson `INonStellarDeconvolver`, unsharp-mask `IStellarSharpener`, bilateral/NLM denoise. No classical fallback for `IStarRemover` -- no respectable analogue.
- [ ] **Hexagon NPU acceleration on win-arm64.** AI4 ships pure FP32; QNN HTP wants INT8/INT16 or a pre-compiled `.serialized.bin`. Either upstream re-export at INT8 or our own ORT QNN compile pass. Current behaviour: FP32 nodes per-node-fall-back to CPU on win-arm64 -- works, just doesn't use the NPU. The new per-phase timing log (`infer={ms}ms`) is the diagnostic that surfaces this.
- [ ] **Per-chunk PSF re-measurement for `INonStellarDeconvolver`.** v1 `HfdPsfEstimator` returns a whole-image scalar; SAS Pro re-measures PSF per chunk via SEP to capture tilt/coma variation across the field. Would land as a new `SepPerChunkPsfEstimator` (port of SAS Pro's `measure_psf_radius`) registered through `IPsfEstimator` -- no changes to the deconvolver needed.
- [ ] **GUI menu entry for `SharpenPipeline`.** Surface the same flow through the GUI's processing menu so non-CLI users can run AI sharpen against the currently-loaded image. Reuses `SharpenPipeline` from DI; UI is a checkbox set per `SharpenRequest` field.
- [ ] **Star plate hue preservation under clipping.** `StretchStarsStep` is per-channel MTF (`Image.StarStretch` = fixed-midtones MTF with `m = 1/(3^amount+1)`), so a bright star that pegs one channel at 1.0 post-stretch but not the others collapses toward white -- an A0 blue and a K2 orange both end up grey. Existing `Image.StretchLumaPixelCpu` (Y'/Y chrominance scaling, used by the viewer's Luma stretch mode) already does the right math. Plan: add a `LumaBlend` knob to `StretchStarsStep` mirroring `StretchUniforms.LumaBlend`; 0 = today's per-channel (sensor-accurate, can clip), 1 = pure Y'/Y (no hue shift under any stretch), ~0.7 default for "coloured stars even at heavy stretch without going artificial". Zero overhead when `LumaBlend=0`. ~80 LOC + 2-3 unit tests against synthetic clipping cases.
- [ ] **Frank Sackenheim's colour-boost (saturation) option on star stretch.** The original SAS Pro `StarStretch` script ships a "Saturate" slider that increases star chroma -- gives the blue / orange end of the stellar spectrum visible punch without changing brightness ordering. Typical impl: RGB -> HSV (or LCh), multiply S/C by 1.x, back to RGB. Pairs naturally with the hue-preserving Luma-blend variant above; you stretch luminance, then optionally boost saturation in the same pass. Add as `StretchStarsStep.SaturationBoost` (default 1.0 = no-op). Cite SAS Pro `star_stretch.py` in the xmldoc for the exact ratio.
- [ ] **Colour-preservation audit across the AI pipeline.** Quantify channel-wise saturation drift pre/post each `SharpenPipeline` stage (measure mean/95th-pct chroma in LCh, per stage, on the enhance corpus) to ground the "we lose a lot of colour info" impression with numbers and decide whether a saturation-restore step is needed (and after which stage). Pure measurement first -- pairs with the Luma-blend + SaturationBoost items above, which are the likely remedies. (2026-07-07)
- [ ] **Star-halo detection via Hough circle transform.** Detect circular halos around bright stars (reflection halos from filters/optics) with a Hough circle pass seeded at bright-star positions from `FindStarsAsync`, then feed the detected annuli into a removal step -- investigate combining with SETI Astro's manual halo-removal approach (the PixInsight plugin) as the correction model, i.e. our detector supplies the centres/radii the SAS tool takes as manual input. Could also serve the star-detection false-positive item above (noise peaks in halos). (2026-07-07)
- [x] **Productionise the GHS starless stretch.** DONE on branch `ghs-converge` per [ghs.md](../plans/ghs.md). The dim-output problem was the convergence target -- median-target = 0.25 left the bg peak (mode) below 0.25 for typical astro frames where median sits above mode (signal tail). Resolved by adding mode-target convergence (`--ghs-target Mode`) + Cranfield's canonical multi-stage chain (`--ghs-stages 3`: stage 1 + BackgroundReduce + stage 2 b=2.5/hp=0.95 + stage 3 b=-1/hp=0.99 log). Canonical recipe: `--dual-stretch --starless-stretch-mode Ghs --ghs-target Mode --ghs-target-value 0.25 --ghs-stages 3`. **GHS stays opt-in per `feedback_ghs_not_default`** -- MTF remains the default starless stretch; user explicitly decided against promotion to `SharpenRequest.Canonical()` because GHS is a different aesthetic, not a universal upgrade. Outstanding: {broadband, narrowband, single-light} corpus validation outside SoL drizzle. The parameter-prediction model idea is parked -- the multi-stage canonical chain with mode convergence covers the in-corpus failure modes without ML.

## Stretch / Image Processing

Learnings from PixInsight Statistical Stretch (SetiAstro, v2.3).

- [x] **Masked finishing boost for the preview render** (2026-07-03); `Image.MaskedBoost` composes the new mask primitives (`LuminanceRangeMask` + `BlendThroughMask` + `Saturate` / `ContrastBoost`, `Image.Masks.cs`) into the Affinity masked-contrast-boost + saturation macro; surfaced as `stack --saturation/--contrast-boost` + the same flags on `image render`, applied to the stretched preview PNG only (`MasterPreviewRenderer.ApplyMaskedBoost`). Basic mask support shipped alongside: `Invert`, `Binarize`, `GaussianBlur` (feathering), scalar `Multiply` (partial-strength masks). Linear masters + split-plate TIFFs untouched by design.
- [ ] **Give the autostretch black point a confidence signal, and an estimator that does not assume a
  Gaussian background.** Two gaps in our own code, stated as such because they are worth closing on
  their own merits. We derive the black point statistically as `median + (-2.8 x MAD)`, mirroring
  Siril's `find_linked_midtones_balance`, which (a) assumes a roughly Gaussian background and (b)
  reports nothing when it is wrong, so **a bad MAD silently produces a bad stretch** and the first
  anyone hears of it is a render that looks off. A confidence number is arguably the more valuable
  half: it lets the pipeline warn or fall back instead of quietly emitting a bad frame.
  A **geometric** estimator gives both. Per channel: build a cumulative histogram from 0 up to the
  median (averaging adjacent bins); take the noise floor as the first bin whose cumulative exceeds
  `(totalPixels / 10000) * aggressiveness`; least-squares fit a line to the cumulative between that
  floor and the histogram peak; the black point is that line's **x-intercept** (`-c/m`), and the fit's
  **R-squared is the confidence measure**, falling back to 0 with a warning when the fit fails or lands
  above the median. Worth pairing with a **negative output shadow** in the histogram transform, so
  input-black maps above 0: a deliberate "do not crush the blacks" control (a sensible default is
  around 0.05).
  *Prior art:* this is the approach EZ_SoftStretch takes. Its source carries a bare copyright line with
  **no licence grant at all**, i.e. all rights reserved, not GPL, and it is deliberately not linked
  here: the method above is all that is needed and all that is usable. Do not go looking for the code.
  (Surfaced 2026-08-02 via the "RESCUE the BLUE" workflow; adjacent to
  [narrowband-colour](../plans/narrowband-colour.md) but a stretch concern, not a colour one.)
- [ ] **DarkStructureEnhance: a one-sided unsharp mask that darkens dust lanes.** Read from source
  (`DarkStructureEnhance.js`, Carlos Sonnenstein + Oriol Lehmkuhl, PTeam). Two steps. **Mask:** an
  a-trous wavelet pass strips the small scales to leave a large-scale (locally smoothed) version, then
  `mask = large_scale - original`, converted to grayscale, rescaled to [0,1] and noise-reduced. Where a
  pixel sits *below* its local large-scale average the difference is positive, so the mask lights up on
  exactly the dark structures (dust lanes, dark nebulae) and is near zero elsewhere. It is an unsharp
  mask kept only on its negative side. **Apply:** a `HistogramTransformation` whose RGB midtones balance
  is the "Amount" parameter, applied *through* that mask; a midtones value below 0.5 darkens, so only
  the dark structures get pushed down. Repeated `iterations` times through a ProcessContainer. Runs on
  **stretched** data, typically last in the workflow, no user mask needed. **Cheap for us:** we already
  have a-trous wavelets (`WaveletSharpen`, from the planetary stacker), the MTF (`Image.StretchValue`)
  and `BlendThroughMask`, so this is composition rather than new maths. **Licence: algorithm only.**
  `DarkStructureEnhance.js` ships with PixInsight and its reuse terms have not been established, so it
  gets the same treatment as any unverified source: implement from the description above, do not copy.
  (2026-08-02, from the "RESCUE the BLUE" workflow.)
- [ ] **`tianwen image adjust` standalone verb**: apply `Image.MaskedBoost` (and the raw mask primitives, e.g. `--export-mask` for previewing what a step will touch) to an already-stretched TIFF/FITS plate outside the stack/render flow. The primitives + CLI parsing shape (mirror `image render`'s flags) are in place; deferred until a concrete need.
- [ ] **Mask morphology (dilate / erode)**: the remaining classic mask ops beyond invert/feather/binarize; useful for growing a star mask before protection. Deferred until a consumer exists.

- [x] Luma-only stretch mode (Rec. 709 luminance, stretch Y, scale RGB by Y'/Y)
- [x] HDR compression in GPU shader (Hermite soft-knee, `uHdrAmount`/`uHdrKnee` uniforms)
- [x] Normalize after stretch (2026-05-11): `StretchUniforms.NormalizeScale` carries a precomputed `1/max` so the GPU stays single-pass. `Image.PredictPostStretchMaxScale` walks the top non-zero histogram bin of each channel and pushes it through the full chain (stretch + curves + HDR); CPU and GPU multiply the post-HDR value before the final clamp. Producer surfaces a `normalize: bool` knob on `AstroImageDocument.ComputeStretchUniforms`; tests in `StretchTests_NewPipeline.GivenColorFitsWithHdrWhenNormalizingThenPeakLiftedToFullRange` + `GpuStretchPipelineTests.GpuMatchesCpuForHdrNormalize`.
- [x] Iterative convergence: `Image.ConvergeStretchFactor` bisects stretchFactor using histogram until post-stretch median converges to target (0.25). Gated by `AstroImageDocument.UseIterativeConvergence`. **Bisection direction was inverted (fixed 2026-05-10)**; **WB-aware (median/mad/binNorm scaled by `whiteBalance` scalar) since 2026-05-10** so converged factor matches per-channel rendering when SPCC/skyBg WB is active.
- [x] Star-masked background extraction: `GetStarMaskedMedianAndMADScaledToUnit` recomputes median/MAD excluding star pixels after detection; `StarMaskedStats`/`StarMaskedLumaStats` preferred in `ComputeStretchUniforms`. **Two bugs fixed 2026-05-10**: (1) returned median in raw pixel-value space while the unmasked twin returns pedestal-subtracted, now consistent; (2) MAD floor `invMax * 0.5f` collapsed to 0.5 after `ScaleFloatValuesToUnitInPlace`'d images had `MaxValue=1`, pinning every masked MAD at half the dynamic range; replaced with fixed `0.5/65535` bin-width floor.
- [x] CPU mirror of GLSL stretch: `Image.StretchChannelCpu` / `StretchLumaPixelCpu` / `ApplyHdr` / `RenderStretchedRgba` (full image → RGBA buffer). `ConsoleImageRenderer` and `StretchTests_NewPipeline` route through these; both must produce visually equivalent output to the GLSL fragment shader for the same `StretchUniforms`.
- [x] Tycho-2 photometric color calibration: `Tycho2ColorCalibration.ComputeWhiteBalance` matches detected stars to Tycho-2, extracts aperture photometry, computes WB multipliers; flows through GPU UBO and CPU path
- [x] SPCC spectrophotometric color calibration: `Tycho2ColorCalibration.ComputeSpectrophotometricWhiteBalance` integrates Pickles SED × system throughput (QE × CFA × filter) per matched star, fits WB multipliers; `AstroImageDocument.ComputeSpccColorCalibrationAsync` surfaces to viewer; `W` key tries SPCC first, falls back to sky-bg method. **Verified end-to-end** by `StretchTests_NewPipeline.GivenSyntheticStarFieldWhenSpccCalibratedThenWritesTiff`; projects Tycho-2 stars onto a synthetic Sony OSC field with matching synthetic WCS, runs SPCC against IMX533 QE × Sony CFA throughputs.
- [x] Background neutralization (pivot1 mode): `BackgroundNeutralization.ComputeGains` ports SETI Astro Suite Pro's highlight-protecting neutralization; uses existing `ScanBackgroundRegion` for dark-region sampling; GPU shader applies `out = norm * g + (1-g)` before white balance; `N` key toggle, toolbar button. Algebraically verified equivalent to SETI's `out = 1 - (1 - val) * g`.
- [x] Fritsch-Carlson spline curves: `FritschCarlsonSpline` struct with monotonic cubic Hermite interpolation; `applyCurveLUT` in GLSL shader via 33-knot UBO; `ApplyCurveLut` CPU path. **`ComputeKnots33` capacity bug fixed 2026-05-10** (would crash GUI when user pressed Shift+B to toggle curve mode); array now sized to 33 floats with no padding so CPU/GPU divisor (lut.Length-1 vs hardcoded 32) align.
- [x] WB-vs-shadow coordinate-space mismatch fixed (2026-05-10); `ComputeStretchUniforms` now scales per-channel median+mad by WB before deriving shadows/midtones/rescale, so post-WB norm and shadow live in the same space and channels reduced by WB don't clamp to zero.
- [x] SASP filter/sensor/SED data tracked in git (2026-05-10); `filter_curves.gs.gz`, `sensor_qe.gs.gz`, `pickles_sed.gs.gz` exempt from the gitignore wildcard so CI can load them. Total +3 MB; only changes when SASP-data upstream changes.
- [x] Test verification overhaul (2026-05-10): `StretchTests_NewPipeline` asserts every `StretchUniforms` field (Pedestal/Shadows/Midtones/Rescale/WhiteBalance/BackgroundNeutralization/CurveData) plus per-channel byte means after rendering. `StretchTestBase` got per-channel float-range + AutoLevel quantum-range assertions for all 4 legacy stretch test files. Catches per-channel collapse regressions.
- [x] **Mesa lavapipe CPU/GPU divergence: root cause was a dangling-pointer bug in `SdlVulkan.Renderer/VkPipelineSet.cs`** (resolved 2026-05-11 evening). NOT a Mesa bug.

  **Actual root cause**: `new VkPipelineColorBlendStateCreateInfo(blendAttachment)` (Vortice.Vulkan 3.2.1 constructor that takes a single attachment by value) stores `pAttachments = &attachment` pointing at the constructor's stack frame, which is reclaimed when the constructor returns. The graphics-pipeline create then reads garbage `VkBlendOp` from that location. On ARM64 the post-frame stack happened to contain values that decoded to valid blend ops; on x86_64 it contained values outside the valid `VkBlendOp` enum range. Release Mesa silently passed the garbage through `vk_blend_op_to_pipe`, producing zeroed-out fragment writes for primitives and the partial channel corruption we observed when the clear color was non-zero.

  **Fix**: in `SdlVulkan.Renderer/src/SdlVulkan.Renderer/VkPipelineSet.cs::CreatePipeline`, replace the single-arg constructor with an explicit `stackalloc VkPipelineColorBlendAttachmentState[1]` whose lifetime spans the `vkCreateGraphicsPipeline` call, then `pAttachments = blendAttachments; attachmentCount = 1`. The local `tools/lavapipe-repro` rebuilt against the fix reports the expected nonzero pixel counts on x86_64 lavapipe with Mesa 25.2.8 / LLVM 20.1.2 / 256-bit AVX2: FillRectangle=18200, DrawRectangle=2752, DrawLine=180-236, FillEllipse=15380, DrawEllipse=1272.

  **How we found it**: built Mesa 25.2.8 from source with `-Dbuildtype=debug -Dshared-llvm=enabled -Dgallium-drivers=llvmpipe -Dvulkan-drivers=swrast -Dplatforms=` and pointed the repro at `lvp_devenv_icd.x86_64.json` via `VK_DRIVER_FILES`. The debug build trips the assertion `vk_blend_op_to_pipe: Invalid blend op` in `src/vulkan/runtime/vk_blend.c:66` and tells us the value passed to `vkCmdBindPipeline`'s blend op was bogus. Distro Mesa is shipped without `--enable-debug`, so `LP_DEBUG=llvm` is a no-op and validation layers don't catch this; only the assertion in debug Mesa surfaces it.

  **Follow-ups** (all DONE, verified 2026-06-12):
  - Commit the fix to `SdlVulkan.Renderer`: done, published (tianwen consumes 6.0 as of PR #21).
  - Bump `SdlVulkan.Renderer` minor and publish via `/release-lib`; done.
  - Bump tianwen `Directory.Packages.props` to consume the new version; done (`549b612` bumped 5.1 -> 6.0).
  - Revert the `Assert.Skip(llvmpipe)` guards in `GpuStretchPipelineTests`, `VkHistogramPipelineTests`, `VkRendererPrimitiveTests`; done, no llvmpipe skips remain in the tree.
  - Delete `.github/workflows/test-mesa-latest.yml`: done, only `dotnet.yml` remains.
  - `lavapipe-bug-report-draft.md` deleted: no upstream bug to file.

- [x] Luma blend (2026-05-11): `StretchUniforms.LumaBlend` (0 = pure linked, 1 = pure luma, default 1 preserves status-quo Luma-mode behaviour). Producer always populates `LumaStretch` (scalar Luma MTF params) and per-channel linked `Shadows/Midtones/Rescale` in Luma mode so the shader has both branches ready; GLSL `mix(linked, luma, lumaBlend)` inside the Luma branch. Tests: `StretchTests_NewPipeline.GivenColorFitsWhenBlendingLumaWithLinkedThenOutputInterpolates` + `GpuStretchPipelineTests.GpuMatchesCpuForLumaBlend`.
- [x] Rec.601 / Rec.2020 luma weighting (2026-05-11); new `LumaWeighting` enum, `StretchUniforms.LumaWeights` `(R,G,B)` triple, resolved by producer; CPU mirror + GLSL Luma branch + `ComputePostStretchBackground` all read from the uniform. Default Rec.709 keeps existing callers on the same numerical path. Tests: `StretchTests_NewPipeline.GivenColorFitsWhenSwitchingLumaWeightingThenWeightsFlowThrough` + `GpuStretchPipelineTests.GpuMatchesCpuForLumaWeightingProfiles`.
- [x] Sensor-derived luma weights (2026-05-11): `LumaWeighting.SensorMatched` resolves through `FilterCurveDatabase.TryComputeSensorLumaWeights(meta, ...)`, which integrates the doc's `BuildChannelThroughputs` (sensor QE x Sony CFA R/G/B) and normalises to sum to 1. Helper retries with `SensorType.RGGB` so debayered OSC images still resolve to the sensor-specific triple; gated on a recognised SensorModel so typos fall back to Rec.709 instead of silently returning CFA-only weights. Pure producer-side wire-up via `AstroImageDocument.ResolveLumaWeights`; no UBO / shader churn. Sample weights: IMX533 (0.29,0.36,0.34), IMX571 (0.35,0.37,0.28), IMX455 (0.30,0.37,0.34) -- broadband response (no photopic V(lambda) convolution, since the database doesn't ship it). Tests: `FilterCurveDatabaseTests.TryComputeSensorLumaWeights_*` + `StretchTests_NewPipeline.GivenOscMetaWhenLumaWeightingIsSensorMatched...` + `GpuStretchPipelineTests.GpuMatchesCpuForSensorMatchedLumaWeights`.
- [ ] Per-channel convergence: `ConvergeStretchFactor` runs once on luma stats; for Linked/Unlinked the converged factor is approximate per channel (still uses single factor with per-channel WB-scaled stats). Per-channel convergence would tighten the post-stretch median per channel; bigger refactor (factor becomes a triple).

## Colour: Unified camera→sRGB matrix

The dcraw `adobe_coeff` 3×3 (now shipped via `FC.SDK.Raw.CanonCameraProfiles`) handles Canon CR2 sensible-default rendering. For OSC astro cameras (ZWO / QHY / etc.) and for Canon bodies whose spectral data is publicly available, we can derive the matrix from first principles; same QE × CFA spectral integration that `Tycho2ColorCalibration.ComputeSpectrophotometricWhiteBalance` already does for SPCC WB. Three pieces, in order:

- [ ] **Add `ImageMeta.CameraToSrgbMatrix`**: nullable `float[]` (9 floats, row-major). Importers populate when known. Render pipeline applies after debayer + WB, before stretch. Identity when null (preserves current behaviour for FITS / TIFF / unknown sensors). This is the generic slot; it doesn't care whether the matrix came from a factory table or was derived from spectral curves.

- [ ] **`FilterCurveDatabase.TryComputeCameraToSrgbMatrix(sensorModel)`**: closed-form integral over the same QE × CFA curves SPCC already loads. For each sRGB primary, integrate against `QE(λ) × CFA_c(λ)` per channel to get the camera-RGB response; invert the resulting 3×3. No stars needed, no per-image fit; pure spectral algebra. Pre-condition: `FilterCurveDatabase.TryGet` returns spectral data for the sensor.

- [ ] **Jiang et al spectral CSV importer**: Stanford 2013 measured camera spectral response (QE × CFA per channel) for ~28 cameras including Canon EOS 5D Mark II / III, 1D X, 40D / 60D, Nikon D40 / D700 / D5100, several Sony / Olympus / Fuji bodies. Public CSV download. Small Python or C# tool that normalises to TianWen's `FilterCurveDatabase` `.gs.gz` format. Once imported, those camera models go through the spectral matrix path; cameras without entries fall back to `CanonCameraProfiles` (Canon) or identity (everything else).

Dispatch order on CR2 import: try spectral matrix first (best, first-principles); fall back to dcraw matrix (factory-curated); fall back to identity (warn). For non-Canon raws (NEF / ARW / etc.) only the spectral path applies until / unless a vendor-specific factory table lands too.

## Colour: narrowband

Everything above (and `Tycho2ColorCalibration`) assumes a **broadband** system: SPCC integrates a Pickles
SED against QE × CFA over the visible, which is exactly the wrong model for an Ha / OIII / SII stack. Today
a narrowband master has no colour-calibration path at all, so the palette is whatever the channel-assignment
and per-channel autostretch happen to produce.

Planned in **[docs/plans/narrowband-colour.md](../plans/narrowband-colour.md)** (researched 2026-08-02),
which carries the algorithms, a pros/cons table across the three candidate techniques, and four ADRs.
Summary of what was decided, so this file is not misleading on its own:

- [ ] **Phase 1-2: robust channel normalization + palette mixer** (the useful minimum). Median offset then
  MAD/percentile gain applied **about the background**, aligning G and B to R, followed by an `Ha`/`OIII`
  to RGB lerp. No catalog, no plate solve, no new data, and it is what actually fixes red-dominated HOO.
  Built almost entirely from primitives we already have. Reimplement from the maths in the plan: the
  reference implementation is GPL-3.0; reimplement rather than vendor by preference now that TianWen
  is AGPL-3.0-or-later and vendoring would be lawful (ADR-2, revised 2026-08-11).
- [ ] **Phase 3: dual-band Ha/OIII unmixing** (optional, gated on a known sensor). DBXtract algebra plus a
  nine-coefficient per-sensor crosstalk table, sourced from DBXtract rather than lifted from the script.
- [ ] **Phase 4: SPCC narrowband. BLOCKED, and the framing this item used to carry was wrong** (ADR-3). It
  cannot be done by pointing a narrow passband at our existing Pickles SEDs: a Pickles template is a
  spectral *type average*, so over a 3 nm window it cannot know whether a given star shows Ha in absorption
  or emission. Siril uses Gaia DR3 `xp_sampled` per-star spectra, which we do not have, so this is a Gaia
  project rather than a colour project. Also scoped to **exclude SHO** on Siril's own guidance: SPCC
  reproduces true spectral intensities and true SHO intensities are green-dominated, so the Hubble palette
  is not something a photometric calibrator should be producing.

