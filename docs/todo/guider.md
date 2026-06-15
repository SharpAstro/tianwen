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
- [x] Support ST-4 guide port as guiding output — `PulseGuideRouter` + `PulseGuideSource` (`?pulseGuideSource=Auto|Camera|Mount` on the guider URI) routes corrections through `ICameraDriver.PulseGuideAsync`. `Auto` prefers the mount (commit 8a08691): camera `CanPulseGuide` only proves an ST-4 *socket* exists (`HasST4Port`), not that a cable is connected
- [ ] Support snap/shutter-release port for external camera triggering
- [ ] Finish the fake disturbance-model refactor (see `docs/architecture/fake-disturbance-model.md`). DONE so far: the neural-vs-P comparison (`GuideLoopTests`) migrated onto the coupling harness via `SetupCoupledGuidedMount` (real ~99-sample runs, not the 2-sample vacuity). REMAINING: (a) the shared `IDisturbanceTerm` / `MountDisturbanceModel` abstraction (steps 1-5) so PE/polar/flexure/wind/seeing are one composable model instead of three overlapping ones; (b) migrate the other `SetupGuidedMount`-based tests (`GivenWindGusts…`, `GivenCableSnag…`, `GivenCombinedDisturbances…`) off the sidereal-contaminated hand-rolled renderer; (c) add wind + seeing knobs to the coupling path (step 7). Also: the comparison currently only exercises ~5-10% neural blend (BlendRampInFrames=480 vs 100 iterations) on a gentle, well-correctable disturbance, so neural ≈ P — a discriminating variant (harder regime + fuller blend, or a model trained on outcomes not P-imitation) would make the guardrail bite.

