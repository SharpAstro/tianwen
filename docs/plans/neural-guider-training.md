# Neural guider training: from P-imitation to prediction

**Status: the model, trainer and guardrail are SHIPPED and opt-in; no training plan existed until
2026-09-02, and the trainer as written cannot produce a model that beats the controller it imitates.**
This is the fifth model training in the repo and the only one that runs on the customer's machine (in
process, C#, no ONNX, no Python): the PPEC-style per-rig adaptation the imaging-model programme
explicitly leaves to it. It has no companion plan; the open items live in
[docs/todo/guider.md](../todo/guider.md) and `TODO.md` (the `train-guide-model` CLI). Shared
discipline where it applies: [model-training-roadmap.md](model-training-roadmap.md).

## 0. What exists, with pointers (`src/TianWen.Lib/Devices/Guider/`)

| Piece | Fact |
|---|---|
| `NeuralGuideModel` | MLP 26 -> 32 -> 16 -> 2, **1,426 parameters**, ReLU on both hidden layers, tanh on the output; Xavier init; hand-written forward pass over `TensorPrimitives.Dot`; `ExportParameters` / `LoadParameters` / `Predict` |
| `NeuralGuideFeatures` | Builds the 26-feature vector from two frames of history; constructed with the site latitude, because features 18 to 20 are hour angle, altitude and declination, which are mutually consistent at exactly one latitude |
| `NeuralGuideTrainer` | `TrainEpoch(calibration, pController, maxPulseMs, siteLatitude, numSamples = 256, seed = 0, inputNoiseStd = 0.15f)`: synthesises samples in process (RA error +/-3 px, Dec +/-2 px, RMS 0.2 to 1.7 / 0.1 to 1.1, HA +/-12 h, Dec +/-60 deg) and trains the net to reproduce the **P controller's** pulse for them, with Gaussian input noise added to the features while the target is computed from the clean error. `TrainOnBatch(buffer, batchSize, rng, clipNorm = 1.0f)`: online mini-batch SGD from an `ExperienceReplayBuffer` with priority weights and a component-wise gradient clamp. Loss MSE, plain SGD averaged over the batch, **no schedule, no momentum**, learning rate 0.001 offline / 0.0001 online |
| `GuideLoop` | `EnableNeuralModel` (constructs the features at guide start with the site latitude since `c6bb72dd`), `EnableOnlineLearning(onlineLearningRate = 0.0001f, profileFolder)`, `OnlineBatchSize = 16`, trains once `_experienceBuffer.Count >= MinExperiencesBeforeTraining`, blends the neural pulse in over `BlendRampInFrames = 480` |
| `NeuralGuidePerformanceMonitor` | The guardrail: refuses to use the model unless it beats the P-controller baseline by 15 percent over a minimum sample count. The one part of the design `docs/todo/sequencing.md` singles out as transferable |
| `NeuralGuideModelPersistence` | Binary `.ngm` under `<profile>/NeuralGuider/`, magic `0x4E47`, version 3, 20-byte header + 56-byte calibration block + 1,426 floats; validates the four architecture dimensions on load |
| Tests | `NeuralGuideTrainerTests` (incl. `GivenTwoSitesWhenTrainEpochThenTheLatitudeReachesTheWeights`), `NeuralGuideModelTests`, `NeuralGuideModelPersistenceTests`, `NeuralGuidePerformanceMonitorTests`; `GuideLoopTests`' neural-vs-P comparison on the coupling harness (`SetupCoupledGuidedMount`, real ~99-sample runs) |
| Simulated data | `FakeSkywatcherMountDriver` (worm PE + polar-misalignment drift, believed/true pointing split) coupled to `FakeCameraDriver`'s guide camera; the disturbance-model refactor in [docs/architecture/fake-disturbance-model.md](../architecture/fake-disturbance-model.md) is PARTIAL (the comparison test migrated; `IDisturbanceTerm` / wind / seeing knobs remain) |
| Real data | none recorded. The `train-guide-model` CLI (`TODO.md`) and the per-session CSV guide log (`guider.md`) are both TODO |

**What the shipped trainer teaches, stated plainly:** the P controller. A student trained to
reproduce a teacher's output is bounded by the teacher, and the functional comparison measured
exactly that: neural ~ P at 5 to 10 percent blend on a gentle disturbance (`guider.md` item 17). The
monitor's 15 percent bar is therefore unreachable BY CONSTRUCTION with `TrainEpoch` as written, and
the four open `guider.md` items (pretrained vs per-mount, wider MLP, real telemetry) all presuppose a
target the model could learn something from. Fixing the target comes first.

## 1. Hypotheses

**H1. The imitation ceiling is real: no configuration of `TrainEpoch` clears the monitor.** Wider
hidden layers, more samples, more epochs, less input noise.
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
recorded (or simulated) error series. Teacher = the future, not P.
*Test:* on the fake SkyWatcher with worm PE dominant on RA and seeing dominant on Dec, train
predictive against imitation, three seeds, full blend.
*Prediction:* predictive beats P by more than 15 percent RMS on RA and ties on Dec; imitation ties on
both. The RA/Dec asymmetry is the signature that separates "learned the mount" from "got lucky".
*Kill:* predictive ties P on RA too. Then either the features cannot represent the period (H3) or
the fake's PE is not what the features see.

**H3. Worm phase is the one feature that carries PE, and two frames of history cannot substitute
for it.** A worm period of several hundred seconds against a 2 to 5 s guide cadence is far beyond a
two-frame window.
*Test:* read `NeuralGuideFeatures`: if no worm-phase (or time-in-period) feature exists, add one
(sin and cos of the phase, from the mount's worm period, which the SkyWatcher driver can derive from
its step counts, and from a fitted period elsewhere), bump `.ngm` to version 4 (the input size
changes), and rerun H2 with and without it.
*Prediction:* without phase, predictive collapses toward P; with it, the H2 gain appears. If the
feature already exists, H3 reduces to an ablation of it.

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
| N0 | Read `NeuralGuideFeatures`; write down the 26 features and whether a phase term exists | hour | H3's form |
| N1 | Ceiling measurement on the coupling harness, harder regime, full blend, three seeds | hours (fake clock) | H1 |
| N2 | Predictive target in `TrainEpoch` (hindsight from a simulated series); worm-phase features if absent; `.ngm` v4 | 2 days | H2, H3 |
| N3 | Capacity ablation under the new target | hours | H4 |
| N4 | Cross-mount transfer on two fakes; online refinement | a day | H5 |
| N5 | Eight-hour stability run under the time pump | hours | H6 |
| N6 | CSV guide log + `train-guide-model` CLI (record N cycles, train, write the base `.ngm`) | 2 days | H7 tooling |
| N7 | One real recording night, then one guided night with the model admitted | nights | H7 |

## 6. Phasing

| Phase | Deliverable | Exit |
|---|---|---|
| NG0 | Ceiling measured and recorded in `guider.md` | N1 |
| NG1 | Predictive trainer + phase features + `.ngm` v4, beating P on the fake's RA | N2, N3 |
| NG2 | Pretrained base + per-profile online refinement, stable over a night | N4, N5 |
| NG3 | Recording CLI + guide log; real-night result | N6, N7 |

## 7. Open questions

- **Which worm period** for mounts that do not expose step counts (LX200, OnStep): a fitted period from
  the open-loop recording (a periodogram of the RA error) is the fallback; the CLI should print it.
- **Does the model act, or advise?** Today it emits a pulse blended with P. A PEC-style alternative
  emits a predicted displacement that P then corrects for, which keeps P's stability guarantees and
  makes the neural part a feed-forward term. Decide after H2: if the gain is all on the predictable RA
  component, feed-forward is the cleaner shape.
- **Site latitude at inference** is fixed (`c6bb72dd`); confirm the persisted `.ngm` carries the
  latitude it trained at, so a profile moved to another site refuses the model rather than guiding
  off-manifold.
