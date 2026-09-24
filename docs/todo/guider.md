# TODO -- Guider

**The open items are GitHub issues** labelled [`area:guider`](https://github.com/SharpAstro/tianwen/issues?q=is%3Aissue+is%3Aopen+label%3Aarea%3Aguider) since 2026-09-24, when this file was migrated. What is left here is the DONE archive, kept for the measurements and reasons it records. Never add an open `- [ ]` here: open an issue.

## Guider

- [x] Built-in guider receives same mount driver instance via `IMountDependentGuider` wiring in `SessionFactory`
- [x] Support ST-4 guide port as guiding output: `PulseGuideRouter` + `PulseGuideSource` (`?pulseGuideSource=Auto|Camera|Mount` on the guider URI) routes corrections through `ICameraDriver.StartPulseGuideAsync`. `Auto` prefers the mount (commit 8a08691): camera `CanPulseGuide` only proves an ST-4 *socket* exists (`HasST4Port`), not that a cable is connected

### Filed 2026-08-30 from the 2026-08-29 session notes (found in passing, neither fixed)

- [x] **`GuideLoop.EnableNeuralModel` builds `new NeuralGuideFeatures(siteLatitude: 0)`** -- a placeholder
  that looks like a value; the real latitude is only applied later at guide start. Harmless today, but a
  zero that reads like a measurement is how a future caller uses it before guide start and gets features
  for the equator. Make the placeholder unmistakable (NaN, or construct at guide start). **DONE 2026-09-02:**
  constructed at guide start only (`RunAsync`, keyed on the model rather than on a placeholder builder), and
  the `NeuralGuideFeatures` ctor lost its `= 0` default so every caller has to state the latitude.
- [x] **`NeuralGuideTrainer` hardcodes `siteLatitude: 45.0`.** Features 18/19/20 are HA, altitude and Dec,
  which are mutually consistent for latitude 45 at TRAINING and for the user's latitude at INFERENCE, so
  the model can learn a relation between them that only holds at 45 deg. Second-order (the neural guider
  is opt-in), but the trainer should take the site it trains for, or the features should not encode
  latitude-dependent geometry at all. **DONE 2026-09-02:** `TrainEpoch` takes a required `siteLatitude`,
  the site the model trains for (one per profile, like the persisted weights). Production never called
  the offline trainer, so the only models it had shaped were the functional tests', which pretrained at 45
  and guided at 48.2; they now train at the latitude they guide at, and
  `NeuralGuideTrainerTests.GivenTwoSitesWhenTrainEpochThenTheLatitudeReachesTheWeights` pins that the
  parameter reaches the weights.
