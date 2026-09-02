# PixInsight Parity Tracker

PixInsight is by far the most-referenced third-party tool in this repo's docs (69 mentions across
22 files at last count) -- TianWen's image-processing pipeline implicitly targets, and in several
places exceeds, its capabilities the same way Astropy is the science-layer target
([astropy-parity.md](astropy-parity.md)) and N.I.N.A. is the app-layer target
([nina-parity.md](nina-parity.md)). **This file is an INDEX, not a duplicate**: every subsystem
already has its own plan doc carrying the real detail; this just says what's shipped, what isn't,
and points at where. Siril (52 mentions/10 files) and GraXpert (25/9 files) are already framed
alongside PixInsight as prior art for background extraction specifically, so they get a shared
section rather than their own files. SetiAstro gets its own section: it has **five** distinct
relationships to this codebase that must not be conflated.

Snapshot taken 2026-08-29. Update the STATUS cell in place as linked plans change state.

## Core stretch / colour pipeline

| Subsystem | Status | Detail |
|---|---|---|
| SPCC (spectrophotometric colour calibration) | DONE | Integrates Pickles SED x system throughput per matched star; see CLAUDE.md's stacking/stretch-pipeline sections. Uses SetiAstro's SASP calibration data -- see the SetiAstro section below. |
| Stretch semantics (Linked / Unlinked / Luma / MTF) | DONE | `docs/architecture/stretch-pipeline.md`. CLAUDE.md is explicit that "Linked and Unlinked mean what they mean in PixInsight." |
| GHS (Generalized Hyperbolic Stretch) | DONE ~90% | [ghs.md](ghs.md) -- ported from SetiAstro's PJSR `ghs_dialog_pro.py`. |
| Background extraction (ABE/DBE) | NOT STARTED, but the plan itself is near implementation-ready | [background-extraction.md](background-extraction.md) -- PixInsight ABE/DBE, Siril, and GraXpert all cited as prior art; full interface contract, project layout and a 5-phase table already written, not just a design sketch. Scoped 2026-08-29: the Siril scripts `AutoBGE.py`/`GraXpert-AI.py`/`AutoGradientRemoval.py` (`free-astro/siril-scripts`) now resolve the sample-placement heuristic and the GraXpert-interop open question with concrete references instead of "probably yes eventually." Two corrections made: the plan's claim that `MathNet.Numerics` was already a dependency was **false** (grepped, no reference anywhere) -- picking it is a genuinely new dependency, re-weighed against a no-new-dep hand-rolled option; and SASPro's own `abe.py` (the 5th SetiAstro relationship -- see below), whose reference checkout is a sibling repo at `~/source/repos/other/setiastrosuitepro` (not inside `tianwen/`, which is why it first looked absent), is confirmed **GPL-3.0** per its own README -- same framing as Siril/GraXpert: lawful to combine under TianWen's AGPL-3.0-or-later, reimplementing from the algorithm still preferred. |
| Narrowband colour (SHO/HOO palettes) | NOT STARTED, P1 scope is clear and unblocked | [narrowband-colour.md](narrowband-colour.md) -- Siril techniques cited by name: VeraLux Alchemy, DBXtract, AstroColorMixer, NarrowbandNormalization, plus a newer "Spectral Extract" alternative to DBXtract's hardcoded crosstalk table (fits coefficients from `FilterCurveDatabase`'s own QE x CFA x filter curves instead). Scoped 2026-08-29: **P1 = Phases 1+2 only** (robust plane normalization + named-preset palette mixer, ADR-1's own decision) -- no new data dependency, ~1 day of core effort, fixes the rank-deficient-cyan-HOO complaint directly. Only Phase 4 (SPCC narrowband) is *truly* blocked, on Gaia DR3 per-star spectra (ADR-3); everything else is buildable today, just bigger in scope than P1. The single biggest remaining chunk of unbuilt PixInsight/Siril-parity work in this list, but no longer an undifferentiated "13 ADRs, NOT STARTED" -- what to build first is settled. |

## AI enhancement ecosystem

| Subsystem | Status | Detail |
|---|---|---|
| RC-Astro (BlurX/NoiseX/StarXTerminator) | Phases 1-3 DONE | [rc-astro-enhancers.md](rc-astro-enhancers.md) -- has its own plan already; point here, don't duplicate. These are PixInsight plugins, which is why they belong in this family, but the detail lives in their own doc. |
| SetiAstro | See dedicated section below -- five distinct relationships, do not collapse them. | |

## SetiAstro: five relationships, not one

1. **AI4 ONNX model vendor (SAS Pro).** The denoise/deconvolution ONNX models -- see CLAUDE.md's
   "AI Image Enhancement: SETI Astro (ONNX) + RC-Astro (CLI)" and
   [rc-astro-enhancers.md](rc-astro-enhancers.md) (RC-Astro preferred when licensed, SAS Pro is the
   fallback). A model-weights dependency, not a code port.
2. **Ported PJSR scripts.** GHS (above, DONE ~90%) and Star Stretch (`docs/plans/ai-enhancement.md`,
   "Frank StarStretch" in the original dual-stretch pipeline, later superseded by
   `MasterPreviewRenderer`/`StretchSolver`). Algorithm ports, not model weights.
3. **"Statistical Stretch" learnings -- shipped; the cross-reference gap below is now CLOSED
   (2026-08-29).** `docs/todo/imaging.md`'s "Learnings from PixInsight Statistical Stretch (SetiAstro,
   v2.3)" section (line 409 on) is where these actually live, and every one of the following is
   `[x]` DONE there: **Luma-only stretch mode** (Rec.709 luminance, stretch Y, scale RGB by Y'/Y --
   line 450), **Luma blend** (`StretchUniforms.LumaBlend`, line 479), **Rec.601/Rec.2020 luma
   weighting** (`LumaWeighting` enum, line 480), **sensor-derived luma weights**
   (`LumaWeighting.SensorMatched`, line 481), and **background neutralization pivot1 mode**
   (`BackgroundNeutralization.ComputeGains`, "algebraically verified equivalent to SETI's
   `out = 1 - (1 - val) * g`," line 458). **The actual gap is narrow and purely documentary**: none
   of this is mentioned in `docs/architecture/stretch-pipeline.md`, which is the doc that actually
   documents `StretchMode.Luma` -- the shipped capability and its origin story live in two different
   places with no link between them.
4. **SASP calibration data itself.** `filter_curves.gs.gz`/`sensor_qe.gs.gz`/`pickles_sed.gs.gz`
   (`docs/todo/imaging.md` line 461) -- tracked in git, feeds SPCC directly. A *data* dependency,
   categorically different from the other relationships here; don't describe it as a ported
   algorithm or a model vendor.
5. **SASPro (Seti Astro Suite Pro), the standalone desktop app -- distinct from the PJSR scripts
   above, which run inside PixInsight.** [background-extraction.md](background-extraction.md)'s
   reference implementation for ABE is `abe.py` from this app, not a PJSR script. Confirmed GPL-3.0
   (its own `README.md`: "Seti Astro Suite Pro is licensed under the GNU General Public License
   v3.0"). ~~Reference checkout lives at `~/source/repos/other/setiastrosuitepro`~~ **No local
   checkout exists on this machine (checked 2026-09-02)**; the source is
   `github.com/setiastro/setiastrosuitepro` (`src/setiastro/saspro/abe.py`, read from `main` that
   day, see background-extraction.md's "Reference review"). The path claim was wrong twice, once
   as an in-repo relative path and once as a sibling checkout; cite the GitHub URL.

Two more SetiAstro/PixInsight-script-derived backlog items, both NOT STARTED, both in the same
`docs/todo/imaging.md` section:

- **`DarkStructureEnhance`** (a one-sided unsharp mask that darkens dust lanes; read from source,
  `DarkStructureEnhance.js`, Carlos Sonnenstein + Oriol Lehmkuhl/PTeam). Its reuse terms have not
  been established, so any implementation is algorithm-only from the written description, never a
  code copy.
- **An autostretch confidence-signal estimator**, mirroring Siril's `find_linked_midtones_balance`.
  **License caution, worth repeating exactly**: the doc cites EZ_SoftStretch as prior art for one
  approach and notes its source "carries a bare copyright line with [no license grant at all] --
  do not go looking for the code." Implement from the description, never from that source.

## Mosaic / stitching

Already covered by [wcs-reprojection.md](wcs-reprojection.md), which names PixInsight's own manual
multi-step mosaic scripts as what consumer users reach for today instead of a native reprojection
pipeline -- point there rather than re-deriving it here.

## Deliberately excluded: SharpCap

SharpCap is a different tool category (live capture / EAA, not post-processing) and does **not**
belong in this index. It already has thorough, gap-free documentation at its points of use:
[polar-alignment.md](polar-alignment.md)'s own title is "Plan: Polar Alignment Routine
(SharpCap-style)" (DONE ~85%), and [live-planetary-capture.md](live-planetary-capture.md) has a
dedicated "SharpCap-informed UI redesign" section (ROI drag-to-set, Track Planet Center/Lock). No
new doc, no pointer needed beyond what already exists at each site.

## Ranked gaps

1. ~~Cross-reference the Statistical-Stretch-derived features in `stretch-pipeline.md`~~ **DONE
   2026-08-29**: provenance notes added for Luma mode, `LumaBlend`, the `LumaWeighting` options
   (Rec.709/601/2020/SensorMatched), and the `MinPivot` background-neutralization gain formula.
2. **Background extraction (ABE/DBE)** -- design captured, nothing built; the GraXpert-interop open
   question is the one place this is cross-tool compatibility rather than algorithm porting.
3. **Narrowband colour** -- 13 ADRs already written, the largest unbuilt chunk here.
4. **`DarkStructureEnhance` + the autostretch confidence estimator** -- both NOT STARTED backlog
   items; the latter carries an explicit license trap (EZ_SoftStretch: no license grant, don't look
   at the source).

## Maintenance rule

Update the STATUS cell here whenever a linked plan changes state. Do not let this file say DONE
while the source doc still says NOT STARTED, or vice versa.
