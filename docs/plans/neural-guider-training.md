# Neural guider training: from P-imitation to prediction

**Status: the model, both trainers and the guardrail are SHIPPED and opt-in. Corrected 2026-11-11 by a read of
the code (section 0a): only the OFFLINE pre-training imitates the P controller. The online learner already
trains on the hindsight target, and the worm-phase inputs already exist, so the model can improve past P as
it guides, on a mount that reports its phase. What is missing is the evidence that it does, and a phase for
every mount that reports none (OnStep, LX200, the SkyGuider Pro).**
This is the fifth model training in the repo and the only one that runs on the customer's machine (in
process, C#, no ONNX, no Python): the PPEC-style per-rig adaptation the imaging-model programme
explicitly leaves to it. It has no companion plan; the open items live in
[docs/todo/guider.md](../todo/guider.md) and `TODO.md` (the `train-guide-model` CLI). Shared
discipline where it applies: [model-training-roadmap.md](model-training-roadmap.md).

## 0. What exists, with pointers (`src/TianWen.Lib/Devices/Guider/`)

| Piece | Fact |
|---|---|
| `NeuralGuideModel` | MLP 26 -> 32 -> 16 -> 2, **1,426 parameters**, ReLU on both hidden layers, tanh on the output; Xavier init; hand-written forward pass over `TensorPrimitives.Dot`; `ExportParameters` / `LoadParameters` / `Predict` |
| `NeuralGuideFeatures` | Builds the 26-feature vector: the RA and Dec error now and at the three frames before (0 to 7), their means over 10, 60 and 300 frames and the short RMS (8 to 15), the **accumulated gear error** (16, 17: the measured error plus the corrections already applied, the curve the mount would have drawn unguided), hour angle, altitude and declination (18 to 20, mutually consistent at exactly one latitude, hence the site latitude in the constructor), the time since the last correction (21), and **the sine and cosine of the RA and Dec worm phase (22 to 25), zero where the mount reports none** |
| `NeuralGuideTrainer` | `TrainEpoch(calibration, pController, maxPulseMs, siteLatitude, numSamples = 256, seed = 0, inputNoiseStd = 0.15f)`: synthesises samples in process (RA error +/-3 px, Dec +/-2 px, RMS 0.2 to 1.7 / 0.1 to 1.1, HA +/-12 h, Dec +/-60 deg) and trains the net to reproduce the **P controller's** pulse for them, with Gaussian input noise added to the features while the target is computed from the clean error. `TrainOnBatch(buffer, batchSize, rng, clipNorm = 1.0f)`: online mini-batch SGD from an `ExperienceReplayBuffer` with priority weights and a component-wise gradient clamp, **on the hindsight target**: a sample enters the buffer with P's pulse as its target, and the next frame replaces it with the pulse applied plus what was still needed to zero that frame's error (`ExperienceReplayBuffer.UpdateOutcome`); P's pulse stays only where no next frame came (a lost star). Loss MSE, plain SGD averaged over the batch, **no schedule, no momentum**, learning rate 0.001 offline / 0.0001 online |
| `GuideLoop` | `EnableNeuralModel` (constructs the features at guide start with the site latitude since `c6bb72dd`), `EnableOnlineLearning(onlineLearningRate = 0.0001f, profileFolder)`, `OnlineBatchSize = 16`, trains every `OnlineTrainingInterval = 20` frames once the buffer holds `MinExperiencesBeforeTraining = 64`, blends the neural pulse in over `BlendRampInFrames = 480` up to `NeuralBlendFactor = 0.5`; the phase is the mount's `GetAxisPositionAsync` modulo `GetWormPeriodStepsAsync`, read each frame |
| `NeuralGuidePerformanceMonitor` | The guardrail: refuses to use the model unless it beats the P-controller baseline by 15 percent over a minimum sample count. The one part of the design `docs/todo/sequencing.md` singles out as transferable |
| `NeuralGuideModelPersistence` | Binary `.ngm` under `<profile>/NeuralGuider/`, magic `0x4E47`, version 3, 20-byte header + 56-byte calibration block + 1,426 floats; validates the four architecture dimensions on load |
| Tests | `NeuralGuideTrainerTests` (incl. `GivenTwoSitesWhenTrainEpochThenTheLatitudeReachesTheWeights`), `NeuralGuideModelTests`, `NeuralGuideModelPersistenceTests`, `NeuralGuidePerformanceMonitorTests`; `GuideLoopTests`' neural-vs-P comparison on the coupling harness (`SetupCoupledGuidedMount`, real ~99-sample runs) |
| Simulated data | `FakeSkywatcherMountDriver` (worm PE + polar-misalignment drift, believed/true pointing split) coupled to `FakeCameraDriver`'s guide camera; the disturbance-model refactor in [docs/architecture/fake-disturbance-model.md](../architecture/fake-disturbance-model.md) is PARTIAL (the comparison test migrated; `IDisturbanceTerm` / wind / seeing knobs remain) |
| Real data | none recorded. The `train-guide-model` CLI (`TODO.md`) and the per-session CSV guide log (`guider.md`) are both TODO |

**What the shipped trainers teach, stated plainly.** `TrainEpoch`, the offline pre-training, teaches the P
controller, and a student trained to reproduce a teacher's output is bounded by the teacher. `TrainOnBatch`, the
online learner, teaches the pulse that would have zeroed the next frame's error, which P cannot give. The one
functional comparison read neural ~ P at 5 to 10 percent blend on a gentle disturbance (`guider.md` item 17):
that is a model fresh from pre-training on a regime with little to predict, so it says nothing about what the
online learner reaches. The monitor's 15 percent bar is unreachable by `TrainEpoch` alone, and reachable in
principle once the model has guided for a while. No measurement shows either yet.

## 0a. Findings, 2026-11-11

Tracked by #2106 (the measurement, N1 and N2, and the fitted period, N2b).

### The code already holds the predictive target and the phase inputs

This plan's first version (2026-09-02) was written from `TrainEpoch` and missed the online path. Read on
2026-11-11:
- **The hindsight target is built** (`ExperienceReplayBuffer.UpdateOutcome`, fed by `GuideLoop` each frame):
  target = the pulse applied, less the next frame's error over the calibrated rate. It is a statement about the
  mount, whoever chose the pulse, so the blend with P does not pull it toward P.
- **The worm-phase features are built** (22 to 25), from the mount's raw axis position modulo its steps per
  worm turn.
- So H2's target and H3's feature exist; what both hypotheses still lack is their test.

### Which mounts feed the phase

| Mount | `GetAxisPositionAsync` and `GetWormPeriodStepsAsync` | Phase features |
|---|---|---|
| SkyWatcher, and the fakes | both, the period off the `:s` reply | fed |
| OnStep | neither. `GetAxisAngleAsync` reads `:GX42#` / `:GX43#` in degrees since 2026-11-02, for the meridian limit alone; raw counts would be that angle times `:GXE4#` / `:GXE5#`, the worm period `:GXE7#`, none of them seen on a mount (`docs/architecture/onstep-protocol.md`, "No periodic-error phase") | zero |
| LX200, the SkyGuider Pro, ASCOM, Alpaca | the interface defaults (not read on a mount) | zero |

A harmonic drive (the bench TeeSeek 11) has no worm for `:GXE7#` to report, and it still has a periodic error:
it repeats once a turn of the wave generator (the sidereal day over the total reduction), usually with a strong
component at half that, and steeper slopes than a worm's. Its period has to come from the data.

### The paper behind PHD2's predictive PEC

Klenske, Zeilinger, Schoelkopf and Hennig, "Gaussian Process-Based Predictive Control for Periodic Error
Correction", IEEE Transactions on Control Systems Technology 24(1), 110-121, 2016,
[doi:10.1109/TCST.2015.2420629](https://doi.org/10.1109/TCST.2015.2420629). Read in full on 2026-11-11. What it
says, and what follows here:
- **The model.** The mount is `x' = u + g(t)`: the correction plus an unknown, nearly periodic disturbance. Each
  frame gives one observation of `g`, from the change in the error and the correction applied, which is our
  accumulated gear error's increment.
- **The kernel is a PRODUCT**, a periodic kernel times a broad squared-exponential one, so the periodicity is
  local: the prediction fades to zero away from the data instead of running out of phase. A strictly periodic
  model is named as the failure to avoid.
- **The period is a fitted parameter**, one of four, by the posterior's maximum, one optimiser step a frame.
  After it converges, HALF the period is tried as well, since a multiple of the true period fits nearly as
  well. The paper takes no spectrum; a spectrum's peak is how PHD2's later implementation is remembered to seed
  it, which nobody here has re-read.
- **How much data.** The estimate is overconfident until the data covers several periods. The hardware runs
  kept the last 90 samples (270 s, about three turns of the short component); the simulations gave ten periods.
- **The measured gain.** On a Vixen Sphinx at 3 s sampling, three runs of 25 minutes, against the same
  controller without the prediction: RMS down 22.6 percent, about a third of the controllable error, the
  components near 100 s and 500 s damped and some structure left. So the monitor's 15 percent is reachable, and
  not by much.
- **Where it pays.** In simulation the gain appears where the sampling interval is 10 to 80 percent of the
  period; far below that the error is locally linear and every controller does as well. A 2 s cadence on a 400 s
  worm is 0.5 percent. The gain is in the faster components, in long guide exposures and on steep slopes.
- **Through a dropout.** With the measurements gone (cloud, a lost star), the periodic model keeps correcting
  open loop with almost no error and every other controller degrades.
- **The shape.** The prediction is fed forward and the reactive controller stays, which answers section 7's
  "act or advise" for the paper's part: advise.
- **One period only.** A second period (a harmonic drive's half-turn component) is beyond the paper.

### What follows

- **A fitted period supplies the phase where the mount reports none** (H8): from the accumulated gear error,
  kept refining, with the half-period check, and a floor under which it declares no period.
- **On OnStep the phase is anchored to the axis.** The fitted period times the sidereal rate is an angle, and
  the phase is `GetAxisAngleAsync` modulo that angle, which survives a slew, a flip and a night where a phase
  counted on the clock does not.
- **The comparison varies the cadence against the period**, since the paper says that ratio decides the result,
  and reads a dropout as well as steady guiding.

## 1. Hypotheses

**H1. The imitation ceiling is real for the pre-training: no configuration of `TrainEpoch` ALONE clears the
monitor.** Wider hidden layers, more samples, more epochs, less input noise, online learning off.
*Test:* the coupling harness with a harder regime (worm PE at the fake's default amplitude plus
polar drift) and FULL blend (no ramp), three seeds, RA and Dec RMS against P on the same disturbance
realisation.
*Prediction:* the neural RMS lands within +/-5 percent of P for every configuration; the monitor
never admits the model.
*Kill:* none; this is the measurement that licenses replacing the teacher. If it does clear 15
percent somewhere, record where, because it would mean the P baseline was mis-tuned, not that
imitation works.

**H2. The learnable signal is the PREDICTABLE part of the disturbance, and the target has to be
predictive.** Worm periodic error and polar drift are deterministic functions of worm phase and time;
seeing is not. A controller that can predict the next frame's PE displacement can pre-compensate it,
which P by definition cannot (it acts on the error after it appears). Target: the pulse that would
have ZEROED the next frame's measured error given the current history, computed in hindsight from a
recorded (or simulated) error series. Teacher = the future, not P. **The online learner already has this
target (section 0a); the test below has not been run.**
*Test:* on the fake SkyWatcher with worm PE dominant on RA and seeing dominant on Dec, the online learner
against the pre-training alone, three seeds, full blend, at guide cadences from 1 to 30 percent of the period
(section 0a, "Where it pays"), and through a 60 s gap in the frames.
*Prediction:* predictive beats P by more than 15 percent RMS on RA and ties on Dec; imitation ties on
both. The RA/Dec asymmetry is the signature that separates "learned the mount" from "got lucky".
*Kill:* predictive ties P on RA too. Then either the features cannot represent the period (H3) or
the fake's PE is not what the features see.

**H3. Worm phase is the one feature that carries PE, and two frames of history cannot substitute
for it.** A worm period of several hundred seconds against a 2 to 5 s guide cadence is far beyond a
two-frame window.
**The feature exists (22 to 25, section 0a), so H3 is an ablation of it.**
*Test:* H2's run on the fake SkyWatcher with the phase fed and with it zeroed, as OnStep's is today.
*Prediction:* without phase, the online learner collapses toward P; with it, the H2 gain appears.

**H4. Capacity is not the lever until the target is.** 1,426 parameters is ample for a 26-input
policy; `guider.md`'s "wider/deeper MLP" question is premature.
*Test:* 64/32 hidden against 32/16 under the predictive target from H2.
*Prediction:* no gain beyond seed spread. Revisit only if H2's gain is capacity-limited (training
loss still falling at the end).

**H5. The POLICY transfers across mounts; the PE model does not, so ship a pretrained base and learn
PE per profile.** Gain scheduling against measured RMS and settle behaviour is universal; worm
period, amplitude and phase are the mount's.
*Test:* pretrain on fake mount A (one PE period and amplitude), evaluate on fake mount B (different
period and amplitude) before and after online refinement.
*Prediction:* before refinement, B performs like P (policy transferred, PE not); after N worm cycles
of online learning, B reaches A's gain.
*Consequence:* the answer to "ship a pretrained model or train from scratch per mount" is BOTH: a
pretrained base `.ngm` (policy) per architecture version, refined per profile online, persisted per
profile id (not name; `guider.md` item 9).

**H6. Online learning is stable over a night, given the guardrail.** Plain SGD with no schedule on a
priority replay buffer can drift.
*Test:* an 8-hour fake night under `ExternalTimePump` (never a wall clock; the pump is the sole clock
or two competing clocks scramble the cadence), sampling held-out validation error every 30 minutes.
*Prediction:* validation error does not rise after the first hour; the monitor keeps the model
admitted; the blend ramp never has to re-arm.
*Kill:* drift. Then add a learning-rate decay per session and a replay-buffer freeze once the monitor
has admitted the model.

**H7. Real recordings replace the synthetic teacher entirely.** The `train-guide-model` CLI records N
worm cycles on a connected mount and guide camera; the recorded series IS the predictive target in
hindsight, with no controller in the loop at all.
*Test:* one real night on the user's rig, once H2 passes on the fake.
*Prediction:* the real-trained model clears the monitor on RA on a following night; the CSV guide log
(`guider.md` item 10) is the evidence.

**H8. A period fitted from the guiding itself is as good as the mount's own phase.** The accumulated gear
error over a few turns holds the period (section 0a).
*Test:* on the fake SkyWatcher, whose true period and phase are known: the fitted period against the true one
after 2, 3 and 5 turns, then H2's run with the fitted phase in place of the mount's. Then a second fake with a
harmonic drive's two components.
*Prediction:* the period within 1 percent after three turns; the fitted phase gives at least 80 percent of the
true phase's gain over P.
*Kill:* the fitted phase gives under half the true phase's gain. Then the phase is not what the net lacks on
such a mount, and the paper's own model (a Gaussian process fed forward) is ported instead, PHD2's licence
checked first.

## 2. Data

- **Synthetic (now):** the fake SkyWatcher's believed/true split with worm PE and polar drift, coupled
  to the fake guide camera through `IDeviceHub` (no session special-casing). The disturbance-model
  refactor should finish first where it is cheap (wind and seeing knobs on the coupling path), because
  a training set with no seeing teaches a controller that over-trusts its prediction.
- **Recorded (H7):** per-session CSV guide logs beside the model weights (frame time, measured RA/Dec
  error, pulse issued, worm phase if known, HA/alt/Dec, star SNR), written by `GuideLoop` from the
  same values `Session.GuideSamples` already holds, so nothing new is measured. The `train-guide-model`
  CLI records N cycles with guiding OFF (open loop) and with P ON (closed loop) so both targets exist.
- **Held-out:** by night, never by frame (adjacent frames share the disturbance).

## 3. Model and recipe

Keep the MLP; change what it learns. Predictive target (H2), worm-phase features (H3), MSE loss,
SGD with a per-session decay if H6 needs it, gradient clamp as today, seeds fixed (the offline trainer
seeds via `seed`, the online loop uses `new Random(42)`; keep both explicit). The `.ngm` version bumps
when the input size changes and the loader keeps refusing mismatched dimensions, the gate-and-refuse
pattern the imaging models borrow.

## 4. Metrics and gates

- **Paired RMS against P on the same disturbance realisation** (same seed), RA and Dec separately, in
  arcseconds; peak error; settle time after a dither.
- **The monitor's 15 percent bar** is the release gate, on the fake first and a real night second.
- **Blind stability over a night** (H6).
- Report the RA/Dec asymmetry: a win on both axes with seeing-dominated Dec is a sign of a
  mis-tuned baseline, not of a good model.

## 5. Experiments, in order

| Step | What | Cost | Decides |
|---|---|---|---|
| N0 | Read `NeuralGuideFeatures`; write down the 26 features and whether a phase term exists | **done 2026-11-11** (section 0a): it exists, and so does the hindsight target | H3's form |
| N1 | Ceiling measurement on the coupling harness, harder regime, full blend, three seeds, online learning off | hours (fake clock) | H1 |
| N2 | The online learner against it on the same realisations, the phase fed and zeroed, over the cadence ratios and a dropout | hours (fake clock) | H2, H3 |
| N2b | The period estimator in `TianWen.Lib` (from the accumulated gear error, refining, the half-period check), its phase fed where the mount reports none; OnStep's anchored to `GetAxisAngleAsync`; `.ngm` v4 only if a second period's inputs are added | 2 days | H8 |
| N3 | Capacity ablation under the new target | hours | H4 |
| N4 | Cross-mount transfer on two fakes; online refinement | a day | H5 |
| N5 | Eight-hour stability run under the time pump | hours | H6 |
| N6 | CSV guide log + `train-guide-model` CLI (record N cycles, train, write the base `.ngm`) | 2 days | H7 tooling |
| N7 | One real recording night, then one guided night with the model admitted | nights | H7 |

## 6. Phasing

| Phase | Deliverable | Exit |
|---|---|---|
| NG0 | Ceiling measured and recorded in `guider.md` | N1 |
| NG1 | The online learner beating P on the fake's RA, with the mount's phase and with a fitted one | N2, N2b, N3 |
| NG2 | Pretrained base + per-profile online refinement, stable over a night | N4, N5 |
| NG3 | Recording CLI + guide log; real-night result | N6, N7 |

## 7. Open questions

- **Which worm period** for mounts that do not expose step counts (LX200, OnStep): fitted from the guiding
  itself (H8, N2b), with no recording step; the CLI should print it. Open: how a second period (a harmonic
  drive's half-turn component) is fed, and whether one unguided bench run of the TeeSeek 11 (15 to 20 minutes on
  a star) should set the fake's harmonic-drive term first.
- **Does the model act, or advise?** Today it emits a pulse blended with P. A PEC-style alternative
  emits a predicted displacement that P then corrects for, which keeps P's stability guarantees and
  makes the neural part a feed-forward term. Decide after H2: if the gain is all on the predictable RA
  component, feed-forward is the cleaner shape. The paper (section 0a) feeds its prediction forward.
- **Site latitude at inference** is fixed (`c6bb72dd`); confirm the persisted `.ngm` carries the
  latitude it trained at, so a profile moved to another site refuses the model rather than guiding
  off-manifold.
