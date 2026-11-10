# CI test times: before and after, Linux against Windows

Issue #2054 added the unit and functional suites on `windows-latest`, built the test projects once instead of in
every test job, and sharded every test job by class. This page keeps the tables the change was judged by, and how to
read a newer run.

    python tools/ci-test-timings.py <run id> --before <run id> --per-test 30 --min-seconds 2

A time is the SUM of a class's test durations, not wall-clock: four tests run at once, so a class waiting on a
starved thread pool reads slow here even when its own work is not. The Linux column is the `test-unit
(ubuntu-latest ...)` and `test-functional ...` jobs, summed over their shards; the Windows column the `test-windows`
shard named beside it; the before column the Linux time of run 42751729433, a green PR run of 2026-11-10 before any
of #2054 (its functional log printed no per-test times, so that column is empty for the functional suite).

## Per job, wall-clock

The time from the run's start is when gate reported; gate is the one check main's ruleset requires.

| | before (42751729433) | round 1 (42804689190) | round 2 (42814136317) |
|---|---:|---:|---:|
| build | 4m51s | 3m57s | 4m53s |
| test-unit (ubuntu-latest) | 20m27s | 27m40s | 14m39s |
| test-unit (ubuntu-24.04-arm) | 18m48s | 18m03s | 17m51s |
| test-functional | 15m21s | 11m52s | 15m59s |
| test-unit (debug) | 3m08s | 4m37s | 5m37s |
| build-windows | | | 6m01s |
| test-windows, each shard | | 6m47s to 14m27s (built itself) | 3m13s to 7m58s |
| gate, from the run's start | 25.5 min | 33.6 min (red) | 18.3 min |

### Round 3 and round 4, by job

Round 3 built the Linux test projects once per runner and suite (`build-linux`, three legs), sharded the Linux jobs
like the Windows ones (5 unit shards on each runner, 2 functional) and split `build-windows` by suite. Round 4 cut the
slow tests' causes, took the test builds' analyzers off and their catalogs from a cache, ran the Linux x64 shards from
`build`'s own build, and went to 4 unit shards and 3 functional, balanced by chain (below). A job's time is its own,
from its start; the wait is the longest any job of the row spent queued for a runner; "ended" is how far into the run
the row's last job ended.

| | round 3 (42885521250) | round 4 (42900071837) |
|---|---|---|
| build | 4m46s | 5m54s (packing the x64 test builds too) |
| build-linux (x64 unit, arm unit, x64 functional) | 6m17s, 5m53s, 5m00s | arm unit only: 3m36s, ended 4m03s in |
| build-windows | 7m28s and 7m04s, a leg per suite | 4m56s for both suites, ended 5m20s in |
| test-unit (debug) | 5m44s | 3m10s |
| test-unit (ubuntu-latest), each shard | 3m36s to 7m29s (of 5), ended 14m14s in | 4m01s to 7m41s (of 4), ended 14m02s in |
| test-unit (ubuntu-24.04-arm), each shard | 2m51s to 5m26s, waited up to 1m00s, ended 12m44s in | 3m38s to 4m37s, ended 11m00s in |
| test-functional, each shard | 4m42s to 5m15s (of 2), ended 12m00s in | 3m32s to 4m15s (of 3), ended 10m36s in |
| test-windows (unit), each shard | 4m31s to 6m11s, waited up to 2m27s, ended 15m25s in | 4m54s to 7m40s (of 4), ended 13m03s in |
| test-windows (functional), each shard | 5m34s to 7m29s, waited up to 3m17s, ended 17m53s in | 4m38s to 5m27s (of 3), ended 10m50s in |
| longest wait for a runner, any job | 3m17s | 6 s |
| gate, from the run's start | 18.0 min (red: the Windows cancel test below) | 14.2 min (green) |

Round 4 took the builds off the path (the Windows one ends 5.3 minutes in where it ended 7.9) and the runner waits
off the run (18 shards, none queued for more than 6 s), and every Windows job stayed under 8 minutes. What held its
gate was one unit shard on both OSes, 3/4, which tested for 6.5 minutes on Linux x64 and 6.2 on Windows while the other
shards took 3.5 to 4.7: its family prefixes had gathered the planetary viewer collection and PlanetarySharpeningTests
beside other CPU-heavy classes, 1067 s summed on Linux and 1228 on Windows against 603 to 927, two chains of about 5
minutes. And the Linux x64 shards waited for `build`, whose whole-solution compile with analyzers ran 4m47s; the
analyzer-free ARM64 build had ended two minutes earlier.

Two changes answer them, committed after round 4 and not yet run, since the flight's CI budget stopped further pushes:
the x64 shards run an analyzer-free `build-linux` leg of their own again (both suites in one job), and the unit shards
are rebalanced on round 4's own per-class times, 4 on Linux and 5 on Windows (whose build ends a minute later), from a
layout per shard count in `.github/test-shards.json`. On round 4's numbers the heaviest unit shard then sums 870 s on
Linux and 934 on Windows (against 1067 and 1228), and the x64 shards start about two minutes earlier.

## What the tables said, and what was done

- **The Linux x64 runner's speed moves 2x between runs of the same tree.** Round 1's `test-unit (ubuntu-latest)`
  summed 4577 s of tests in 21m31s, round 2's 2286 s, against 2471 s before. The arm leg, the Windows legs and the
  before run do not move like that. So a class's Windows to Linux ratio from one run says little near 2x, and #2092
  counts a class as over 2x only where it is so in both rounds.
- **The unit suite is not slower on Windows overall** (3531 and 3638 s summed against 4577 and 2286 s). Its classes
  over 2x Linux in both rounds are small (at most 29 s) and are filed with their numbers in #2092, most of them a short
  real delay paying the Windows timer's 15.6 ms quantum.
- **The functional suite's Windows stall left with the build.** In round 1 shard 3 of 3 summed 1083 s against 529 s
  on Linux, and its seven failures (all timeouts) fell in its first three minutes, with 1.5 s GC pauses and
  thread-pool starvation. Each shard then built its own test project, 3 to 5 minutes of jobs that took 7 to 14.5
  minutes. With `build-windows` building once (and the build's resident MSBuild and compiler servers off the test
  machine), every functional shard passed in round 2 and the suite summed 1315 s on Windows against 1348 s on Linux.
- **The Linux jobs were the long pole.** Building once per runner (`build-linux`) and sharding them like the Windows
  ones is round 3: five unit shards on each architecture and two functional shards, from the same
  `.github/test-shards.json` as Windows. The counts hold a run to about 20 jobs at once, the org plan's limit (round 1's
  x64 unit job waited three minutes for a runner), which is also why the Windows functional suite went from four
  shards to two.
- **Round 3 gated at 18.0 minutes, for two reasons, and sharding was neither.** A 5 to 7.5 minute test build sat in
  front of every shard, and 19 shards plus the builds against the org's 20 jobs at once left Windows shards queued for
  up to 3m17s. The slowest Linux shard was not the busiest: unit 4/5 summed only 629 s but held the five planetary
  viewer classes, one xUnit collection (`ViewerPlanetaryCollection`) and so one chain of 324 s, and took 6 minutes. A
  shard is never shorter than its longest chain, since xUnit runs a collection's classes, and a class's tests, one
  after another.
- **Round 4 cut the causes the runs pointed at, each measured on 4 cores (affinity 0xF, DOTNET_PROCESSOR_COUNT=4)
  before and after on one build:**
  - `SyntheticBackgroundTests.ByAreaACutHoldsKnotsPerExportCellOfItsArea` drew 360 whole cells, texture and all, to
    count their knots: 32.7 to 45.6 s on Linux x64. It now counts knots drawn onto planes of zeros through the one
    method `Cell` draws them with (`SyntheticBackground.DrawKnots`): 0.66 s.
  - `Image.AnalyseStar`, which star detection calls for every pixel over its level, zeroed its 1.3 KB annulus buffer
    on every call (a CPU trace of the synthetic plate solve put `Buffer.ZeroMemoryInternal` under it at 35 % of the
    samples). `[SkipLocalsInit]`, with its one counted buffer cleared: detection 30 to 40 % faster (795 ms against
    1189 on the RGGB fixture), every star, solution and residual the same.
  - `PlanetarySharpeningTests` rendered the same diffracted Jupiter once per test (and the viewer's planetary tests
    through `NoisyStack`), half as much CPU as all its sharpening: rendered once and copied out, 136 s against 165,
    every printed number the same.
- **Measured, and not a cause.** `ViewerE2E.PumpUntilAsync`'s full-frame paint every 10 ms: pausing up to 100 ms
  while the host wants no frame changed the five planetary viewer classes from 273 to 268 s, inside the noise, so it
  was not kept; their time is the sharpening they compare against (the slowest test sharpens the same master at every
  strength stop, once per DPI). The synthetic plate-solve theory's 7 s is 3.4 s of solve. `SaturnLimbFitTests`' and
  the rest of `PlanetarySharpeningTests`' time is the limb fit's Levenberg-Marquardt model evaluations (`DiskModel`'s
  `Evaluate` and `Blur`, from the same trace), work the tests are about.
- **Round 4's test build skips analyzers and XML documentation**, which only report and which `build` still runs over
  the same sources: a full rebuild of the unit test project's graph took 1m55s against 3m50s. The catalog
  preprocessing comes from a cache keyed on its inputs (`.github/actions/catalog-cache`), the Linux x64 shards run
  `build`'s own build (two jobs fewer), and the shards are balanced by chain as well as by sum (`.github/test-shards.json`):
  4 unit and 3 functional, 18 test jobs instead of 19.
- **`ExternalPlateSolverProcessTests.ACancelledSolveKillsTheToolAndRethrows` timed out at 60 s on Windows in round 3**,
  the first of its class to start `pwsh` in that process, and printed nothing. The test now waits for the tool's id
  or the solve, whichever comes first, logs every stage and tool line live, and the stand-in starts its child without
  a cmdlet (module auto-loading is the one first-use cost on its path). The cause is not proven; the log will say.

## Run 42900071837 (round 4) against the before run, classes at 2 s or more

### unit: 1040 classes, Linux before 2471 s summed, Linux 3149 s summed, Windows 3710 s (182 at 2 s or more shown)

| class | Linux before s | Linux s | Windows s | W/L | tests L/W | failed on W | Windows shard |
|---|---:|---:|---:|---:|---:|---:|---|
| PlanetarySharpeningTests | 152.2 | 190.3 | 281.2 | 1.5 | 39/39 |  | unit-3 |
| ClassicalStarRemoverTests | 76.7 | 152.7 | 235.2 | 1.5 | 17/17 |  | unit-2 |
| SyntheticBackgroundTests | 181.9 | 152.3 | 192.0 | 1.3 | 35/35 |  | unit-2 |
| ViewerWaveletDeriveTests | 67.5 | 166.5 | 191.8 | 1.2 | 21/21 |  | unit-2 |
| DatasetBuildRunnerTests | 96.9 | 43.0 | 168.0 | 3.9 | 18/18 |  | unit-1 |
| ViewerPlanetaryMasterTests | 36.0 | 112.0 | 111.2 | 1.0 | 8/8 |  | unit-3 |
| ViewerPlanetaryLookTests | 38.4 | 106.8 | 77.2 | 0.7 | 6/6 |  | unit-3 |
| DatasetDegradationExporterTests | 106.4 | 86.4 | 88.9 | 1.0 | 48/49 |  | unit-1 |
| SkyTextureTests | 28.8 | 69.8 | 104.3 | 1.5 | 8/8 |  | unit-4 |
| PlanetaryLimbFitTests | 98.3 | 102.1 | 102.4 | 1.0 | 13/13 |  | unit-2 |
| StarInjectionTests | 98.3 | 95.3 | 46.4 | 0.5 | 66/66 |  | unit-1 |
| PlateSolverTests | 81.5 | 55.5 | 69.5 | 1.3 | 21/31 |  | unit-1 |
| VelaMosaicFieldTests | 30.3 | 75.4 | 59.3 | 0.8 | 53/53 |  | unit-3 |
| PlanetaryDegradeTests | 41.5 | 50.6 | 68.8 | 1.4 | 29/29 |  | unit-2 |
| ViewerAutoViewTests | 20.7 | 54.9 | 67.8 | 1.2 | 5/5 |  | unit-3 |
| PlanetaryRenderTests | 67.5 | 47.7 | 41.6 | 0.9 | 12/13 |  | unit-1 |
| PlanetaryCompositionTests | 66.0 | 48.5 | 43.5 | 0.9 | 7/7 |  | unit-1 |
| N2nDenoiserTests | 13.5 | 46.8 | 61.1 | 1.3 | 18/18 |  | unit-3 |
| SaturnLimbFitTests | 57.2 | 52.2 | 55.5 | 1.1 | 7/7 |  | unit-1 |
| DatasetSessionRegistrarTests | 56.1 | 37.1 | 41.8 | 1.1 | 13/13 |  | unit-1 |
| PlanetaryBestStackTests | 29.8 | 52.8 | 38.1 | 0.7 | 3/3 |  | unit-3 |
| ViewerBestStackTests | 13.7 | 48.3 | 52.5 | 1.1 | 12/12 |  | unit-3 |
| PlanetarySpectralRatioTests | 20.7 | 46.3 | 32.8 | 0.7 | 3/3 |  | unit-3 |
| StretchTests_NewPipeline | 21.3 | 29.1 | 44.6 | 1.5 | 15/15 |  | unit-3 |
| StretchTests_DebayeredImageUnlinked | 16.2 | 32.3 | 43.6 | 1.3 | 4/4 |  | unit-3 |
| DatasetTileExporterTests | 10.0 | 38.3 | 43.2 | 1.1 | 11/11 |  | unit-1 |
| PlanetaryColourTests | 36.6 | 42.0 | 29.6 | 0.7 | 18/18 |  | unit-2 |
| PlanetaryCaptureStreamingTests | 16.6 | 8.3 | 41.6 | 5.0 | 8/8 |  | unit-3 |
| OrreryTests | 38.5 | 30.6 | 27.2 | 0.9 | 101/101 |  | unit-1 |
| OnnxTianWenDeconvolverTests | 8.8 | 33.2 | 35.6 | 1.1 | 19/19 |  | unit-3 |
| Planetary.PlanetaryChannelAlignmentTests | 35.3 | 27.3 | 16.4 | 0.6 | 11/11 |  | unit-3 |
| PlanetaryPictureTests | 31.2 | 32.0 | 30.4 | 1.0 | 6/6 |  | unit-2 |
| StarRemovalEvalTests | 21.1 | 20.6 | 29.8 | 1.4 | 10/10 |  | unit-2 |
| ColourAlignmentStackingTests | 21.9 | 27.7 | 28.7 | 1.0 | 9/9 |  | unit-4 |
| Cr2ImportTests | 23.1 | 15.5 | 25.0 | 1.6 | 3/3 |  | unit-4 |
| StackingPipelineRgbBayerSyntheticTest | 2.8 | 6.2 | 24.9 | 4.0 | 2/2 |  | unit-4 |
| PlanetaryLookFitsTests | 18.7 | 24.9 | 20.2 | 0.8 | 2/2 |  | unit-3 |
| PlanetaryGhostTests | 20.1 | 23.2 | 24.5 | 1.1 | 11/11 |  | unit-3 |
| ApertureFromSpectrumTests | 21.4 | 13.8 | 24.3 | 1.8 | 8/8 |  | unit-4 |
| ExternalPlateSolverProcessTests | 20.5 | 20.3 | 23.2 | 1.1 | 4/4 |  | unit-4 |
| Tycho2PartialCatalogTests | 23.0 | 13.7 | 18.2 | 1.3 | 9/9 |  | unit-4 |
| NightCalendarTests | 21.6 | 20.0 | 22.1 | 1.1 | 23/23 |  | unit-4 |
| GuideStarAcceptanceTests | 21.9 | 13.5 | 15.5 | 1.1 | 53/53 |  | unit-4 |
| PlanetaryFrameTimeOrderTests | 11.4 | 17.5 | 21.3 | 1.2 | 6/6 |  | unit-3 |
| SaturationStackingTests | 11.9 | 20.1 | 18.5 | 0.9 | 7/7 |  | unit-4 |
| StackingIntermediateDumpTests | 3.5 | 7.2 | 19.9 | 2.8 | 3/3 |  | unit-4 |
| CorruptReadoutTests | 3.7 | 5.3 | 19.5 | 3.7 | 5/5 |  | unit-4 |
| LightGradingTests | 7.3 | 19.5 | 17.7 | 0.9 | 42/42 |  | unit-4 |
| PoleMasterFrameSolveTests | 10.4 | 13.0 | 19.4 | 1.5 | 2/2 |  | unit-4 |
| CometModelTests | 11.9 | 15.4 | 18.7 | 1.2 | 10/10 |  | unit-4 |
| OrreryCometTests | 18.1 | 11.8 | 10.4 | 0.9 | 35/35 |  | unit-1 |
| PlanetaryTrainingPairsTests | 6.9 | 14.1 | 17.9 | 1.3 | 2/2 |  | unit-3 |
| DALCameraVideoTests | 2.1 | 3.2 | 15.7 | 5.0 | 35/35 |  | unit-4 |
| PlanetaryPointKeepTests | 2.2 | 4.2 | 15.1 | 3.6 | 2/2 |  | unit-3 |
| DatasetCrossNightExporterTests | 6.4 | 14.7 | 6.1 | 0.4 | 17/17 |  | unit-1 |
| ViewerAutoFrameDemandTests | 8.2 | 11.6 | 14.4 | 1.2 | 8/8 |  | unit-3 |
| SkyMapPlanetDiscTests | 4.1 | 7.9 | 14.1 | 1.8 | 36/36 |  | unit-4 |
| ViewerBestViewTests | 5.6 | 8.1 | 14.0 | 1.7 | 2/2 |  | unit-3 |
| BiasPedestalFallbackTests | 1.8 | 9.8 | 13.4 | 1.4 | 1/1 |  | unit-4 |
| PlanetaryIdentificationTests | 7.8 | 11.8 | 13.4 | 1.1 | 10/10 |  | unit-3 |
| SimbadMergeSnapshotTests | 8.5 | 13.3 | 12.1 | 0.9 | 20/20 |  | unit-4 |
| StretchTests_MonoImage | 1.9 | 5.4 | 13.2 | 2.4 | 2/2 |  | unit-3 |
| AlignmentPointMatchingTests | 10.0 | 13.0 | 10.0 | 0.8 | 3/3 |  | unit-4 |
| Tycho2RegionBakeProbe | 6.2 | 9.0 | 13.0 | 1.4 | 2/3 |  | unit-4 |
| SyntheticStarDetectionTests | 2.0 | 12.3 | 4.8 | 0.4 | 26/26 |  | unit-2 |
| InheritedWhiteBalanceStampTests | 12.3 | 10.8 | 3.1 | 0.3 | 2/2 |  | unit-4 |
| SaturnDerotationTests | 12.2 | 7.2 | 6.8 | 0.9 | 2/2 |  | unit-1 |
| FindBestFocusTests | 8.4 | 5.8 | 11.9 | 2.1 | 5/5 |  | unit-4 |
| TexturedHoleFillTests | 5.0 | 6.5 | 11.4 | 1.7 | 1/1 |  | unit-4 |
| DrizzleParallelBitIdentityTests | 3.1 | 11.1 | 3.1 | 0.3 | 13/13 |  | unit-4 |
| GpuStretchPipelineTests | 11.0 | 9.5 | 0.0 | 0.0 | 26/0 |  |  |
| DrizzleOutlierRejectionTests | 1.8 | 1.5 | 10.4 | 6.7 | 5/5 |  | unit-4 |
| DatasetPsfNoiseReportTests | 6.8 | 10.2 | 5.3 | 0.5 | 17/17 |  | unit-1 |
| PlanetaryFinishingTests | 2.3 | 6.0 | 10.1 | 1.7 | 7/7 |  | unit-3 |
| CanonPreviewRobustnessTests | 9.7 | 7.0 | 7.6 | 1.1 | 19/19 |  | unit-4 |
| DebayerRegressionTests | 6.4 | 6.7 | 9.6 | 1.4 | 5/5 |  | unit-4 |
| StackingPipelineRgbBayerDrizzleTest | 4.2 | 9.1 | 3.2 | 0.3 | 3/3 |  | unit-4 |
| SaveTargetExtensionTests | 2.4 | 2.8 | 9.1 | 3.3 | 10/10 |  | unit-4 |
| TiffRoundTripTests | 6.2 | 9.0 | 7.2 | 0.8 | 6/6 |  | unit-4 |
| StarRemoverRunnerTests | 7.9 | 7.9 | 8.9 | 1.1 | 17/17 |  | unit-2 |
| Tycho2RegionSelectorTests | 7.7 | 7.3 | 8.9 | 1.2 | 7/7 |  | unit-4 |
| FakeFieldSolveProbe | 8.8 | 4.3 | 1.7 | 0.4 | 6/6 |  | unit-4 |
| Tycho2LiteLookupParityTests | 2.0 | 3.7 | 8.8 | 2.4 | 1/1 |  | unit-4 |
| CometRepositoryTests | 2.2 | 3.4 | 8.4 | 2.5 | 17/17 |  | unit-4 |
| SyntheticClusterExportTests | 2.2 | 8.2 | 2.9 | 0.3 | 4/4 |  | unit-2 |
| DeviceOwnershipTests | 7.6 | 8.0 | 4.0 | 0.5 | 20/20 |  | unit-4 |
| FindStarsFromFitsFileTests | 4.6 | 7.5 | 6.9 | 0.9 | 12/12 |  | unit-4 |
| ViewerSkyBackdropAgreementTests | 1.8 | 4.3 | 7.5 | 1.7 | 10/10 |  | unit-3 |
| StandardErrorPlaneTests | 3.3 | 7.5 | 2.1 | 0.3 | 22/22 |  | unit-4 |
| PolarAlignmentRampIntegrationTests | 4.3 | 6.7 | 7.4 | 1.1 | 1/1 |  | unit-4 |
| PlanetaryInverseTests | 4.9 | 7.3 | 6.6 | 0.9 | 8/8 |  | unit-3 |
| ContrastBoostTests | 1.0 | 2.0 | 7.2 | 3.6 | 15/15 |  | unit-4 |
| StretchTests_ColorImageLuma | 4.8 | 7.2 | 6.0 | 0.8 | 1/1 |  | unit-3 |
| PlanetaryLimbKernelTests | 4.7 | 7.2 | 4.4 | 0.6 | 1/1 |  | unit-2 |
| Tycho2BulkInjectionTests | 4.7 | 4.7 | 7.1 | 1.5 | 5/5 |  | unit-4 |
| SharedAppDataFileTests | 1.9 | 5.3 | 7.0 | 1.3 | 4/5 |  | unit-4 |
| ViewerDisplayMemoryE2ETests | 0.4 | 0.5 | 6.9 | 14.3 | 16/16 |  | unit-3 |
| PlanetaryApStackTests | 2.3 | 6.9 | 1.1 | 0.2 | 12/12 |  | unit-3 |
| NightSummaryTests | 3.7 | 6.8 | 4.6 | 0.7 | 10/10 |  | unit-4 |
| SolverSamplingFloorTests | 3.1 | 2.1 | 6.7 | 3.1 | 6/6 |  | unit-4 |
| PlanetaryCeilingsTests | 1.8 | 3.6 | 6.5 | 1.8 | 8/8 |  | unit-3 |
| DebayerRegionTests | 2.9 | 3.9 | 6.4 | 1.7 | 2/2 |  | unit-4 |
| SolveHintCacheTests | 2.7 | 3.1 | 6.4 | 2.1 | 4/4 |  | unit-4 |
| StarDetectionRetryProbe | 1.0 | 3.6 | 6.2 | 1.7 | 7/7 |  | unit-2 |
| PlateSolveParityRaceTests | 0.3 | 0.6 | 6.0 | 10.8 | 5/5 |  | unit-4 |
| Cr3ImportTests | 5.9 | 3.0 | 4.3 | 1.5 | 2/2 |  | unit-4 |
| CometMaskTests | 5.5 | 1.8 | 3.4 | 1.9 | 10/10 |  | unit-4 |
| SkyMapDarkNebulaScreenFilterTests | 4.1 | 5.0 | 5.4 | 1.1 | 6/6 |  | unit-4 |
| DatasetSplitAndParityTests | 0.8 | 5.2 | 2.7 | 0.5 | 7/7 |  | unit-1 |
| ClassicalBackgroundExtractorTests | 1.1 | 5.0 | 2.8 | 0.6 | 19/19 |  | unit-2 |
| RealFrameSolveTests | 3.0 | 5.0 | 3.7 | 0.7 | 2/2 |  | unit-4 |
| FakeTimePumpTests | 1.6 | 4.9 | 3.0 | 0.6 | 6/6 |  | unit-4 |
| StretchLinkedRealImageWhiteBalanceTests | 0.3 | 1.6 | 4.9 | 3.1 | 2/2 |  | unit-3 |
| OrreryGpuTests | 4.9 | 4.1 | 0.0 | 0.0 | 7/0 |  |  |
| ViewerEnhancePopoverTests | 0.6 | 2.6 | 4.8 | 1.8 | 41/41 |  | unit-3 |
| TonightsBestTests | 4.8 | 3.9 | 3.0 | 0.8 | 15/15 |  | unit-4 |
| SkyMapDeepLinkTests | 3.2 | 3.5 | 4.7 | 1.3 | 7/7 |  | unit-4 |
| CaptureStatisticsFileTests | 1.7 | 4.4 | 1.7 | 0.4 | 3/3 |  | unit-4 |
| ViewerTonePopoverTests | 0.2 | 0.6 | 4.4 | 7.1 | 12/12 |  | unit-3 |
| LiveCloudJudgeTests | 0.6 | 0.6 | 4.3 | 6.9 | 7/7 |  | unit-4 |
| LayoutRectSweep | 3.0 | 4.1 | 3.1 | 0.8 | 1/1 |  | unit-4 |
| DatasetGradientFrameExporterTests | 0.8 | 2.5 | 4.0 | 1.6 | 7/7 |  | unit-1 |
| ImageTransformTests | 3.9 | 2.4 | 0.5 | 0.2 | 5/5 |  | unit-4 |
| PlanetaryCaptureLiveViewTests | 1.9 | 0.8 | 3.8 | 5.0 | 10/10 |  | unit-3 |
| PlanetaryMoonProbeTests | 2.1 | 3.8 | 3.4 | 0.9 | 8/8 |  | unit-3 |
| StretchedNoiseTests | 1.2 | 3.8 | 3.4 | 0.9 | 10/10 |  | unit-3 |
| FindStarsFromCameraImageTests | 3.8 | 1.6 | 1.4 | 0.9 | 6/6 |  | unit-4 |
| ExpectedSkyModelTests | 0.8 | 3.1 | 3.8 | 1.2 | 8/8 |  | unit-4 |
| InfoPanelLiveStackViewerTests | 3.7 | 3.3 | 3.5 | 1.0 | 6/6 |  | unit-4 |
| NeuralGuideTrainerTests | 3.6 | 1.6 | 2.2 | 1.4 | 4/4 |  | unit-4 |
| SyntheticLuminanceTests | 0.2 | 3.5 | 0.9 | 0.3 | 4/4 |  | unit-2 |
| ChannelOffsetsTests | 2.3 | 1.7 | 3.5 | 2.1 | 15/15 |  | unit-4 |
| StretchTests_ColorImagelinked | 0.4 | 0.9 | 3.5 | 4.0 | 1/1 |  | unit-3 |
| HdHipCrossSnapshotTests | 1.2 | 1.6 | 3.5 | 2.3 | 2/2 |  | unit-4 |
| StarMagnitudeIndexTests | 3.1 | 2.3 | 3.5 | 1.5 | 14/14 |  | unit-2 |
| CelestialObjectDBTests | 1.9 | 2.6 | 3.5 | 1.4 | 356/356 |  | unit-4 |
| StarDetectionSkyTriggerTests | 0.6 | 3.4 | 3.0 | 0.9 | 1/1 |  | unit-2 |
| TiffReadoutTileTests | 2.0 | 3.4 | 2.7 | 0.8 | 52/52 |  | unit-4 |
| SkyMapPinnedOnlyGatherTests | 2.8 | 3.4 | 3.4 | 1.0 | 7/7 |  | unit-4 |
| RegistrationComparisonTests | 3.1 | 3.4 | 2.1 | 0.6 | 10/10 |  | unit-4 |
| RaDecCellEnumerationTests | 1.2 | 1.3 | 3.3 | 2.5 | 2/2 |  | unit-4 |
| SpccWhiteReferenceProbe | 1.9 | 2.3 | 3.2 | 1.4 | 1/1 |  | unit-4 |
| DatasetStarlessReportTests | 1.1 | 3.1 | 2.8 | 0.9 | 1/1 |  | unit-1 |
| SaturnRingFaceTests | 1.1 | 1.4 | 3.1 | 2.2 | 5/5 |  | unit-1 |
| SyntheticCatalogProjectionTests | 1.2 | 2.0 | 2.9 | 1.4 | 6/6 |  | unit-2 |
| OrreryNightTests | 1.0 | 2.9 | 0.6 | 0.2 | 2/2 |  | unit-1 |
| FaintFrameStretchStatsTests | 1.5 | 1.3 | 2.8 | 2.1 | 8/8 |  | unit-4 |
| PhotometricTransparencyTests | 2.1 | 2.1 | 2.8 | 1.3 | 8/8 |  | unit-4 |
| PlanetaryCaptureInterludeTests | 1.0 | 1.0 | 2.8 | 2.8 | 11/12 |  | unit-3 |
| PlanetaryReframeTests | 2.8 | 2.3 | 2.4 | 1.1 | 3/3 |  | unit-3 |
| GuiWindowTests | 0.0 | 0.0 | 2.7 |  | 0/1 |  | unit-4 |
| ExpectedSkyFitterTests | 1.7 | 2.7 | 0.9 | 0.3 | 3/3 |  | unit-4 |
| CelestialObjectDBBenchmarkTests | 1.6 | 2.2 | 2.6 | 1.2 | 4/4 |  | unit-4 |
| CoverageEdgeWalkTests | 1.6 | 1.5 | 2.6 | 1.7 | 11/11 |  | unit-4 |
| ViewerDisplayMemoryTests | 0.0 | 0.2 | 2.6 | 12.0 | 6/6 |  | unit-3 |
| SkyMapHoverAndPictureTests | 1.1 | 1.7 | 2.6 | 1.5 | 38/38 |  | unit-4 |
| IncrementalSolverTests | 2.5 | 1.9 | 2.0 | 1.1 | 13/13 |  | unit-4 |
| SpccBeforeEnhanceTests | 0.5 | 1.4 | 2.5 | 1.8 | 30/30 |  | unit-4 |
| PlanetaryFrameDerotationTests | 0.1 | 0.2 | 2.5 | 15.0 | 7/7 |  | unit-3 |
| MoffatCompositionTests | 1.8 | 1.6 | 2.5 | 1.6 | 14/14 |  | unit-4 |
| ViewerGradeVerdictE2ETests | 1.2 | 2.5 | 1.9 | 0.8 | 8/8 |  | unit-3 |
| PlanetaryBayerTwinTests | 0.6 | 1.8 | 2.5 | 1.3 | 7/7 |  | unit-3 |
| PlanetaryPowerSpectrumTests | 1.8 | 2.4 | 2.3 | 1.0 | 2/2 |  | unit-3 |
| PlanetaryWaveletGainsTests | 2.0 | 2.4 | 1.3 | 0.6 | 6/6 |  | unit-3 |
| SharpenPipelineTests | 0.9 | 1.4 | 2.3 | 1.6 | 29/29 |  | unit-4 |
| Tycho2MemberManifestTests | 1.3 | 2.0 | 2.3 | 1.2 | 17/17 |  | unit-4 |
| ViewerObjectSelectionTests | 0.6 | 2.3 | 1.8 | 0.8 | 32/32 |  | unit-3 |
| ModelContractTests | 0.1 | 0.1 | 2.3 | 31.3 | 32/32 |  | unit-4 |
| StackingCloudEmptiedGroupTest | 0.8 | 2.3 | 0.9 | 0.4 | 3/3 |  | unit-4 |
| OrreryPanelTests | 2.3 | 1.6 | 1.8 | 1.1 | 12/12 |  | unit-1 |
| OrreryCompassTests | 2.2 | 1.8 | 1.9 | 1.0 | 7/7 |  | unit-1 |
| DatasetGradientReportTests | 0.4 | 1.1 | 2.1 | 2.0 | 21/21 |  | unit-1 |
| SkyMapAtlasNightCurveTests | 1.0 | 1.2 | 2.1 | 1.8 | 20/20 |  | unit-4 |
| ViewerSkyPaletteRoutingTests | 0.1 | 1.6 | 2.1 | 1.3 | 2/2 |  | unit-3 |
| QuadScaleRecoveryTests | 1.2 | 1.1 | 2.1 | 1.9 | 5/5 |  | unit-4 |
| ViewerWaveletDialRangeTests | 2.1 | 0.2 | 0.1 | 0.4 | 5/5 |  | unit-3 |
| SyntheticClusterSceneTests | 1.5 | 1.7 | 2.1 | 1.3 | 14/14 |  | unit-2 |
| GpuVngDebayerParityTests | 2.1 | 1.7 | 0.0 | 0.0 | 20/0 |  |  |
| ViewerWhiteBalancePopoverTests | 0.2 | 0.7 | 2.1 | 3.1 | 16/16 |  | unit-3 |
| SessionAutoFocusFrameCaptureTests | 2.0 | 1.6 | 1.7 | 1.1 | 2/2 |  | unit-4 |
| ViewerRepaintResidueTests | 0.3 | 0.6 | 2.0 | 3.3 | 7/7 |  | unit-3 |
| StarStatisticsTests | 0.4 | 1.9 | 2.0 | 1.1 | 10/10 |  | unit-2 |

#### unit: the 30 slowest tests

| test | Linux before s | Linux s | Windows s |
|---|---:|---:|---:|
| ClassicalStarRemoverTests.AColourFieldWithUnequalChannelSkiesStillHasItsStarsSubtracted | 16.8 | 32.4 | 60.2 |
| PlanetaryBestStackTests.ADerotatedRunCountsItsLimbFitLoadsAndReportsEveryStageOnceInOrder | 29.6 | 52.7 | 38.0 |
| SkyTextureTests.SparseSharpStructureFillsTheTailThatTheMadIgnores | 4.6 | 6.0 | 50.1 |
| ViewerPlanetaryMasterTests.AMastersStopShowsWhatPlanetarySharpenWritesAtThatStrength(dpi: 1) | 10.7 | 43.5 | 32.6 |
| PlanetaryLimbFitTests.EachChannelIsFittedAgainstItsOwnLimbAndOneWithNoDiskKeepsTheSharedFit | 40.5 | 39.6 | 36.1 |
| PlanetaryCaptureStreamingTests.AutoRecenterOffLeavesTheRoiWindowFixed | 12.9 | 7.0 | 38.4 |
| SyntheticBackgroundTests.ByAreaACutHoldsKnotsPerExportCellOfItsArea | 36.0 | 0.6 | 0.4 |
| ViewerPlanetaryLookTests.TheLiveStackedViewDrawsTheLookOnTheMastersADeriveBalanced(dpi: 1) | 13.3 | 35.3 | 14.7 |
| ViewerPlanetaryLookTests.TheLiveStackedViewDrawsTheLookOnTheMastersADeriveBalanced(dpi: 1.5) | 11.4 | 34.8 | 19.4 |
| ViewerPlanetaryMasterTests.AMastersStopShowsWhatPlanetarySharpenWritesAtThatStrength(dpi: 1.5) | 10.8 | 34.3 | 29.8 |
| N2nDenoiserTests.TheChunkSeamsDoNotShowInTheStitchedOutput(overlap: 128) | 8.1 | 26.8 | 33.3 |
| ViewerWaveletDeriveTests.DeriveOnAColourCaptureMovesEveryLaterMastersColoursOntoGreen(dpi: 1.5) | 10.7 | 25.0 | 33.1 |
| ViewerWaveletDeriveTests.DeriveTakesTheBalanceSaturationChosenInThePanel(dpi: 1) | 10.6 | 32.6 | 31.0 |
| PlanetaryColourTests.ASaturnsGlobeIsBalancedToSaturnsColourWhereItsRingsLeaveItClear | 28.4 | 32.2 | 15.8 |
| PlanetarySharpeningTests.ADerivationReportsItsStagesInOrderOnAColourMaster | 11.6 | 16.7 | 31.4 |
| PlanetarySpectralRatioTests.TheFitFindsTheR0ARatioWasMadeAt | 14.6 | 30.6 | 21.9 |
| ViewerWaveletDeriveTests.DeriveOnAColourCaptureMovesEveryLaterMastersColoursOntoGreen(dpi: 1) | 10.9 | 24.5 | 30.2 |
| StarInjectionTests.AMeasureTheFitCannotReachIsShrunkTowardIndependent | 29.5 | 23.1 | 12.2 |
| SyntheticBackgroundTests.ASteeredTextureRunsAlongThePlatesCoarseContoursWhereAnIsotropicOneDoesNot | 5.2 | 7.0 | 29.5 |
| ViewerWaveletDeriveTests.DeriveTakesTheBalanceSaturationChosenInThePanel(dpi: 1.5) | 11.0 | 29.0 | 27.9 |
| SkyTextureTests.WhiteNoiseReadsAFlatSpectrumAndNoKurtosis | 5.8 | 28.9 | 2.2 |
| SaturnLimbFitTests.TheLimbFitFindsAnEdgeOnSaturnWhereItWasPut(instant: "2025-11-25T19:00:00Z") | 25.1 | 17.5 | 28.8 |
| StarInjectionTests.AFlatTruthReadsTheSameGainInEveryBandOnceTheSkyBiasIsTakenOff | 18.1 | 28.5 | 9.4 |
| SyntheticBackgroundTests.AWanderLoosensTheTieToTheCoarseContoursButKeepsTheTextureOneWay | 21.1 | 27.6 | 19.8 |
| PlanetaryCompositionTests.TheJoinAveragesOnlyTheStacksThatReachAPixelThroughTheStepFilesAsInTheRecipe | 26.7 | 17.8 | 16.6 |
| SyntheticBackgroundTests.TailsFromASmoothPlateDrawASmootherTextureThanTheOneWidth | 26.6 | 20.6 | 24.5 |
| OnnxTianWenDeconvolverTests.TheShippedDeconvolverIsThePythonRuntime | 6.6 | 23.6 | 25.9 |
| DatasetBuildRunnerTests.Run_Resume_RedoesASessionWhoseInputsMovedOrWhoseMasterIsIncomplete_AndOnlyThat | 10.1 | 4.8 | 24.0 |
| DatasetBuildRunnerTests.Run_Resume_RedoesAMovedSession_AndRebuildsOnlyTheNamedSessions | 12.2 | 6.5 | 23.5 |
| SyntheticBackgroundTests.AFineSteerFollowsThePlatesOwnFineStructureWhereItDisagreesWithTheCoarse | 13.4 | 15.1 | 23.3 |

### functional: 109 classes, Linux before 0 s summed, Linux 1171 s summed, Windows 1389 s (65 at 2 s or more shown)

(run 42751729433 printed no per-test times for this suite, so its before column is empty)

| class | Linux before s | Linux s | Windows s | W/L | tests L/W | failed on W | Windows shard |
|---|---:|---:|---:|---:|---:|---:|---|
| FakeCameraMountCouplingTests | 0.0 | 139.4 | 152.3 | 1.1 | 15/15 |  | functional-2 |
| NodeRunsProcessTests | 0.0 | 136.1 | 142.3 | 1.0 | 2/2 |  | functional-1 |
| SessionImagingTests | 0.0 | 51.3 | 126.7 | 2.5 | 16/16 |  | functional-1 |
| SessionObservationLoopTests | 0.0 | 86.1 | 92.6 | 1.1 | 18/18 |  | functional-2 |
| EquipmentFlowProcessTests | 0.0 | 73.5 | 75.9 | 1.0 | 1/1 |  | functional-2 |
| MotionOperationTests | 0.0 | 42.3 | 51.2 | 1.2 | 23/23 |  | functional-1 |
| NodePlanetaryTests | 0.0 | 42.4 | 49.2 | 1.2 | 11/11 |  | functional-1 |
| QuitThroughTheNodeTests | 0.0 | 47.8 | 43.5 | 0.9 | 11/11 |  | functional-3 |
| NodePolarAlignmentTests | 0.0 | 37.5 | 42.7 | 1.1 | 7/7 |  | functional-1 |
| GuideDarkLibraryNodeTests | 0.0 | 33.3 | 11.9 | 0.4 | 4/4 |  | functional-3 |
| NodeKeeperProcessTests | 0.0 | 16.9 | 31.0 | 1.8 | 7/7 |  | functional-1 |
| SlewLessSessionTests | 0.0 | 30.4 | 24.1 | 0.8 | 3/3 |  | functional-2 |
| PlanetarySequenceThroughTheGuiTests | 0.0 | 26.4 | 29.5 | 1.1 | 1/1 |  | functional-2 |
| PlanetaryRecordingThroughTheNodeTests | 0.0 | 23.7 | 11.0 | 0.5 | 4/4 |  | functional-2 |
| LocalNodeLauncherTests | 0.0 | 16.4 | 21.4 | 1.3 | 20/20 |  | functional-2 |
| NodeLiveViewTests | 0.0 | 18.4 | 20.7 | 1.1 | 6/6 |  | functional-1 |
| SessionPhaseTests | 0.0 | 15.5 | 19.2 | 1.2 | 6/6 |  | functional-3 |
| NodeGuidingTests | 0.0 | 4.9 | 18.3 | 3.8 | 4/4 |  | functional-3 |
| GuiContextGatingTests | 0.0 | 9.1 | 18.1 | 2.0 | 9/9 |  | functional-3 |
| MirrorParityTests | 0.0 | 12.7 | 17.7 | 1.4 | 1/1 |  | functional-1 |
| PlanetaryThroughTheNodeTests | 0.0 | 17.6 | 11.2 | 0.6 | 5/5 |  | functional-2 |
| MeridianFlipVerificationSessionTests | 0.0 | 15.3 | 17.3 | 1.1 | 5/5 |  | functional-1 |
| GuideLoopTests | 0.0 | 17.1 | 12.0 | 0.7 | 19/19 |  | functional-3 |
| NodeJournalProcessTests | 0.0 | 10.9 | 17.1 | 1.6 | 3/3 |  | functional-1 |
| NodeJournalServiceTests | 0.0 | 13.7 | 16.9 | 1.2 | 19/19 |  | functional-3 |
| EquipmentThroughTheNodeTests | 0.0 | 14.1 | 16.7 | 1.2 | 10/10 |  | functional-1 |
| LocalNodeConnectionTests | 0.0 | 14.5 | 16.4 | 1.1 | 8/8 |  | functional-2 |
| SessionCloudTests | 0.0 | 13.8 | 16.1 | 1.2 | 4/4 |  | functional-2 |
| NodeAccessTests | 0.0 | 13.6 | 16.0 | 1.2 | 17/17 |  | functional-3 |
| NodePresenceTests | 0.0 | 15.1 | 15.2 | 1.0 | 2/2 |  | functional-3 |
| RigSiteTests | 0.0 | 5.8 | 13.5 | 2.3 | 6/6 |  | functional-3 |
| NodeFrameStreamTests | 0.0 | 10.5 | 12.9 | 1.2 | 6/6 |  | functional-3 |
| LiveSessionThroughTheNodeTests | 0.0 | 12.2 | 11.9 | 1.0 | 7/7 |  | functional-3 |
| SessionFilterTests | 0.0 | 3.7 | 11.1 | 3.0 | 3/3 |  | functional-3 |
| CliVerbsThroughTheNodeTests | 0.0 | 8.4 | 10.5 | 1.2 | 14/14 |  | functional-3 |
| RunsThroughTheNodeTests | 0.0 | 8.8 | 9.1 | 1.0 | 6/6 |  | functional-3 |
| DeviceStateTests | 0.0 | 8.3 | 8.9 | 1.1 | 3/3 |  | functional-3 |
| NodeActiveProfileTests | 0.0 | 2.4 | 8.4 | 3.5 | 3/3 |  | functional-1 |
| TuiEquipmentSlotClickTests | 0.0 | 7.7 | 8.2 | 1.1 | 8/8 |  | functional-3 |
| SessionAutoFocusTests | 0.0 | 5.7 | 7.3 | 1.3 | 7/7 |  | functional-3 |
| NodeRunLifecycleTests | 0.0 | 5.4 | 7.2 | 1.3 | 13/13 |  | functional-3 |
| NodeSolveTests | 0.0 | 4.4 | 7.0 | 1.6 | 4/4 |  | functional-3 |
| AlpacaServerRoundTripTests | 0.0 | 2.6 | 6.5 | 2.5 | 15/15 |  | functional-3 |
| RigCommandRoutingTests | 0.0 | 5.9 | 6.3 | 1.1 | 3/3 |  | functional-3 |
| PlanetaryFilterChangeTests | 0.0 | 3.1 | 6.0 | 1.9 | 2/2 |  | functional-3 |
| SessionLifecycleTests | 0.0 | 3.6 | 5.7 | 1.6 | 31/31 |  | functional-3 |
| LocalProfileAtConnectTests | 0.0 | 5.5 | 5.5 | 1.0 | 1/1 |  | functional-3 |
| RigControlTests | 0.0 | 4.1 | 5.0 | 1.2 | 2/2 |  | functional-3 |
| NodePreviewExposureTests | 0.0 | 4.1 | 4.8 | 1.2 | 5/5 |  | functional-3 |
| GuiNodeHarnessAskAgainTests | 0.0 | 3.6 | 4.8 | 1.3 | 3/3 |  | functional-3 |
| HostingApiTests | 0.0 | 0.9 | 4.6 | 5.1 | 30/30 |  | functional-3 |
| SharedMemoryProcessTests | 0.0 | 3.4 | 4.2 | 1.2 | 1/1 |  | functional-3 |
| RemoteRigContactTests | 0.0 | 2.0 | 4.2 | 2.1 | 1/1 |  | functional-3 |
| NodeDarkLibraryTests | 0.0 | 3.1 | 4.0 | 1.3 | 5/5 |  | functional-3 |
| NodePlanetaryFindTests | 0.0 | 2.4 | 3.4 | 1.4 | 1/1 |  | functional-1 |
| CameraOperationTests | 0.0 | 0.2 | 3.4 | 15.9 | 9/9 |  | functional-3 |
| PlanetaryReadoutsThroughTheNodeTests | 0.0 | 3.1 | 2.2 | 0.7 | 1/1 |  | functional-2 |
| NodeFrameTests | 0.0 | 2.2 | 3.1 | 1.4 | 8/8 |  | functional-3 |
| NodeActuationGateTests | 0.0 | 0.7 | 3.1 | 4.2 | 27/27 |  | functional-3 |
| SessionScoutAndProbeTests | 0.0 | 1.7 | 3.0 | 1.7 | 14/14 |  | functional-3 |
| PlanetaryFilterThroughTheNodeTests | 0.0 | 2.3 | 2.6 | 1.1 | 1/1 |  | functional-2 |
| GuiderCalibrationOnFakeGuideCameraTests | 0.0 | 1.8 | 2.5 | 1.4 | 1/1 |  | functional-3 |
| NodeGuideLoopTests | 0.0 | 2.1 | 2.5 | 1.2 | 3/3 |  | functional-3 |
| TuiLiveSessionRigRowsTests | 0.0 | 2.4 | 2.3 | 1.0 | 2/2 |  | functional-3 |
| ControlRequestOnThisComputerTests | 0.0 | 2.3 | 2.3 | 1.0 | 2/2 |  | functional-3 |

#### functional: the 30 slowest tests

| test | Linux before s | Linux s | Windows s |
|---|---:|---:|---:|
| EquipmentFlowProcessTests.TheEquipmentTabsFlowsRunOverTheSocketAgainstASpawnedNode |  | 73.5 | 75.9 |
| NodeRunsProcessTests.AnInteractiveRunStopsAGraceAfterItsLastClientGoesAndAClientBackWithinItKeepsIt |  | 70.4 | 74.3 |
| NodeRunsProcessTests.ASessionAndADarkLibraryGoOnWithTheirLastClientGone |  | 65.6 | 68.0 |
| FakeCameraMountCouplingTests.GivenBuiltInGuiderWithMisalignedSkywatcherAndCameraPulseWhenCalibrateAndGuideThenStaysLockedWhileMountDrifts |  | 38.9 | 43.4 |
| SessionImagingTests.GivenFilterLadderAlternatingEveryFrameWhenFocusDriftsSlowlyThenRefocusFires |  | 9.3 | 32.4 |
| NodePolarAlignmentTests.AWatchedRunGoesOnAClientBackWithinTheGraceKeepsItAndAnUnwatchedOneStops |  | 32.2 | 32.2 |
| GuideDarkLibraryNodeTests.GuideDarkLibraryTakesAFrameSetPerExposureInTheGuideCamerasFolderAndTheLoopAppliesThem |  | 31.7 | 9.6 |
| PlanetarySequenceThroughTheGuiTests.AStartedSequenceRunsOnTheNodeAndThePanelsStateFollowsItToItsEnd |  | 26.4 | 29.5 |
| FakeCameraMountCouplingTests.FreshNeuralModelTrainsStablyAndSavesNewSample |  | 22.8 | 22.8 |
| FakeCameraMountCouplingTests.GivenCloudWhenGuidingRestartsWithReusedCalibrationThenAcquisitionFailsLoudly |  | 18.3 | 22.1 |
| QuitThroughTheNodeTests.WithACameraCoolingItOffersToWarmItUpAndTheNodeDoes |  | 21.4 | 21.5 |
| FakeCameraMountCouplingTests.GivenCloudDuringGuidingThenStatusReportsLostLockAndRecoversOnClear |  | 16.9 | 21.0 |
| FakeCameraMountCouplingTests.GivenCloudDuringFirstCalibrationWhenItClearsThenRetrySucceedsAndGuides |  | 18.5 | 20.4 |
| FakeCameraMountCouplingTests.GivenGuidingWhenDitherThenSettleCompletesViaGetSettleProgressAlone |  | 20.4 | 17.8 |
| SessionImagingTests.GivenStartOfNightAutoFocusWhenFocusDriftsAtALongerExposureThenRefocusFiresAndFiresAgain |  | 5.5 | 19.1 |
| SessionObservationLoopTests.GivenSkywatcherWithLimitsWhenTargetCrossesMeridianThenItFlipsAndTheLimitStaysClear |  | 15.1 | 18.6 |
| MirrorParityTests.ASessionSeenThroughAMirrorMatchesItSeenInProcess |  | 12.7 | 17.7 |
| SessionObservationLoopTests.GivenSkywatcherWithAFlipConfiguredLaterThanTheLimitThenTheLimitStopsTheRunAsALimit |  | 15.7 | 15.7 |
| SessionObservationLoopTests.GivenAcrossMeridianTargetWhenHACrossesDeadbandThenFlipAndContinueImaging |  | 15.3 | 4.8 |
| NodePresenceTests.APromptWaitsForAWindowThatKeepsDrawingAndIsLetGoOnceItFreezes |  | 15.0 | 15.0 |
| LocalNodeConnectionTests.AWindowWhoseNodeWentFindsItsNodeAgain |  | 14.2 | 14.7 |
| MotionOperationTests.AGotoAwayFromThePoleNeverReadsTheSiteLatitude |  | 13.7 | 14.1 |
| NodePlanetaryTests.ARecordingFinishesItsDurationThoughNobodyWatchesAndTheLiveViewStopsAGraceAfter |  | 13.3 | 13.8 |
| MotionOperationTests.AGotoAtTheSitesPoleGoesHomeAndAnyOtherDoesNot(dec: 70, expectedHomeCalls: 0, expectedStep: null) |  | 13.6 | 13.8 |
| SlewLessSessionTests.AFlipTurnsRaAHalfTurnAtOnceThenWaitsForTheOperatorToSwingDecOverThePole |  | 12.5 | 9.8 |
| SlewLessSessionTests.ARigThatCannotGotoIsCentredWithItsOwnRaMotorAndTheOperatorsDec |  | 12.2 | 9.0 |
| SessionImagingTests.GivenHighAltitudeTargetWhenImagingLoopThenHighUtilization(subSeconds: 20, leastUtilization: 0.75) |  | 4.7 | 11.8 |
| SessionImagingTests.GivenHighMinAltitudeWhenTargetDropsBelowThenImagingStopsEarly |  | 11.3 | 11.3 |
| NodeLiveViewTests.ALiveViewNobodyWatchesStopsOnceTheGraceIsSpent |  | 10.3 | 11.1 |
| NodePlanetaryTests.ACaptureNobodyWatchesStopsOnceTheGraceIsSpent |  | 10.2 | 10.9 |
