# sky-trend-probe

Dev-only tool for measuring #2030's sky-trend rule (`SkyOverTrend` / `SkyRising` in `LightGrading`) on every captured night
(#2037). It is not in `TianWen.slnx` and CI never builds it, like `tools/GssOracle`.

## What it does

For each light folder it grades every FITS the way two real paths do, then writes CSVs to `--out`:

- **The viewer**: the file is read with `Image.TryReadFitsFile`, scaled to [0, 1] by its own peak as `AstroImageDocument.AdoptImageAsync`
  does, stars are found at snrMin 10 (2000 stars) and `LightGrading.Grade(image, stars, fullScaleAdu)` runs; `LightGrading.Verdict` is
  then taken over the whole folder's grades.
- **The session's live judge**: the camera's image as read, stars at snrMin 10 (1000 stars), `LightGrading.Grade(image, stars)` with no
  full-scale ADU, fed frame by frame in time order (one `LiveCloudJudge` per rig) as `Session.Imaging.cs` does. `ReplayLive` then
  applies what the session does with a cloud verdict: a spell of cloud frames starts the clock, a clear frame ends it, and a spell that
  reaches `CloudWaitTimeout` (15 min) is given up (the condition-deterioration path, which advances to the next target).

**No clear-night references are used by default.** `SkyRising` only counts where `ReferenceNight` is default, so a grade with no
references is the one that exercises it. `--references <clear-sky.json>` reads the viewer's `Viewer/clear-sky.json` to grade with them.

Per frame (`frames.csv`): the owner's mark from the name (`BAD_`, `BAD_CLOUD_`, `MAYBE_CLOUD_`; a cloud mark is `MAYBE_CLOUD_`, a bare
`BAD_` or `BAD_CLOUD_`), DATE-OBS, exposure, group and rig, sky level, patchiness, star count, **star flux** (the median `ImagedStar.Flux`
of the 50 brightest stars on the live read, in counts), the folder verdict (`Causes`, `InCloudRun`, `NightSky`, `Shown`, `SkyOverTrend`,
`SkyRising`, and the terms), the live verdict, the Sun's altitude, and the Moon's altitude, illumination and separation from the target
at the header's site. A header with no site takes the same rig's site from another frame (`site_source = borrowed-rig`), else `--site
lat,lon` (`profile`), else the astronomy columns are blank.

Analysis (pure functions in `Analysis.cs`, tested by `--self-check`):

- `stretches.csv`: runs of consecutive marked-cloud frames: minutes, frames, how many each term caught, and how many read clear again
  inside the stretch (the trend catching up, the 51 minute cliff), the star flux against the hour before.
- `rise-stretches.csv`: runs of `SkyRising` frames, classed **FELL-BACK** (the sky came back under the 4 % bar of its pre-rise line
  before the series ended) or **KEPT-RISING-TO-END**, with the Sun's altitude at each end and the flux against the hour before.
- `live.csv`: the live replay (first `MAYBE_CLOUD_`, spells, which pass the 15 minute wait, whether they lie on marked frames).
- `scores.csv`: precision and recall of `SkyRising` and of the existing terms against the marks.
- `summary.md`: the same in a page.

`--synthetic-moon` takes the (clear) folders given, adds a modelled moonlight term (Krisciunas and Schaefer 1991) to each `SkyLevel`
from a modelled Moon (a 25 % crescent and a quarter Moon, each rising mid-run or three quarters through, before dawn), re-verdicts, and
writes `synthetic-<scenario>-*.csv`. `--moon-sep` (default 60 degrees) and `--dark-nl` (dark sky, default 100 nL) tune it. Use it only if
no captured night has the Moon rising during a run.

## Two build modes

```pwsh
dotnet build tools/sky-trend-probe/SkyTrendProbe.csproj -c Release                  # main: the #2030 columns are blank
dotnet build tools/sky-trend-probe/SkyTrendProbe.csproj -c Release -p:SkyTrend=true # over #2030's Lib: they are read
```

Without `SkyTrend` the tool compiles on main, where #2030's fields do not exist, and says at start that it was built without #2030.
With it, code inside `#if SKY_TREND` reads `LightGradeVerdict.SkyOverTrend` and `SkyRising`.

### Overlay recipe (never commit #2030's code, never merge its branch)

```pwsh
git fetch origin fix/grade-sky-trend
git checkout FETCH_HEAD -- src/TianWen.Lib/Imaging/Grading/LightGrading.cs src/TianWen.Lib/Imaging/Stacking/CloudRejection.cs
dotnet build tools/sky-trend-probe/SkyTrendProbe.csproj -c Release -p:SkyTrend=true
git checkout HEAD -- src/TianWen.Lib/Imaging/Grading/LightGrading.cs src/TianWen.Lib/Imaging/Stacking/CloudRejection.cs
```

(A pipe through `Set-Content -NoNewline` joins the lines of the file with no separator in pwsh and breaks the C#, so use the
checkout form.) Check `git status` shows nothing under `src/` afterwards. Build into its own output (the build above does); do not run a plain build
in between and expect the SKY_TREND one to survive.

## Running

```pwsh
dotnet run --project tools/sky-trend-probe -c Release -- "E:/Captures/2026-09-12/LIGHT" --out D:/temp/tianwen-scratch/sky-trend/0912
dotnet run --project tools/sky-trend-probe -c Release -- --self-check
```

D: and E: are read-only: only `--out` is written, and the tool never opens a file for writing anywhere else. Put `--out` on a scratch
root, never inside a checkout. Frames are graded at most 4 at a time (`--parallel`, default 3, clamped to 4), since the machine is shared.
The assembly is named `TianWen.Lib.Tests` on purpose: `TianWen.Lib` makes its internals visible to that name, and the viewer's own path
(`RigOf`, the unit-scale helpers, `MeeusMoon`) is internal.
