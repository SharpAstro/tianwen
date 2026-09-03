# Denoiser training: the campaign after v24

**Status: PLANNED (written 2026-09-02). Nothing below has run.** The shipped model is
`n2n_v19d_s2_final` (task N4, 2026-08-17), an 0.81 M-parameter noise-conditioned U-Net trained by
Noise2Noise on eight OSC narrowband sessions, reachable as `--ai-backend n2n`. Everything measured
about it is in [osc-narrowband-denoiser.md](osc-narrowband-denoiser.md) (the v15 to v24 run log) and
[ai-denoise-deconv.md](ai-denoise-deconv.md) sections 2.1, 2.4, 3a and 3b. This document does not
repeat those results; it states what they leave open, as testable hypotheses with the arm, the
prediction and the kill criterion written down before any GPU time is spent. It is the P1 row of the
programme's phasing table, restated at the level of detail a run needs.

Companions: [model-training-roadmap.md](model-training-roadmap.md) (the order across all five
trainings and the shared tooling), [deconvolver-training.md](deconvolver-training.md),
[gradient-remover-training.md](gradient-remover-training.md).

## 0. Where it stands, with pointers

### The four facts that shape the next campaign

0. **The shipped C# path feeds the model a domain it never trained on (found 2026-09-02 while
   writing this plan).** `DatasetTileExporter` stores every tile AFTER `ChunkedNafnetRunner.ApplyInputStretch`
   (its own XML doc says so: "tile bytes are stored post-stretch"), and the trainer reads the bytes
   as stored. Measured on the `n2n-eval4` cache: master, sub and half-master tiles all have a
   per-channel median of 0.249 to 0.250 (the MTF target is 0.25), a sub's darkest-half sigma is
   0.0082 (the `SIGMA_SCALE` comment's "a single sub sits near 0.01") and a master's 0.00126. Yet
   `N2nLinearRunner` feeds a linear `[0,1]` image "verbatim", and its doc, `N2nDenoiser`'s doc, the
   ship README and section 1o of the run log all state the net trained on linear tiles. A real
   master in linear units has a median near 0.002 to 0.02 and a trainer-sigma of about 8e-5 (the
   seam probe measured exactly that), so at deployment the model sees pixels roughly 100x below the
   level and sigma it was trained on. This is the skew the programme's own "zero train/inference
   skew" rule exists to prevent, and it explains three measured oddities at once: the input-rescale
   probe peaking at the "honest" k = 124 (which happens to lift both sigma and median into the
   training band), the large per-chunk level offsets behind the PR #184 seams, and the modest
   master-depth gains on real frames against the far better numbers measured in Python on stretched
   tiles. **The Python-side conclusions (v8 to v24) are unaffected**: they were measured on the
   tiles, in distribution. What is wrong is the C# runner, and the fix is one call, not a retrain:
   stretch with the exporter's own `ApplyInputStretch`, run, invert. Hypothesis H0 below is the
   measurement that confirms it; it is also the cheapest thing in this document. **Done the same
   day (E0.5): H0 confirmed, the runner fixed, the table in section 9.** The skew's largest effect
   was not the noise shortfall but a flat 30 percent cut of every star's peak at every brightness,
   which nothing in the programme had measured because the tiles were in band.
1. **Sub pairs are the wrong teacher for the master regime.** A pair's gradient scales with the pair's
   noise, so at deployment depth (a master at 0.152x of a sub's noise, a half-master at 0.215x) the
   N2N family pays 21 to 32 percent of faint-star amplitude to remove 17 to 26 percent of the noise
   (`n2n_halfscore.py`, scored against the independent other half). Half-master pairs were the
   planned fix and are a **final negative** (three seeds, two conditioning shapes). Section 3b of the
   programme doc promotes supervised **synthetic noise injection** to co-primary for the deep end;
   no arm has yet been trained that way.
2. **The pool is 100 percent OSC narrowband, and the deployment target says broadband.** The
   organized bake (`D:\Astro-Dataset\2025-2026-organized`) holds 40 sessions of Optolong L-Ultimate
   3 nm and 11 of L-Quad Enhance, zero broadband. Task N3 (restate or acquire) is still open.
3. **There is no size recipe, and the variance exceeds every effect chased since v17.** Three disjoint
   eight-session draws scored 0.825 / 0.726 / 0.739 on the same observer; one arm's three seeds
   spanned 0.122 on the operator probe against a 0.084 group gap. Any claim below needs several seeds
   per arm and a second draw of whatever axis the conclusion rides on, or it is not a claim.

### Artifacts (verified 2026-09-02)

| What | Where |
|---|---|
| Trainer, newest generation | `D:\Astro-Dataset\n2n-smoke\v24\scripts\n2n_smoke.py` (identical to `v23`; 1,031 lines, 45 flags). The gate is `n2n_gate.py`, the metric suite `n2n_metrics.py`, beside it. |
| Shipping scripts | `D:\Astro-Dataset\n2n-smoke\ship\` (`n2n_export.py`, `n2n_dial.py`, `n2n_fixture.py`, `README.md`). They import `../v24/scripts` by relative path. |
| Run records | `D:\Astro-Dataset\n2n-smoke\v2 .. v24\README.md`, each with its `scripts/` snapshot and `run-vNN.ps1` carrying the pre-registered predictions. |
| Prepared caches + checkpoints | `C:\temp\tianwen-scratch\n2n-{ds,big,a52,b52,c21,d8,e8,f8,eval4}` (`tiles.f16` memmap + `meta.json` with train/val sessions BY NAME + `.pt`). `n2n-d8` is armD, the shipped arm; `n2n-eval4` is the four-observer eval cache. **`C:\tianwen-scratch` no longer exists**; every script default and hardcoded `EVAL` constant still says it. |
| Shipped weights | `src/TianWen.AI.Imaging/models/tianwen_denoise_osc_v19d.onnx` (3,268,149 bytes, plain git blob under a temporary `.gitattributes` LFS exemption with a 2026-09 revert note). Source `ship/n2n_v19d_s2_final.onnx`. |
| Parity fixture | `src/TianWen.Lib.Tests/Data/n2n-parity-fixture.json`, pinned by `N2nDenoiserTests.TheWholePipelineReproducesTorch` (5.07e-7 max abs). |
| Inference | `N2nDenoiser` + `N2nLinearRunner` (`src/TianWen.AI.Imaging/Onnx/`): fixed 256 px chunks, sigma computed in the graph, `RestoreLevel` per chunk, 16 px rim dropped, blend dial. **Feeds linear `[0,1]` pixels to a model trained on MTF-stretched tiles (fact 0 above); the runner's and the enhancer's XML docs assert the opposite and are wrong.** |
| Datasets | `2025-2026-organized` (51 sessions, 159,300 tiles, filters from headers, 7 pinned test sessions) is the pool. `2025-2026-darkscaled` (67 sessions, no filter) is what v15 to v24 trained on and is **not interchangeable** (different ids, different pool). Retained linear masters under each bake's `session-masters/`. |
| Stores | `<bake>/stats/psf-sessions.jsonl` (FWHM, per-channel profile, `MasterStrategy`), `skipped-sessions.jsonl`, `session-timings.jsonl`; `tiles-manifest.jsonl` (`NoiseMad` per tile, no FWHM column by design). |
| Environment | `torch==2.13.0+cu126` with `sm_61` in the arch list (re-verify with the one-liner in the roadmap before any run; the cu130 wheel cannot target the 1070). ONNX opset 17. **No `requirements.txt` exists anywhere**; the versions are recoverable only from the export JSON and the ONNX producer string. |

### The recipe that produced the shipped weights

```
python n2n_smoke.py --prepare --root D:\Astro-Dataset\2025-2026-darkscaled --cache <cache> \
    --train-from-list armD-8x45.txt --val-sessions 2 --cells-per-session 45 \
    --val-cells-per-session 120 --val-from-meta C:\temp\tianwen-scratch\n2n-ds\meta.json
python n2n_smoke.py --train --cache <cache> --loss l2 --upsample --mix-avg --cond \
    --band-loss 3 --band-scales "2,4 4,8" --base 32 --steps 4000 --gate-every 100 --seed <s> --out <name>.pt
```

Adam 2e-4, cosine to zero over 4,000 steps, batch 8, L2 on the tile minus a 16 px rim, DoG band loss
on 2-4 and 4-8 px at weight 3, one scalar conditioning plane (darkest-half MAD times 100), mixed
1v1/2v2/4v4 pairing, seed fixing init and draw, cuDNN deterministic. Gate every 100 steps: pass if
`spurious_over_floor <= 6`, `faint_amp >= 0.60`, `noise <= 0.82`, then minimise noise. About 11
minutes per seed on the GTX 1070 at 48 tiles/s.

## 1. Hypotheses

Each is stated with the arm that tests it, the prediction, and what result would close it. The
convention from v22 onward holds: predictions go into the run script header before the prepare, and
a result that needed a new explanation after the fact is a finding to record, not a conclusion to
draw.

**H0. Stretching the input the way the exporter did recovers most of the master-depth gap on real
frames, with no retraining.** The model trained on tiles at per-channel median 0.25 with a sub sigma
near 0.01; the runner hands it a linear master at median ~0.005 and sigma ~1e-4.
*Test, in C#, no GPU:* in `N2nLinearRunner`, apply `ChunkedNafnetRunner.ApplyInputStretch` to the
whole input before chunking (the same whole-frame per-channel MTF the exporter applied), run,
invert with the returned `OrigMin`/`Balances` exactly as `ChunkedNafnetRunner` does, then blend.
Re-run `N2nSeamProbe.ReportInputRescaleResponseOnARealMaster` (env `TIANWEN_N2N_SEAM_FITS`) on the
same seam-report master with the stretch in place of the k-rescale.
*Prediction:* MAD retention at full strength lands at or below the k = 124 peak (86 / 77 / 60 percent
R/G/B), the per-chunk `RestoreLevel` offsets shrink by an order of magnitude (the level prior is now
centred on the input's own level), no background pixel moves more than 10 MAD, and faint-star
amplitude on the master (not measured by the probe today; add it) sits above the k-rescale's.
*Kill:* retention no better than the verbatim path (90 / 91 / 83 percent, i.e. almost nothing
removed) or seams no quieter. Then the skew is real but harmless and the model is simply weak at
master depth, which H1 addresses.
*Consequences if it passes:* fix the runner (it becomes the stretched-domain sibling of
`ChunkedNafnetRunner` differing only in the fixed tile and the in-graph sigma), correct the four
places that say "linear" (`N2nLinearRunner`, `N2nDenoiser`, `ship/README.md`, run-log 1o), regenerate
the parity fixture through the new path, and re-read every "master-depth" verdict in 3b in that
light: the deployment gap measured in Python on stretched tiles stands, the extra loss measured in C#
was this.
*Result, E0.5, 2026-09-02: CONFIRMED.* Same master, one process, six arms (a probe-side stretch,
then k = 1, 8, 62, 124, 247 through the then-verbatim runner); the table is in section 9. MAD
retention through the stretch 87 / 77 / 64 percent (R/G/B) against the k = 124 peak's 86 / 77 / 60
and the verbatim path's 90 / 91 / 83: at the peak, not below it. Per-chunk level-restore |offset|
median 0.074 (max 0.117) in linear units on a sky of 0.0019 for the verbatim path, about 740 input
MADs; 0.0029 (max 0.046) on a level of 0.25 through the stretch. Background movers above 10 MAD: 0
per Mpx on every channel. Faint-star amplitude (the column added for this run; 3,396 stars):
stretch 0.732 / 0.854 / 0.927 / 0.933 by SNR bucket 8-15 / 15-30 / 30-100 / 100+ against k = 124's
0.656 / 0.843 / 0.947 / 0.983, so above the rescale at the faint end and below it at the bright end.
**Unpredicted:** the verbatim path kept 0.713 / 0.696 / 0.694 / 0.697, a flat 30 percent cut of every
star's peak at every brightness while removing a tenth of the noise, and k = 8 shows the same (0.67
to 0.70). That is the defect the skew shipped, and no metric in the programme had measured it. The
runner now stretches for itself (`N2nLinearRunner` step 0, `MtfUnstretch` in step 4, the blend in
the input's units after it); the probe's k = 1 row equals its probe-side stretch arm to 0.000, and
`N2nDenoiserTests.ALinearInputTakesTheExportersStretchAndComesBackInItsOwnUnits` pins the path.
After the fix k = 1, 8 and 62 all route through the stretch and agree on every column to within 0.01,
so the MTF removes the input scale as a variable; the rescale arms had shown it moving amplitude kept
by 0.3. **Open, carried to H1/E2:** the bright-end loss (0.93 kept at SNR 100+ against the rescale's
0.98) is inherent to inference in a stretched domain, where the inverse MTF steepens toward saturation
and magnifies a small stretched-domain error on a bright core; the AI4 NAFNets share it. Score every
later arm's star column in LINEAR units through the runner, never on the tiles alone.

**H1. Supervised synthetic injection beats N2N at deployment depth.** Train noise-to-clean with the
target a low-noise reference and the input that reference plus injected noise at a level drawn per
sample from a wide range. The target is always clean, so a quiet input still carries a full gradient,
and master depth is in-distribution because the range is synthesised.
*Arm S:* same net, same conditioning, same gate; targets are the retained session masters' tiles,
inputs those tiles plus electron-domain noise at sigma drawn log-uniform over [0.1x, 1.5x] of a
single sub's sigma for that session (read per tile from `NoiseMad` of that cell's sub tiles).
*Prediction:* on half-master inputs scored against the other half, faint amplitude kept at matched
0.85x noise rises from the N2N family's 0.68 to 0.79 band to at least 0.85, with fabrication on the
absolute bar unchanged (near the raw floor). On sub-depth inputs it may trail v19d slightly; that is
acceptable if the master-depth gain holds, since masters are what the enhancer is run on.
*Kill:* less than +0.03 over v19d at matched noise on half-master inputs across three seeds, or
fabrication on the absolute bar rising above the raw-sub floor.
*Caveat carried from 3b:* the reference is a master, so the "clean" target carries 0.152x of a sub's
noise and the model learns to leave that. Score against the OTHER half, never the master.

**H2. Noise SHAPE has to be injected, not only level.** Measured scene-free, every sub-derived regime
shares one band1/band0 power ratio (0.59 to 0.60) while a half-master reads 0.32: stacking through
the registration warp correlates the noise. White noise straight onto a master therefore has the
wrong shape for the deployed input.
*Arms:* S-white (noise added to the master tile) against S-warped (noise generated at sub resolution,
pushed through a resampling equivalent to the registrar's warp, then averaged as the master was).
*Prediction:* S-warped transfers to real half-master inputs with at least 0.02 more faint amplitude at
matched noise than S-white, and its residual band1/band0 on real inputs sits closer to 0.32.
*Kill:* the two arms overlap across three seeds. Then shape is not the lever and S-white is the
cheaper recipe. Note from 3b: R's residual is dominated by correlated stacking noise (adjacent-
difference MAD over MAD 0.51) while G and B sit near white (0.88, 1.05), so the shape story may hold
for red only; report the three channels separately.

**MEASURED 2026-09-03 (E1), and the arms are now calibrated rather than assumed.** The exporter's
`--measure-shape` computes band1/band0 scene-free (the difference of two frames of one scene) for the
injected draws AND for the bake's own real pairs with the SAME code, because 0.60 and 0.32 came from
another implementation on another domain and a number is only a target if it is measured the same
way. On `2025-2026-organized`, 64 pairs each:

| population | band1/band0 |
|---|---|
| real sub pairs | 0.786 |
| **real half-master pairs (the deployment regime)** | **0.463** |
| injected, S-white | 0.216 |
| injected, S-warped, bilinear alone | 0.328 |
| injected, S-warped, `--warp-sigma 0.5` | **0.460** |
| injected, S-warped, `--warp-sigma 0.8` | 0.765 |

Three things follow. **The ordering the hypothesis rests on replicates** (a master's noise is smoother
than a sub's) even though the absolute numbers differ from the Python probe's, so the phenomenon is
real and the two estimators simply disagree on scale. **White is nowhere near either regime** (0.216
against 0.463), which is the hypothesis' premise confirmed with a number. And **bilinear resampling
alone does not get there either** (0.328): the registrar's warp is not the only thing correlating a
real frame's noise, which on an OSC set is unsurprising once said out loud, since every frame has
been through a demosaic before anything else touches it. The `--warp-sigma` knob stands in for the
whole correlating chain, and at 0.5 px the injected shape lands on the deployment regime to within
one percent (0.460 against 0.463); 0.8 px reproduces a single sub (0.765 against 0.786).

*So the arms to run are S-white against S-warped AT `--warp-sigma 0.5`*, and the sigma is re-measured
per bake rather than carried as a constant: it is a property of that bake's demosaic and registration,
not of the code.

**H3. A wide level range weakens the per-channel level prior.** The shipped model drags an input
toward the sky level of its eight training sessions (median shift vs input level correlates at
-0.988), fixed at inference by `RestoreLevel`. A model that has seen many sky levels should learn
that the level is not a property to predict.
*Test:* re-run the 49-tile regression from `ship/README.md` on every new checkpoint.
*Prediction:* |correlation| below 0.5 for the synthetic arms. Whatever the outcome, `RestoreLevel`
stays: it is free (3.7e-9 on the per-channel std) and the guard belongs in the runner, not in a hope.

**H4. Narrowband training does not transfer to broadband.** A 3 nm frame has flux in one channel and
near-nothing in the others; a broadband frame has all three. The pool has never contained a
broadband session, so nothing measured so far says how v19d behaves on one.
*Data:* `C:\temp\astro\2026-08 SV545` (a working copy of the D: archive, two nights, SV545 camera,
`FILTER='IDAS LPS-D3'` on all 780 frames, 729 lights: 135 comet, 354 Lobster Nebula, 240 SMC; FLAT 51,
BIAS 200, DARK 60 at 60 s / -5 C / gain 1600 / offset 20, DARKFLAT 60). The comet night is a moving
target and must be excluded from any pair or reference (the armE lesson); the other two are ordinary
deep-sky sessions.
*Step 1, no training:* bake the two non-comet SV545 sessions with `tianwen dataset build`, then score
v19d and v17c on them as a fifth and sixth observer.
*Prediction:* faint amplitude at matched noise falls below the narrowband observers' 0.85 to 0.90 band
by more than the 0.04 seed spread, or the absolute-bar fabrication count rises above the raw floor.
Either is a transfer failure.
*Step 2, if step 1 fails:* arm B adds the two SV545 sessions to the training pool at the same cell
budget and is scored on a held-out SV545 session (the third night, or an interleaved half of one, is
the only option with two sessions; state that limit beside the number).
*Kill for the broadband programme:* step 1 passes. Then N3 resolves as "the model transfers", the
deployment target is restated as OSC (any filter), and no broadband acquisition is needed.

**H5. Capacity is still not the lever.** Base 48 (1.83 M) landed on base 32's (0.81 M) frontier on
N2N pairs. The programme doc's NAFNet-32 (29 M) has never been tried, and the reason to try it is
H1, not N2N: a supervised target with a full gradient is what a large net can use.
*Arm:* only if H1 passes. NAFNet-32 on the S recipe, one seed on a rented GPU first, three if the one
seed clears v19d by more than 0.04 at matched noise.
*Prediction:* at most +0.02 over the 0.81 M U-Net on the S recipe. A larger gain would be the first
evidence in this programme that capacity matters here.
*Kill:* the single-seed run lands within the U-Net's seed spread. Then the U-Net ships and the 29 M
plan is retired for OSC denoising.

**H6. The four-observer evaluation cannot resolve differences under about 0.04.** Task N11 asked
whether the eval is strong enough to select a model at all. The resolution is set by the number of
observers and seeds, not by more cells (the 64-cell slice ranks unstably; the 120-cell report
separates 3-seed arms cleanly; the session shift is systematic).
*Test:* bake older archive years (`docs/todo/imaging.md`, "Bake the older archive years") and the
SV545 set to reach at least eight observers, then re-score the existing checkpoints.
*Prediction:* the ranking of v19d / v17c / armF on the four current observers survives on the wider
set, but the margins shrink toward the seed spread.
*Consequence either way:* every shipping decision from this campaign states its observer count and
seed spread beside the number.

**H7 (deferred). Mono.** The two ASI1600MM sets are poor and excluded by `drop_foreign_channel_sessions`;
the user intends to shoot true narrowband mono later. Nothing to test until that data exists.

## 2. Data

### The pool

The organized bake is the pool: 51 sessions, every session id carrying its filter, 7 test sessions
pinned in `test-sessions.txt` by a stable hash bucket. Val is pinned BY NAME through
`--val-from-meta`; the proof the pin worked is the printed raw-sub floor matching the previous run to
the decimal, never the config. **The armD/armE/armF caches were built from the darkscaled bake and
cannot be reused against the organized one** (different ids, different pool); any arm that must be
compared against v19d is scored on the `n2n-eval4` observers, which are darkscaled sessions, or
v19d is re-scored on the new observers. Mixing the two bakes in one table is the cross-bake error
that produced a wrong fabrication win once (2.4, "my measurement error").

### What the synthetic arms need that the tiles do not hold

The stored tiles are MTF-stretched (fact 0: per-channel median 0.25, the `ApplyInputStretch`
contract, per-frame statistics). Injecting photon noise is only correct in the LINEAR domain, and a
stretch is not invertible per tile (its parameters are whole-frame, per channel, and are not in the
manifest). So the injection exporter must work from the **retained linear masters**
(`<bake>/session-masters/`, 51 files in the organized bake) and apply the identical export path
afterwards: `ToUnitRange` then `ApplyInputStretch` on the whole degraded frame, then cut the cells.
This is the same shape the deconvolver exporter needs
([deconvolver-training.md](deconvolver-training.md) section 2), so build one exporter with two
degradation modes (noise injection; PSF blur plus noise) rather than two exporters.

Note the parity test cannot see a domain error. `n2n_fixture.py` generates its plate at a background
of 0.26 (a stretched level) and both languages run the same bytes through the same graph, so it pins
graph equivalence and stitching, not whether the runner hands the graph the domain it trained on.
H0 adds the measurement that does.

Noise model: electron domain, the `SyntheticPlanetRenderer` calibration (shot noise Poisson in
electrons, read noise in quadrature, `aduPerElectron = maxAdu / fullWell`), with per-camera gain and
full well from headers. The ASI585's per-channel ADU scale is unresolved
([filter-inference.md](filter-inference.md) section 8); keep it out of the injection arms until it is.

For H2 the noise is generated at sub scale, passed through a resampling that stands in for the
registrar's warp (a sub-pixel bilinear or the drizzle kernel, per `MasterStrategy`), and averaged over
N realisations, N drawn from the session's registered sub count. Measure the resulting band1/band0
against the 0.32 the real halves show before training on it; an injector that does not reproduce the
shape is not testing H2.

### Level range

Per tile, read the cell's sub `NoiseMad` values from the manifest, take their median as "one sub"
for that cell, and draw the injected sigma log-uniform over [0.1, 1.5] of it. The bottom of that range
is below master depth (0.152x) on purpose, so the deployed level is interior, not an edge.

**CORRECTED 2026-09-03: 0.1 is not below master depth on this pool, and a fixed bottom cannot be.**
Master depth is 1/sqrt(StackedFrames) and the organized bake runs from 15 frames to 257, so it spans
0.258 down to 0.062 with a median of 0.089: the 0.1 floor sits ABOVE the master depth of **34 of the
51 sessions**, and the conditioning plane at inference would read a level below everything the model
had seen. That is H0's domain skew one layer up, so the floor is now derived per session
(`MasterDepthFraction`, default half the session's own master depth, clamped by the old 0.1) and the
row records `MasterDepth` so a reader can check the range covered it. The 0.152x the paragraph above
quotes was reasoned on a 43-frame master, which is the darkscaled pool, not this one. Pinned by
`TheDeploymentDepthIsInteriorToTheInjectedRange` at 15, 64 and 257 frames. The
conditioning plane is computed from the noisy tile inside the graph exactly as today, so the model
still reads its own input's noise; the injected level is never handed to it as a label (the
tile-border asymmetry lesson in another form: never condition on a number inference cannot measure).

## 3. Metrics and gates

Settled by the campaign; restated so no run re-derives them.

- **Select on faint-star amplitude kept at MATCHED residual noise** (`n2n_bars.py`, blend curve
  interpolated at 0.90 / 0.85 / 0.80x), SNR 8-15 bucket, on observers no arm selected on. It transfers
  6:1 across sessions. Report the seed spread beside every mean.
- **Fabrication on the ABSOLUTE bar** (input's own MAD) is a safety check: anything above the raw-sub
  floor fails. The relative-bar count is a ranker only and is never reported as "invented sources".
- **Structure at matched noise**, 1.5-4 px and 4-16 px bands (`n2n_structfrontier.py`), so a star metric
  cannot be won by ironing nebulosity flat. Nebulosity has held at 0.97 to 1.00 for every arm so far.
- **Level bias per channel** (H3) before any checkpoint is exported.
- **Photometric integrity** (section 7 of the programme doc): signed flux bias under 0.5 percent per
  SNR band above 20 on held-out subs, measured with `PhotometricRepeatability.Compare`. Not yet run on
  any denoiser checkpoint; it becomes a release gate with this campaign.
- **Residual correlation is reported, never gated** (session shift twice the model spread). PSNR is
  never computed for selection.
- **Truth masks are depth-specific.** The master-at-8-MAD star mask is calibrated for sub-depth inputs
  and faked two results at half depth; re-derive or sweep it for any measurement on halves or masters.
- **The gate orders steps within one run on one session.** Its 6.0 fabrication constant is
  session-calibrated; `--gate-observe` (default on) prints the second session so the "probed session is
  the stricter one" assumption is checkable. Never gate on both.
- **Blend grid at 0.05 or finer** before calling a checkpoint unable to reach a noise level.

## 4. Experiments, in order

| Step | What | Cost | Decides |
|---|---|---|---|
| **E0** | **DONE 2026-09-02, PASSED.** Repo-ise the trainer (roadmap section 2): `training/denoise/` with `n2n_smoke.py`, `n2n_gate.py`, `n2n_metrics.py`, `n2n_bars.py`, `n2n_rotate.py`, `n2n_structfrontier.py`, `ship/*`; a pinned `requirements.txt`; every `C:\tianwen-scratch` default and `EVAL` constant becomes a required argument or reads `C:\temp\tianwen-scratch`. **Acceptance: re-run v19d seed 2 from the `n2n-d8` cache and reproduce `n2n_v19d_s2_final.pt` bit-identically** (the deterministic mode was verified to do this on the 1070). Anything else and the port changed the trainer. *Result: `repro-v19d.ps1` re-ran the recipe in 12.3 min; the log matches the 2026-08-16 run at every printed loss and gate figure (selected step 1500, score 0.709; the final gate row identical to the digit), and both checkpoints reproduce all 813,251 parameters bit for bit. The script's first verdict was DIFFERENT because it hashed the files: `torch.save` names every archive member after the output stem and pickles the trainer's metadata dict, which now carries `pair_time`, so identical weights under another name are a different file by construction; re-saving the reference with that key under the repro's name reproduced the repro's hash exactly. The comparison is tensor for tensor now (`n2n_ckpt_equal.py`) and `-CompareOnly` re-judges without retraining.* | done, 12 min GPU | Whether the campaign starts from a controlled instrument: it does |
| **E0.5** | **DONE 2026-09-02, H0 confirmed** (result under H0, table in section 9). Stretch-run-invert in `N2nLinearRunner`; the seam probe re-run on the seam-report master with a faint-star amplitude column; the four "linear" claims corrected (runner, enhancer, ship README, run-log 1o). The parity fixture was regenerated with one dead pixel in the plate so the auto-detect reads it as in band and torch and the runner see the same bytes (the stretch itself is pinned in C#, by equality with the by-hand route, not by a Python MTF). The 3b real-master table is superseded by section 9. Ran before E0 because it costs nothing E0 provides. | done, 3 h | H0 |
| **E1** | **DONE 2026-09-03.** `tianwen dataset degrade` (`DatasetDegradationExporter`, shared with the deconvolver's E2): retained linear masters, cells and level anchor taken from the P0 manifest, both sides stretched with the TARGET's parameters, tiles written P0-shaped so `--prepare` reads them unchanged (slot 0 clean, slots 1..8 draws). Shape measured against the bake's own real pairs and the warped arm calibrated to the deployment regime (see H2). `n2n_smoke.py --synthetic` completes it on the Python side: an injected draw against the clean target in slot 0, exclusive of the N2N regimes so the arm answers H1, and refused on a cache of real subs (`--prepare` records whether the sub slots hold `deg*` frames, read off the tiles rather than taken from a flag). Depth variety comes from the injection itself, since every draw carries its own log-uniform depth, so `--mix-avg` has nothing left to add | 1 to 2 days of code, minutes to bake | Whether H2 can be tested at all |
| **E2** | Arm S-white and S-warped, three seeds each, controls v19d s0-2 and v17c s0-2 re-scored on the same slices. Score on the four `eval4` observers at sub depth AND on half-master inputs against the other half. Post a labelled comparison image. | 6 x 11 min GPU, then scoring | H1, H2 |
| **E3** | Level-prior regression on every E2 checkpoint. | minutes | H3 |
| **E4** | Bake the two non-comet SV545 sessions (`run-dataset-bake.ps1`, per subtree, never the SV545 root); score every checkpoint on them. | 1 to 2 h bake | H4 step 1, adds observers for H6 |
| **E5** | If H4 fails: arm B (pool + SV545), three seeds. | 3 x 11 min | H4 step 2 |
| **E6** | If H1 passes: NAFNet-32 on the S recipe, one seed, rented GPU (RunPod 4090, per-second billing) or the internal T4 pool; AMP on there, off locally. | ~$15 to 50 | H5 |
| **E7** | Export the winner with `n2n_export.py` (baked sigma, fixed 256, opset 17), parity to torch under 2e-7, regenerate the parity fixture, replace the in-repo weights, **execute the LFS revert in `.gitattributes`**, re-measure the dial (blend stays unless measured otherwise). | half a day | Ships v2 |

Every arm: pre-register predictions in the run script header; three seeds; one prepared cache per
arm, never edited between runs; launch multi-hour jobs detached (`Start-Process`), never through the
session's background shell, and never read a file a running job appends to.

## 5. Export and integration

Unchanged from N4 unless a hypothesis forces it: the baked graph with sigma computed per 256 px tile
inside it and `strength` pinned at 1.0; `N2nLinearRunner` chunking, rim drop and per-chunk
`RestoreLevel`; the blend as the only user dial via `EnhanceTuning.DenoiseStrength`; opt-in
`AddTianWenN2nDenoiser` and `--ai-backend n2n`; Auto's rescue tier fires only when the SAS AI4
weights are absent. A `<model>.contract.json` (dataset manifest SHA, git commit, ONNX SHA, tensor
conventions) ships beside v2 and is asserted at load, the gate-and-refuse pattern from the roadmap.

**The comparison nobody ran stays un-run in the automated loop.** Whether the in-house model should
become Auto's preferred OSC denoiser over the SAS AI4 model is a benchmark against a third-party
model's output, which the RC-Astro EULA forbids for RC and which the unverified SAS licence puts under
the same default. It can only be a human side-by-side outside the loop, on the user's own images,
and the decision to promote is the user's. Record it as such rather than as a pending metric.

## 6. Invariants carried from the campaign

- Seed init and draw, cuDNN deterministic; verify two runs of one seed produce identical tensors.
- Several seeds per arm; rotate the axis the conclusion rides on (training set, observer, seed) and
  measure the noise floor of the comparison before explaining a difference.
- Compare at matched noise, never at matched step; a shorter schedule is the same curve stopped early.
- Val by name; check the printed floor. Two runs share one prepared cache; the trainer is not edited
  while a chain is in flight (each run re-reads the file; snapshot scripts per run as before).
- A default that points at a sibling dataset is worse than a required argument.
- Mono sessions stay excluded. Moving targets (comets) stay out of every pair and reference.
- Never write to `D:\Astro-Pics`; `C:\temp\astro` is a working copy and may be amended in place.
- Post a labelled comparison image when an experiment concludes, not a path to one.

## 7. Phasing

| Phase | Deliverable | Exit |
|---|---|---|
| D0 | **DONE 2026-09-02.** Trainer in the repo with a requirements pin and no machine paths; both shipped v19d seed-2 checkpoints reproduce bit for bit, judged tensor for tensor | E0 passed |
| D1 | Injection exporter (white + warped) over retained masters; shape measured | E1 |
| D2 | H1/H2/H3 answered with three seeds per arm and a posted comparison | E2, E3 |
| D3 | Broadband transfer answered; N3 restated in the programme doc and in `N2nDenoiser`'s XML doc | E4, E5 |
| D4 | Capacity answered or retired | E6 |
| D5 | v2 exported, parity-pinned, LFS exemption reverted, contract JSON asserted at load, photometric gate run | E7 |

## 8. Open questions

- **How much of 3b's real-frame shortfall was the runner?** Answered by E0.5 (section 9): the
  whole of the gap between the verbatim path and the k = 124 peak, plus a 30 percent flat star
  suppression 3b never measured. The 3b k = 124 row stands as measured; its k = 1 row describes the
  retired path. What remains at master depth is the Python-side gap (H1).
- **What does the injector do about hot pixels and cosmic rays?** A rejecting integrator removes them
  from the master; a sub carries them. Injecting them (salt at the session's hot-pixel rate from the
  dark) is what makes the synthetic sub-depth input honest; omitting them is what makes the model
  ignore them. Decide after E2 shows whether sub-depth performance trails.
- **Reference sharpness for the S arms:** drizzled masters are sharper (red -22 percent) and the
  strategy is mixed 33 drizzle / 7 staged inside the 3 nm group; whether the model should see both or
  one is a confound to state per arm.
- **How many broadband nights are enough** to say anything about H4 step 2 with two SV545 sessions
  in hand? Probably none; step 2 is a pilot until a third broadband night exists.

## 9. Run log

### E0.5, 2026-09-02: H0 on the seam-report master

`N2nSeamProbe.ReportInputRescaleResponseOnARealMaster` on
`C:\Users\SebastianGodelet\Desktop\163x150s Bubble Nebula SV220.fit.fz` (3840 x 2160 x 3, input
medians 0.0019 / 0.0019 / 0.0020, MADs 1.9e-4 / 9.5e-5 / 9.7e-5, trainer sigma 8.09e-5, honest
k = 123.6), one process, the verbatim runner, overlap 64. The stretch arm is the exporter's
`ApplyInputStretch` around the runner (stretched medians 0.2500 on every channel) followed by
`MtfUnstretch`; every arm is scored on a linear-units plane. Stars: 3,396 input-luminance peaks at
or above 8 darkest-half MADs over a 21 x 21 ring, 1,355 / 872 / 714 / 455 in the SNR buckets 8-15 /
15-30 / 30-100 / 100+. The level-restore column is the median and max |offset| over the 756
(channel, chunk) pairs in the units the net saw; the k rows are in k-scaled units.

| arm | MAD kept R/G/B | adjacent-diff MAD kept | seams median (loud/38) | bg movers >10 MAD /Mpx | level-restore median / max | amp kept 8-15 / 15-30 / 30-100 / 100+ | detect 8-15 |
|---|---|---|---|---|---|---|---|
| **stretch** (ships now) | **87 / 77 / 64 %** | 61 / 81 / 73 % | 0.9 / 0.9 / 1.0 (3 / 7 / 2) | 0 / 0 / 0 | **0.0029 / 0.046** on a level of 0.25 | **0.732 / 0.854 / 0.927 / 0.933** | 98 % |
| k = 1, verbatim (retired) | 90 / 91 / 83 % | 82 / 92 / 90 % | 0.8 / 1.0 / 1.2 (6 / 6 / 6) | 0 / 0 / 1 | **0.074 / 0.117** on a sky of 0.0019 | **0.713 / 0.696 / 0.694 / 0.697** | 100 % |
| k = 8 | 91 / 91 / 83 % | 81 / 91 / 89 % | 0.8 / 1.1 / 1.0 (4 / 8 / 9) | 0 / 0 / 2 | 0.076 / 0.111 | 0.699 / 0.685 / 0.683 / 0.672 | 100 % |
| k = 62 | 89 / 88 / 69 % | 69 / 87 / 78 % | 0.9 / 0.8 / 1.0 (1 / 3 / 3) | 0 / 0 / 0 | 0.060 / 0.081 | 0.531 / 0.510 / 0.472 / 0.887 | 82 % |
| k = 124 (3b's peak) | 86 / 77 / 60 % | 61 / 81 / 71 % | 1.0 / 0.7 / 0.9 (1 / 3 / 3) | 0 / 0 / 0 | 0.0043 / 0.058 | 0.656 / 0.843 / 0.947 / 0.983 | 94 % |
| k = 247 | 93 / 91 / 84 % | 80 / 90 / 85 % | 1.0 / 0.8 / 1.0 (1 / 3 / 3) | 0 / 0 / 0 | 0.022 / 0.087 | 0.944 / 0.975 / 0.990 / 0.991 | 100 % |

Readings, in order of weight:

1. **The stretch lands on the rescale's peak for noise and beats it where it matters for stars.**
   Same MAD removal as k = 124 within 4 points, 0.08 more faint-star amplitude kept at SNR 8-15,
   equal at 15-30, and 0.02 / 0.05 less at 30-100 / 100+. The bright-end loss is the inverse MTF
   steepening toward saturation, which the AI4 NAFNets share; it is a property of stretched-domain
   inference, not of this net, and every later arm's star column has to be read in linear units.
2. **The verbatim path's defect was photometric, not a weak denoise.** It cut every star's peak by
   30 percent at every SNR while removing a tenth of the noise, and its per-chunk level drag was 39
   times the sky. Neither number existed before this run: the probe had no star column and the runner
   did not report its offsets. Both do now (`N2nRunResult.LevelOffsetMedianAbs` / `MaxAbs` are in the
   denoiser's log line).
3. **The k rows are one family.** Amplitude kept walks from 0.70 (k = 1, 8) through 0.47 to 0.53
   (k = 62) to the 0.66 / 0.84 / 0.95 / 0.98 profile at k = 124 and 0.94 to 0.99 at k = 247, where
   the net barely acts. The conditioning plane interpolates along the level axis, as 3b read it, and
   the honest k is the one place the rescale is competitive; the stretch reaches the same place
   without a hand-picked scale.
4. **After the fix the scale is gone as a variable.** Re-run with the runner stretching for itself:
   k = 1 equals the probe-side stretch arm to 0.000; k = 8 and k = 62 (medians 0.015 and 0.118, both
   under the 0.125 auto-detect) route through the stretch too and agree with k = 1 on every column
   to within 0.01; k = 124 and 247 (medians above the threshold) are fed as they are and reproduce
   their rows above exactly.
5. **Cost.** The stretch adds 1.2 s and the inverse plus blend 0.34 s to a 4.3 s run on this frame
   (CPU), 5.9 s total at 4.2 Mp/s.

Artifacts: `scratchpad/h0/before.log`, `before-telemetry.log`, `after.log`, crops under
`h0/before/` and `h0/after/` (`rescale-stretch.png`, `rescale-k*.png`, `rescale-input.png`, channel 1,
896 x 384 at y = 1200, ticks at every seam). The crops are session scratch, not committed.

### E2, 2026-09-03: H1 and H3 on the injection arms, against a matched control

Six checkpoints of this campaign (S-white s0-2, and a matched N2N control s0-2) plus the three v19d
controls, all scored in ONE run of `n2n_halfscore.py` on the `eval4` cache: 192 val cells carrying a
half-master pair, 56,216 master-detected stars, four held-out observers. Scoring the old numbers
again rather than quoting them is deliberate; a table assembled from two runs of two versions of a
script is not a comparison.

**The matched control is new and it earned its keep immediately.** The v19d controls were trained on
the DARKSCALED pool and these arms on ORGANIZED, so any difference between them confounds the regime
under test with the pool draw, and v24 had already measured that draw carrying more variance than
most effects chased here. The control arm is N2N on the SAME pool, the SAME eight sessions, the SAME
cells and the SAME recipe, differing only in `--mix-avg` where the arms have `--synthetic`. It landed
a WORSE trade than v19d at every strength, so comparing the arms against v19d alone would have
flattered them.

#### The reading that was about to be wrong

Exchange rate (amplitude spent per unit of noise removed) put white at 1.54 to 1.75 and the controls
at 1.39 to 1.93: overlap, which reads as H1's kill. The conclusion was right and the reason was not.
The arms were not doing the same amount of work (white removes 6 to 13 percent of the noise, the
controls 18 to 35), and within BOTH control families the rate gets BETTER the harder the model
denoises (control 1.93 to 1.57, v19d 1.67 to 0.90). A rate that varies with intensity ranks the
intensities. `--blend` fixes it: blending an output back toward its input walks each model down its
own curve, which is verbatim what the shipped runner's strength dial does, so every model has a value
at the same noise removed.

#### At matched noise removed

Amplitude spent / colour cast, both at that strength. Lower is better on each.

| model | at 4 % removed | at 6 % | at 10 % | max reach |
|---|---|---|---|---|
| white s0 / s1 / s2 | 6.3 / 1.0 · 5.7 / 0.5 · 5.5 / 1.9 | - · 8.9 / 0.8 · 8.5 / 2.9 | - · 15.4 / 1.5 · 15.3 / 5.3 | 5.9 / 12.8 / 10.1 % |
| control s0 / s1 / s2 | 6.3 / 1.0 · 5.5 / 1.0 · 4.9 / 1.1 | 9.7 / 1.6 · 8.2 / 1.5 · 7.4 / 1.7 | 16.8 / 2.7 · 14.1 / 2.6 · 12.5 / 2.9 | 21.8 / 31.8 / 35.2 % |
| v19d s0 / s1 / s2 | 5.4 / 1.4 · 4.4 / 2.1 · 3.2 / 1.5 | 8.3 / 2.1 · 6.8 / 3.2 · 4.9 / 2.3 | 14.7 / 3.7 · 11.9 / 5.5 · 8.4 / 3.9 | 18.0 / 21.9 / 25.8 % |

#### One more confound: the controls were CHOSEN and the arms were not

The table above is not like for like. All three v19d checkpoints are gate-selected mid-schedule
(steps 2200, 900, 1500) and control s0 at step 3100, while every white checkpoint is final weights
because no probe passed every gate. A selected checkpoint is picked to be good; an unselected one is
wherever the schedule ended. Re-scored with every family at FINAL weights, at 10 percent removed:

| family | s0 | s1 | s2 |
|---|---|---|---|
| white (final, as before) | - | 15.4 | 15.3 |
| control, final | 17.5 | 14.1 | 12.5 |
| v19d, final | 13.7 | 14.2 | 13.9 |
| v19d, gate-selected (the row above's source) | 14.7 | 11.9 | **8.4** |

**The gate is doing real work, and it is not the regime.** v19d's three finals converge to 13.7 to
14.2 where its gate-selected checkpoints span 8.4 to 14.7: the 8.4 that made v19d look dominant is
selection, not training. This is incidental evidence that the gate earns its keep, and it is exactly
the kind of difference that a table assembled without asking how each checkpoint was chosen reports
as a regime effect.

**H1: KILLED for S-white, on the "do not separate" clause rather than by trailing.** Like for like at
final weights, white (15.3 to 15.4 at 10 percent removed) sits inside the matched control's band
(12.5 to 17.5) and just above v19d's tight one (13.7 to 14.2). That is well short of the predicted
+0.03 improvement and is the pre-registered kill. **One asymmetry survives selection and is not a
tie:** white cannot reach past 12.8 percent noise removal even at full strength, where the control
families reach 21 to 35. The supervised arm is not trading worse so much as it is weaker, which is a
different failure and points at a different suspect.

**H3: NOT SUPPORTED, and the way it failed is the finding.** Per-channel level bias at each model's
own full strength separated the arms cleanly and with no overlap across three seeds each: cast (the
max-minus-min differential across R, G and B, in units of the raw half's noise) read 1.67 / 1.98 /
5.37 for white against 6.78 to 12.24 for the control and 7.41 to 13.86 for v19d, with every N2N model
pushing blue up by 5.5 to 12.9. It is the same confound. Matched at 10 percent removed the same nine
models overlap (white 1.5 and 5.3, control 2.6 to 2.9, v19d 3.7 to 5.5) and the worst of all nine is a
white seed. At final weights it goes mildly the other way: v19d reads 1.8 to 2.4 at 10 percent removed
against white's 1.5 and 5.3. The level prior is a property of the strength, not of the training regime,
and the direction that remains is not the predicted one. The bias table
stays in the script because the per-channel breakdown shows the direction, but it now says on its face
that it is not a ranking.

#### What the 1:1 comparison showed that no column did

`compare-e2-h1.png` (raw half A, white s1, control s2, v19d s2, half B, master; one stretch per row
taken from the raw column; 1:1, no resample). White s1 is nearly indistinguishable from its input.
Both N2N models visibly quiet the background and thin the faint stars, and in the green-cast cell both
pull the sky toward neutral grey while white keeps the cast. That last observation is what prompted
measuring level bias at all, and then measuring it at matched strength, which is what turned it from a
finding into an artefact. The image is what raised the question; it is not what answered it.

#### H2, with the warped arm in

The two arms are a clean one-variable contrast, verified rather than assumed: identical export seed, so
48,960 rows each with identical keys, **zero** rows differing in drawn depth or in seed, and `Shape`
the only field that differs; then identical prepared caches (600 cells, same keys, same splits). All
three warped seeds failed the gate exactly as all three white seeds did, so both arms are final
weights and there is no selection asymmetry between them either.

| amplitude spent at | 4 % | 6 % | 10 % | max reach |
|---|---|---|---|---|
| white s0 / s1 / s2 | 6.3 / 5.7 / 5.5 | - / 8.9 / 8.6 | - / 15.5 / 15.3 | 5.9 / 12.8 / 10.1 % |
| warped s0 / s1 / s2 | 6.2 / 5.8 / 4.4 | 9.5 / 8.9 / 6.7 | 16.6 / 14.9 / 11.5 | 10.3 / 19.7 / 15.8 % |
| control, final | 6.6 / 5.5 / 4.9 | 10.1 / 8.3 / 7.4 | 17.5 / 14.2 / 12.5 | 21.0 / 31.8 / 35.2 % |
| v19d, final | 5.3 / 5.5 / 5.4 | 7.9 / 8.3 / 8.1 | 13.7 / 14.3 / 13.9 | 24.5 / 28.7 / 26.6 % |

**H2: KILLED.** Warped beats white by about 1.1 points of amplitude at 10 percent removed against a
predicted 2 or more, and `warped_s0` is worse than both white seeds. The arms overlap across three
seeds, which is the pre-registered kill.

**H2's second clause was mis-specified, and the direction is the finding.** It predicted warped's
residual band1/band0 would sit CLOSER to the real 0.463. At matched strength it sits further: white
leaves 0.339 to 0.370, warped 0.215 to 0.258, the controls 0.240 to 0.264. That is mechanically right
and the proxy was wrong -- a model that correctly recognises correlated noise REMOVES it, so it leaves
a whiter residual. "Residual still looks like the input's noise" measures how little was done, not how
well. Warped's residual being indistinguishable from the controls' is the honest sign it learned what
they learned. Do not re-use this clause; if a future run wants a shape claim, it needs a target derived
from what a correct denoiser should leave, not from the input.

#### The finding that outranks all three hypotheses

Twelve checkpoints, four training regimes, three pools, two injected noise shapes. Decomposed at each
matched point, over the four regimes' three seeds each:

| at | grand mean | between-regime sd | within-regime (seed) sd | ratio |
|---|---|---|---|---|
| 4 % | 5.59 | 0.20 | 0.58 | 0.34 |
| 6 % | 8.43 | 0.28 | 0.82 | 0.35 |
| 10 % | 14.54 | 0.61 | 1.40 | 0.44 |

**The seed moves the matched trade two to three times as much as the training regime does**, and the
best three models at every matched point are `warped_s2`, `control_s2` and `v19d_s0`: one from each of
three different regimes. The ranking is by seed, not by recipe.

**Two claims in the sections above are therefore weaker than they were written, and both are corrected
here rather than edited away.** "Shape bought about 60 percent more reach" (9.6 to 15.3 percent mean)
is t = 1.7, p about 0.16 against a within-arm sd of 4.1 at n = 3: suggestive, not established. Warped's
1.1-point trade edge over white is the same story. Neither is a result at this sample size.

**The design was underpowered for every question it asked.** At sd 1.4, detecting a 1-point regime
effect at 80 percent power needs about **31 seeds per arm** (~6 h GPU each); three seeds can only
resolve about 3 points, and nothing in play is that large. Any future arm on this axis needs seeds
rather than variations, and the first thing worth trying is anything that COLLAPSES the seed spread,
because that is what makes every later experiment readable.

**E2b, pre-registered as triggering on exactly this overlap, is NOT run as designed.** Not because the
result disappointed, but because it is another three-seed arm and three seeds cannot read a one-point
effect. The pre-registration's own fear -- that an overlap could not separate "shape does not matter"
from "this architecture cannot express shape" -- is answered by other means: shape moved reach and
moved the residual, so the architecture expresses it. Shape changes what the model DOES and not the
trade it OFFERS. If E2b is ever run, it runs at the power the question needs, on reach, not the trade.

### 2026-09-04: the metric's "faint stars" are largely NOT stars on a nebula field

Raised as a question -- might some of the faint stars be detail we misread as stars? -- and it is the
most consequential thing this campaign has measured, because it changes what every number in it means.

**`star_table` has no star test.** It accepts any 5x5 local maximum above median + 8 MAD. No PSF check,
no roundness, no catalogue. On a pool that is 100 percent OSC narrowband emission nebulae that also
admits knots, filament crests and dust-lane edges.

**A shape proxy could not settle it.** Classifying peaks by second-moment FWHM and ellipticity against
the population's OWN median said 96.9 percent were "PSF-like", which is close to worthless: the test
is self-referential, and had the population been mostly knots the median would be a knot and knots
would pass. It does establish one useful negative -- the gate1500-vs-shipped ranking is identical on
every subset -- but it cannot answer the question asked.

**An external catalogue can.** Plate-solve the master (`tianwen solve --update-fits`), project every
detected peak, and ask Gaia DR3 whether a star is there. Two fields, both directions, coincidence floor
quoted because a crowded field matches by chance:

| field | peaks | matched | coincidence | expected star-peaks |
|---|---|---|---|---|
| M33 (galactic lat -31, sparse) | 1,754 | **99.6 %** | 10.3 % | ~100 % |
| Great Orion (emission nebula) | 11,406 | **30.0 %** | 8.1 % | 33 % |

**So on a nebula field about two thirds of the metric's "faint stars" have no star there**, while on a
sparse field essentially all of them do. It is not noise (an 8-MAD peak over 9.3 Mpx is statistically
impossible from Gaussian noise) and it is not catalogue truncation: the offline ASTAP D50 extract gave
68 percent and untruncated Vizier, 21 percent deeper, gives 67 percent.

**What it does and does not invalidate.** Every model was scored on the SAME peaks, so every ranking
stands, `gate1500` over `shipped4000` included. What changes is the reading: "the shipped model spends
32 percent of faint-star amplitude" is mostly "it flattens a third of the faint nebular structure",
which for an image enhancer is the more serious charge, not a lesser one. It also explains the
metric's oddest behaviour, flagged in `n2n_halfscore.py`'s own caveat as the direction flipping a third
time: a model that reveals real nebulosity below the 8-MAD truth bar is scored as FABRICATING it.

**Tooling, in `training/denoise/`.** `gaia_vizier.py` (TAP/ADQL, disk-cached per field),
`gaia_d50.py` (offline ASTAP extract), `peak_audit.py` (both directions plus the coincidence floor).
Three traps found while building it, each of which produced a plausible wrong answer first:

- **ADQL `BOX` width is COORDINATE degrees of RA at CDS, not true angle.** Passing true angle narrows
  the box by 1/cos(dec) and drops peaks near the RA edges: M33 read 90.0 percent instead of 99.6. The
  tell was that a DEEPER catalogue matched FEWER, which is impossible; at Orion's -5.4 deg it would
  have been invisible, and it would have been silently wrong on every northern field.
- **A match rate is unreadable without its coincidence floor.** At Orion's peak density, 8.1 percent of
  peaks match something by chance; the faint buckets of the reverse direction sit at exactly that.
- **D50 is not a substitute where the image is deep.** It caps at 5000 stars/sqr(degree), and its
  bright-end retention against Vizier (23 to 40 percent at BP 13 to 15) is not what a brightest-first
  cut produces. Unexplained; the decode is validated against the format's own Sirius vector, the
  traversal is not. Offline fallback only.

**Next, and it is a change to the metric rather than to a model:** report amplitude on Gaia-CONFIRMED
stars separately from amplitude on unmatched compact detail. Both matter and a denoiser should scrub
neither, but conflating them is what made this unreadable. It needs one WCS per eval session, which is
a one-off solve of six masters.
