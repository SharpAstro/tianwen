# TODO -- Guider

Part of the TianWen TODO set. See [TODO.md](../../TODO.md) for the index and the active/high-priority list.

## Guider

- [ ] `appState` parameter should probably be an enum (`GuiderStateChangedEventArgs.cs:34`)
- [ ] Decide whether to ship a pretrained neural guide model (or train from scratch per-mount)
- [ ] Guider profile should use profile id (not name) for model persistence and lookup
- [ ] Write guide logs (CSV) into folder next to model weights for post-session analysis
- [ ] Investigate if increasing neural model parameters (wider/deeper MLP) improves guide accuracy
- [ ] Investigate improving pretrained model with real-time mount telemetry data
- [x] Built-in guider receives same mount driver instance via `IMountDependentGuider` wiring in `SessionFactory`
- [x] Support ST-4 guide port as guiding output: `PulseGuideRouter` + `PulseGuideSource` (`?pulseGuideSource=Auto|Camera|Mount` on the guider URI) routes corrections through `ICameraDriver.StartPulseGuideAsync`. `Auto` prefers the mount (commit 8a08691): camera `CanPulseGuide` only proves an ST-4 *socket* exists (`HasST4Port`), not that a cable is connected
- [ ] Support snap/shutter-release port for external camera triggering
- [ ] MetaGuide support: as an external guider (MetaMonitor UDP telemetry listener, like `OpenPHD2GuiderDriver` for PHD2) and/or adopting its video/lucky-guiding technique internally. See [docs/plans/video-guiding.md](../plans/video-guiding.md).
- [ ] Finish the fake disturbance-model refactor (see `docs/architecture/fake-disturbance-model.md`). DONE so far: the neural-vs-P comparison (`GuideLoopTests`) migrated onto the coupling harness via `SetupCoupledGuidedMount` (real ~99-sample runs, not the 2-sample vacuity). REMAINING: (a) the shared `IDisturbanceTerm` / `MountDisturbanceModel` abstraction (steps 1-5) so PE/polar/flexure/wind/seeing are one composable model instead of three overlapping ones; (b) migrate the other `SetupGuidedMount`-based tests (`GivenWindGusts…`, `GivenCableSnag…`, `GivenCombinedDisturbances…`) off the sidereal-contaminated hand-rolled renderer; (c) add wind + seeing knobs to the coupling path (step 7). Also: the comparison currently only exercises ~5-10% neural blend (BlendRampInFrames=480 vs 100 iterations) on a gentle, well-correctable disturbance, so neural ≈ P; a discriminating variant (harder regime + fuller blend, or a model trained on outcomes not P-imitation) would make the guardrail bite.


### Filed 2026-08-30 from the 2026-08-29 session notes (found in passing, neither fixed)

- [x] **`GuideLoop.EnableNeuralModel` builds `new NeuralGuideFeatures(siteLatitude: 0)`** -- a placeholder
  that looks like a value; the real latitude is only applied later at guide start. Harmless today, but a
  zero that reads like a measurement is how a future caller uses it before guide start and gets features
  for the equator. Make the placeholder unmistakable (NaN, or construct at guide start). **DONE 2026-09-02:**
  constructed at guide start only (`RunAsync`, keyed on the model rather than on a placeholder builder), and
  the `NeuralGuideFeatures` ctor lost its `= 0` default so every caller has to state the latitude.
- [ ] **`NeuralGuideTrainer` hardcodes `siteLatitude: 45.0`.** Features 18/19/20 are HA, altitude and Dec,
  which are mutually consistent for latitude 45 at TRAINING and for the user's latitude at INFERENCE, so
  the model can learn a relation between them that only holds at 45 deg. Second-order (the neural guider
  is opt-in), but the trainer should take the site it trains for, or the features should not encode
  latitude-dependent geometry at all. **DONE 2026-09-02:** `TrainEpoch` takes a required `siteLatitude`,
  the site the model trains for (one per profile, like the persisted weights). Production never called
  the offline trainer, so the only models it had shaped were the functional tests', which pretrained at 45
  and guided at 48.2; they now train at the latitude they guide at, and
  `NeuralGuideTrainerTests.GivenTwoSitesWhenTrainEpochThenTheLatitudeReachesTheWeights` pins that the
  parameter reaches the weights.
