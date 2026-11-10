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

| | round 3 (42885521250) |
|---|---|
| build | 4m46s |
| build-linux (x64 unit, arm unit, x64 functional) | 6m17s, 5m53s, 5m00s |
| build-windows (unit, functional) | 7m28s, 7m04s |
| test-unit (debug) | 5m44s |
| test-unit (ubuntu-latest), each shard | 3m36s to 7m29s, ended 14m14s in |
| test-unit (ubuntu-24.04-arm), each shard | 2m51s to 5m26s, waited up to 1m00s, ended 12m44s in |
| test-functional, each shard | 4m42s to 5m15s, ended 12m00s in |
| test-windows (unit), each shard | 4m31s to 6m11s, waited up to 2m27s, ended 15m25s in |
| test-windows (functional), each shard | 5m34s to 7m29s, waited up to 3m17s, ended 17m53s in |
| gate, from the run's start | 18.0 min (red: the Windows cancel test below) |

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

## Run 42814136317 against the before run, classes at 2 s or more

### unit: 1037 classes, Linux before 2471 s summed, Linux 2286 s summed, Windows 3638 s (174 at 2 s or more shown)

| class | Linux before s | Linux s | Windows s | W/L | tests L/W | failed on W | Windows shard |
|---|---:|---:|---:|---:|---:|---:|---|
| PlanetarySharpeningTests | 152.2 | 96.4 | 308.8 | 3.2 | 39/39 |  | unit-1 |
| SyntheticBackgroundTests | 181.9 | 150.6 | 147.5 | 1.0 | 35/35 |  | unit-2 |
| SkyTextureTests | 28.8 | 28.1 | 150.2 | 5.3 | 8/8 |  | unit-5 |
| ClassicalStarRemoverTests | 76.7 | 125.3 | 141.0 | 1.1 | 17/17 |  | unit-2 |
| ViewerWaveletDeriveTests | 67.5 | 125.0 | 99.3 | 0.8 | 21/21 |  | unit-4 |
| StarInjectionTests | 98.3 | 124.0 | 108.3 | 0.9 | 66/66 |  | unit-2 |
| PlanetaryLimbFitTests | 98.3 | 55.9 | 120.6 | 2.2 | 13/13 |  | unit-1 |
| PlanetaryCompositionTests | 66.0 | 48.2 | 106.9 | 2.2 | 7/7 |  | unit-1 |
| DatasetDegradationExporterTests | 106.4 | 70.3 | 89.3 | 1.3 | 48/49 |  | unit-3 |
| DatasetBuildRunnerTests | 96.9 | 67.1 | 105.2 | 1.6 | 18/18 |  | unit-3 |
| PlateSolverTests | 81.5 | 55.9 | 104.3 | 1.9 | 21/31 |  | unit-5 |
| SaturnLimbFitTests | 57.2 | 50.9 | 96.7 | 1.9 | 7/7 |  | unit-1 |
| PlanetaryDegradeTests | 41.5 | 35.5 | 88.6 | 2.5 | 29/29 |  | unit-1 |
| ViewerPlanetaryLookTests | 38.4 | 78.0 | 46.5 | 0.6 | 6/6 |  | unit-4 |
| PlanetaryRenderTests | 67.5 | 45.7 | 65.9 | 1.4 | 12/13 |  | unit-1 |
| ViewerPlanetaryMasterTests | 36.0 | 62.0 | 66.8 | 1.1 | 8/8 |  | unit-4 |
| ColourAlignmentStackingTests | 21.9 | 59.2 | 31.3 | 0.5 | 9/9 |  | unit-5 |
| DatasetSessionRegistrarTests | 56.1 | 33.2 | 48.9 | 1.5 | 13/13 |  | unit-3 |
| N2nDenoiserTests | 13.5 | 14.0 | 55.8 | 4.0 | 18/18 |  | unit-5 |
| VelaMosaicFieldTests | 30.3 | 26.0 | 47.7 | 1.8 | 53/53 |  | unit-5 |
| ViewerAutoViewTests | 20.7 | 41.1 | 23.3 | 0.6 | 5/5 |  | unit-4 |
| PlanetaryColourTests | 36.6 | 29.2 | 40.4 | 1.4 | 18/18 |  | unit-1 |
| OrreryTests | 38.5 | 36.2 | 39.1 | 1.1 | 101/101 |  | unit-3 |
| ApertureFromSpectrumTests | 21.4 | 17.4 | 38.2 | 2.2 | 8/8 |  | unit-5 |
| OnnxTianWenDeconvolverTests | 8.8 | 13.6 | 37.5 | 2.8 | 19/19 |  | unit-5 |
| BiasPedestalFallbackTests | 1.8 | 5.4 | 36.1 | 6.7 | 1/1 |  | unit-5 |
| Planetary.PlanetaryChannelAlignmentTests | 35.3 | 14.8 | 20.2 | 1.4 | 11/11 |  | unit-1 |
| DatasetTileExporterTests | 10.0 | 34.7 | 24.1 | 0.7 | 11/11 |  | unit-3 |
| PlanetaryBestStackTests | 29.8 | 23.7 | 33.3 | 1.4 | 3/3 |  | unit-1 |
| Cr2ImportTests | 23.1 | 14.5 | 32.5 | 2.2 | 3/3 |  | unit-5 |
| PlanetarySpectralRatioTests | 20.7 | 17.3 | 32.2 | 1.9 | 3/3 |  | unit-1 |
| PlanetaryPictureTests | 31.2 | 25.8 | 31.5 | 1.2 | 6/6 |  | unit-1 |
| DALCameraVideoTests | 2.1 | 1.2 | 28.6 | 23.4 | 35/35 |  | unit-5 |
| NightCalendarTests | 21.6 | 18.0 | 26.7 | 1.5 | 23/23 |  | unit-5 |
| SaturationStackingTests | 11.9 | 6.7 | 25.9 | 3.9 | 7/7 |  | unit-5 |
| ExternalPlateSolverProcessTests | 20.5 | 20.5 | 25.9 | 1.3 | 4/4 |  | unit-5 |
| PlanetaryLookFitsTests | 18.7 | 12.3 | 24.4 | 2.0 | 2/2 |  | unit-1 |
| StarRemovalEvalTests | 21.1 | 11.2 | 23.9 | 2.1 | 10/10 |  | unit-2 |
| Tycho2PartialCatalogTests | 23.0 | 15.0 | 21.4 | 1.4 | 9/9 |  | unit-5 |
| StretchTests_DebayeredImageUnlinked | 16.2 | 22.3 | 22.9 | 1.0 | 4/4 |  | unit-4 |
| ViewerBestStackTests | 13.7 | 22.7 | 14.5 | 0.6 | 12/12 |  | unit-4 |
| PlanetaryFrameTimeOrderTests | 11.4 | 8.8 | 22.2 | 2.5 | 6/6 |  | unit-1 |
| GuideStarAcceptanceTests | 21.9 | 12.6 | 19.0 | 1.5 | 53/53 |  | unit-5 |
| StretchTests_NewPipeline | 21.3 | 16.4 | 18.1 | 1.1 | 15/15 |  | unit-4 |
| DrizzleOutlierRejectionTests | 1.8 | 4.5 | 20.4 | 4.6 | 5/5 |  | unit-5 |
| PlanetaryGhostTests | 20.1 | 17.0 | 19.9 | 1.2 | 11/11 |  | unit-1 |
| LightGradingTests | 7.3 | 3.6 | 19.9 | 5.6 | 42/42 |  | unit-5 |
| CometModelTests | 11.9 | 9.0 | 19.0 | 2.1 | 10/10 |  | unit-5 |
| Tycho2RegionBakeProbe | 6.2 | 4.5 | 18.7 | 4.1 | 2/3 |  | unit-5 |
| OrreryCometTests | 18.1 | 13.0 | 12.1 | 0.9 | 35/35 |  | unit-3 |
| SaturnDerotationTests | 12.2 | 9.1 | 17.7 | 1.9 | 2/2 |  | unit-1 |
| PlanetaryCaptureStreamingTests | 16.6 | 7.7 | 16.5 | 2.2 | 8/8 |  | unit-1 |
| Tycho2RegionSelectorTests | 7.7 | 5.2 | 16.1 | 3.1 | 7/7 |  | unit-5 |
| PlanetaryTrainingPairsTests | 6.9 | 5.8 | 15.6 | 2.7 | 2/2 |  | unit-1 |
| AlignmentPointMatchingTests | 10.0 | 7.9 | 15.3 | 1.9 | 3/3 |  | unit-5 |
| SimbadMergeSnapshotTests | 8.5 | 7.6 | 14.6 | 1.9 | 20/20 |  | unit-5 |
| CorruptReadoutTests | 3.7 | 2.5 | 14.3 | 5.7 | 5/5 |  | unit-5 |
| ViewerAutoFrameDemandTests | 8.2 | 6.6 | 14.2 | 2.2 | 8/8 |  | unit-4 |
| DatasetCrossNightExporterTests | 6.4 | 14.0 | 9.5 | 0.7 | 17/17 |  | unit-3 |
| FindStarsFromFitsFileTests | 4.6 | 6.6 | 13.9 | 2.1 | 12/12 |  | unit-5 |
| CanonPreviewRobustnessTests | 9.7 | 8.1 | 12.8 | 1.6 | 19/19 |  | unit-5 |
| PlanetaryPointKeepTests | 2.2 | 1.1 | 12.8 | 11.8 | 2/2 |  | unit-1 |
| PlanetaryIdentificationTests | 7.8 | 6.3 | 12.7 | 2.0 | 10/10 |  | unit-1 |
| FindBestFocusTests | 8.4 | 5.4 | 12.6 | 2.3 | 5/5 |  | unit-5 |
| ViewerBestViewTests | 5.6 | 4.3 | 12.6 | 2.9 | 2/2 |  | unit-4 |
| PoleMasterFrameSolveTests | 10.4 | 10.7 | 12.4 | 1.2 | 2/2 |  | unit-5 |
| InheritedWhiteBalanceStampTests | 12.3 | 8.8 | 5.7 | 0.6 | 2/2 |  | unit-5 |
| RealFrameSolveTests | 3.0 | 2.3 | 11.2 | 4.8 | 2/2 |  | unit-5 |
| PolarAlignmentRampIntegrationTests | 4.3 | 3.5 | 11.1 | 3.1 | 1/1 |  | unit-5 |
| GpuStretchPipelineTests | 11.0 | 9.3 | 0.0 | 0.0 | 26/0 |  |  |
| SaveTargetExtensionTests | 2.4 | 1.8 | 10.7 | 5.8 | 10/10 |  | unit-5 |
| SkyMapDarkNebulaScreenFilterTests | 4.1 | 3.1 | 10.7 | 3.4 | 6/6 |  | unit-5 |
| DebayerRegressionTests | 6.4 | 4.6 | 9.5 | 2.1 | 5/5 |  | unit-5 |
| DebayerRegionTests | 2.9 | 4.9 | 9.3 | 1.9 | 2/2 |  | unit-5 |
| StackingIntermediateDumpTests | 3.5 | 9.0 | 5.4 | 0.6 | 3/3 |  | unit-5 |
| StretchTests_MonoImage | 1.9 | 3.5 | 8.9 | 2.5 | 2/2 |  | unit-4 |
| PlanetaryCaptureTests | 0.9 | 0.3 | 8.8 | 29.3 | 8/8 |  | unit-1 |
| FakeFieldSolveProbe | 8.8 | 1.5 | 3.9 | 2.5 | 6/6 |  | unit-5 |
| PlanetaryFinishingTests | 2.3 | 1.8 | 8.7 | 4.7 | 7/7 |  | unit-1 |
| TexturedHoleFillTests | 5.0 | 5.2 | 8.4 | 1.6 | 1/1 |  | unit-5 |
| TiffRoundTripTests | 6.2 | 8.4 | 6.6 | 0.8 | 6/6 |  | unit-5 |
| StarRemoverRunnerTests | 7.9 | 5.0 | 8.3 | 1.7 | 17/17 |  | unit-2 |
| PlanetaryCaptureLiveViewTests | 1.9 | 0.2 | 8.0 | 37.2 | 10/10 |  | unit-1 |
| StackingPipelineRgbBayerSyntheticTest | 2.8 | 6.8 | 7.8 | 1.2 | 2/2 |  | unit-5 |
| SolveHintCacheTests | 2.7 | 1.9 | 7.7 | 4.1 | 4/4 |  | unit-5 |
| DeviceOwnershipTests | 7.6 | 2.0 | 1.3 | 0.7 | 20/20 |  | unit-5 |
| CometRepositoryTests | 2.2 | 1.9 | 7.1 | 3.7 | 17/17 |  | unit-5 |
| ViewerDisplayMemoryE2ETests | 0.4 | 0.3 | 6.9 | 27.3 | 16/16 |  | unit-4 |
| DatasetPsfNoiseReportTests | 6.8 | 3.4 | 3.1 | 0.9 | 17/17 |  | unit-3 |
| SkyMapPlanetDiscTests | 4.1 | 2.6 | 6.7 | 2.6 | 36/36 |  | unit-5 |
| Cr3ImportTests | 5.9 | 4.3 | 6.7 | 1.6 | 2/2 |  | unit-5 |
| ViewerSkyBackdropAgreementTests | 1.8 | 0.8 | 6.5 | 7.8 | 10/10 |  | unit-4 |
| PlanetaryInverseTests | 4.9 | 5.4 | 6.4 | 1.2 | 8/8 |  | unit-1 |
| NightSummaryTests | 3.7 | 2.9 | 6.3 | 2.2 | 10/10 |  | unit-5 |
| SolverSamplingFloorTests | 3.1 | 1.9 | 6.1 | 3.3 | 6/6 |  | unit-5 |
| SyntheticClusterExportTests | 2.2 | 1.5 | 5.6 | 3.6 | 4/4 |  | unit-2 |
| CometMaskTests | 5.5 | 3.7 | 3.3 | 0.9 | 10/10 |  | unit-5 |
| NeuralGuideTrainerTests | 3.6 | 1.4 | 5.5 | 4.0 | 4/4 |  | unit-5 |
| RegistrationComparisonTests | 3.1 | 2.2 | 5.5 | 2.5 | 10/10 |  | unit-5 |
| PlanetaryCeilingsTests | 1.8 | 1.9 | 5.4 | 2.9 | 8/8 |  | unit-1 |
| Tycho2LiteLookupParityTests | 2.0 | 1.9 | 5.4 | 2.8 | 1/1 |  | unit-5 |
| PlanetaryReframeTests | 2.8 | 1.4 | 5.4 | 3.9 | 3/3 |  | unit-1 |
| StackingPipelineRgbBayerDrizzleTest | 4.2 | 5.3 | 3.7 | 0.7 | 3/3 |  | unit-5 |
| OrreryCompassTests | 2.2 | 2.2 | 5.2 | 2.4 | 7/7 |  | unit-3 |
| SkyMapDeepLinkTests | 3.2 | 3.3 | 5.0 | 1.5 | 7/7 |  | unit-5 |
| LiveCloudJudgeTests | 0.6 | 0.5 | 4.9 | 9.7 | 7/7 |  | unit-5 |
| SessionAutoFocusFrameCaptureTests | 2.0 | 2.6 | 4.9 | 1.9 | 2/2 |  | unit-5 |
| OrreryGpuTests | 4.9 | 4.0 | 0.0 | 0.0 | 7/0 |  |  |
| ChannelOffsetsTests | 2.3 | 2.2 | 4.9 | 2.2 | 15/15 |  | unit-5 |
| TonightsBestTests | 4.8 | 3.8 | 3.5 | 0.9 | 15/15 |  | unit-5 |
| StretchTests_ColorImageLuma | 4.8 | 1.3 | 1.5 | 1.2 | 1/1 |  | unit-4 |
| StarDetectionSkyTriggerTests | 0.6 | 1.0 | 4.7 | 4.8 | 1/1 |  | unit-2 |
| DrizzleParallelBitIdentityTests | 3.1 | 3.3 | 4.7 | 1.4 | 13/13 |  | unit-5 |
| PlanetaryLimbKernelTests | 4.7 | 3.5 | 4.2 | 1.2 | 1/1 |  | unit-1 |
| Tycho2BulkInjectionTests | 4.7 | 3.8 | 4.4 | 1.2 | 5/5 |  | unit-5 |
| SaturnRingFaceTests | 1.1 | 0.8 | 4.5 | 5.9 | 5/5 |  | unit-1 |
| InfoPanelLiveStackViewerTests | 3.7 | 2.8 | 4.4 | 1.5 | 6/6 |  | unit-5 |
| SpccWhiteReferenceProbe | 1.9 | 1.9 | 4.4 | 2.3 | 1/1 |  | unit-5 |
| SkyMapPinnedOnlyGatherTests | 2.8 | 2.1 | 4.2 | 2.0 | 7/7 |  | unit-5 |
| DatasetStarlessReportTests | 1.1 | 1.9 | 4.2 | 2.2 | 1/1 |  | unit-3 |
| SharedAppDataFileTests | 1.9 | 1.4 | 4.2 | 3.0 | 4/5 |  | unit-5 |
| ExpectedSkyModelTests | 0.8 | 0.6 | 4.0 | 6.6 | 8/8 |  | unit-5 |
| LightGradeReferenceStoreTests | 0.1 | 0.1 | 3.9 | 29.0 | 6/6 |  | unit-5 |
| PlanetaryReferenceJudgeTests | 1.1 | 3.9 | 1.0 | 0.3 | 2/2 |  | unit-1 |
| CelestialObjectDBBenchmarkTests | 1.6 | 1.3 | 3.9 | 3.1 | 4/4 |  | unit-5 |
| ImageTransformTests | 3.9 | 1.8 | 0.6 | 0.3 | 5/5 |  | unit-5 |
| SyntheticStarDetectionTests | 2.0 | 1.7 | 3.8 | 2.2 | 26/26 |  | unit-2 |
| FindStarsFromCameraImageTests | 3.8 | 1.2 | 2.8 | 2.3 | 6/6 |  | unit-5 |
| StandardErrorPlaneTests | 3.3 | 3.7 | 2.5 | 0.7 | 22/22 |  | unit-5 |
| StackingCloudEmptiedGroupTest | 0.8 | 3.6 | 1.1 | 0.3 | 3/3 |  | unit-5 |
| ClassicalBackgroundExtractorTests | 1.1 | 2.6 | 3.5 | 1.3 | 19/19 |  | unit-2 |
| LayoutRectSweep | 3.0 | 2.1 | 3.5 | 1.7 | 1/1 |  | unit-5 |
| SkyMapAtlasNightCurveTests | 1.0 | 0.9 | 3.4 | 3.8 | 20/20 |  | unit-5 |
| MoffatCompositionTests | 1.8 | 1.3 | 3.3 | 2.6 | 14/14 |  | unit-5 |
| GuiWindowTests | 0.0 | 0.0 | 3.2 |  | 0/1 |  | unit-5 |
| PlanetaryMoonProbeTests | 2.1 | 2.1 | 3.2 | 1.5 | 8/8 |  | unit-1 |
| ReloadBackedReleaseTests | 0.6 | 0.4 | 3.1 | 7.7 | 12/12 |  | unit-5 |
| StarMagnitudeIndexTests | 3.1 | 1.8 | 2.6 | 1.4 | 14/14 |  | unit-2 |
| DatasetSplitAndParityTests | 0.8 | 1.6 | 3.1 | 2.0 | 7/7 |  | unit-3 |
| PhotometricTransparencyTests | 2.1 | 3.1 | 3.0 | 1.0 | 8/8 |  | unit-5 |
| PlanetaryTelescopePersistenceTests | 0.0 | 0.0 | 3.1 |  | 2/2 |  | unit-1 |
| ViewerRepaintResidueTests | 0.3 | 0.2 | 2.9 | 16.0 | 7/7 |  | unit-4 |
| SkyMapHoverAndPictureTests | 1.1 | 0.9 | 2.9 | 3.2 | 38/38 |  | unit-5 |
| TiffReadoutTileTests | 2.0 | 2.8 | 2.9 | 1.0 | 52/52 |  | unit-5 |
| Tycho2MemberManifestTests | 1.3 | 1.2 | 2.8 | 2.2 | 17/17 |  | unit-5 |
| StarDetectionRetryProbe | 1.0 | 1.5 | 2.8 | 1.9 | 7/7 |  | unit-2 |
| SpccBeforeEnhanceTests | 0.5 | 0.5 | 2.7 | 5.2 | 30/30 |  | unit-5 |
| SessionFlatsTests | 0.8 | 0.4 | 2.7 | 6.3 | 12/12 |  | unit-5 |
| IncrementalSolverTests | 2.5 | 1.5 | 1.7 | 1.1 | 13/13 |  | unit-5 |
| SessionPersistenceTests | 0.1 | 0.1 | 2.5 | 29.9 | 10/10 |  | unit-5 |
| CaptureStatisticsFileTests | 1.7 | 1.4 | 2.5 | 1.8 | 3/3 |  | unit-5 |
| DarkFrameRunCoolingTests | 0.7 | 0.8 | 2.5 | 3.3 | 7/7 |  | unit-5 |
| FaintFrameStretchStatsTests | 1.5 | 1.6 | 2.5 | 1.5 | 8/8 |  | unit-5 |
| PlanetaryCaptureInterludeTests | 1.0 | 2.5 | 0.4 | 0.2 | 11/12 |  | unit-1 |
| SharpenPipelineTests | 0.9 | 0.9 | 2.5 | 2.7 | 29/29 |  | unit-5 |
| HistogramSelectionParityTests | 0.6 | 1.3 | 2.4 | 1.9 | 5/5 |  | unit-5 |
| ContrastBoostTests | 1.0 | 2.0 | 2.4 | 1.2 | 15/15 |  | unit-5 |
| FakeTimePumpTests | 1.6 | 1.5 | 2.4 | 1.6 | 6/6 |  | unit-5 |
| CelestialObjectDBTests | 1.9 | 1.9 | 2.4 | 1.2 | 356/356 |  | unit-5 |
| RaDecCellEnumerationTests | 1.2 | 1.1 | 2.4 | 2.2 | 2/2 |  | unit-5 |
| SyntheticClusterSceneTests | 1.5 | 1.1 | 2.4 | 2.1 | 14/14 |  | unit-2 |
| PlannerTabLayoutTests | 0.7 | 0.6 | 2.3 | 4.2 | 34/34 |  | unit-5 |
| PlanetaryApStackTests | 2.3 | 1.3 | 1.1 | 0.8 | 12/12 |  | unit-1 |
| OrreryPanelTests | 2.3 | 2.2 | 2.3 | 1.1 | 12/12 |  | unit-3 |
| OnStepQuirkProbeTests | 0.2 | 0.8 | 2.3 | 2.7 | 1/1 |  | unit-5 |
| ViewerWaveletDialRangeTests | 2.1 | 1.8 | 2.3 | 1.2 | 5/5 |  | unit-4 |
| ViewerGradeVerdictE2ETests | 1.2 | 1.0 | 2.3 | 2.4 | 8/8 |  | unit-4 |
| CometObservabilityTests | 1.6 | 1.1 | 2.2 | 2.0 | 2/2 |  | unit-5 |
| BayerCentroidShiftProbe | 0.4 | 0.6 | 2.2 | 3.9 | 1/1 |  | unit-5 |
| StretchedNoiseTests | 1.2 | 2.2 | 2.1 | 1.0 | 10/10 |  | unit-4 |
| GpuVngDebayerParityTests | 2.1 | 1.7 | 0.0 | 0.0 | 20/0 |  |  |
| QuadScaleRecoveryTests | 1.2 | 1.0 | 2.0 | 2.1 | 5/5 |  | unit-5 |
| ViewerEnhancePopoverTests | 0.6 | 0.5 | 2.0 | 4.2 | 41/41 |  | unit-4 |
| PlanetaryWaveletGainsTests | 2.0 | 0.9 | 0.8 | 0.9 | 6/6 |  | unit-1 |

#### unit: the 30 slowest tests

| test | Linux before s | Linux s | Windows s |
|---|---:|---:|---:|
| SkyTextureTests.SparseSharpStructureFillsTheTailThatTheMadIgnores | 4.6 | 4.1 | 93.0 |
| SaturnLimbFitTests.TheLimbFitFindsAnEdgeOnSaturnWhereItWasPut(instant: "2025-11-25T19:00:00Z") | 25.1 | 18.6 | 47.3 |
| PlanetaryLimbFitTests.EachChannelIsFittedAgainstItsOwnLimbAndOneWithNoDiskKeepsTheSharedFit | 40.5 | 22.8 | 45.7 |
| BiasPedestalFallbackTests.WithNoMatchingDarkTheBiasStillRemovesThePedestal | 1.8 | 5.4 | 36.1 |
| SyntheticBackgroundTests.ByAreaACutHoldsKnotsPerExportCellOfItsArea | 36.0 | 32.7 | 20.3 |
| PlanetaryCompositionTests.TheJoinAveragesOnlyTheStacksThatReachAPixelThroughTheStepFilesAsInTheRecipe | 26.7 | 19.2 | 35.9 |
| PlanetaryBestStackTests.ADerotatedRunCountsItsLimbFitLoadsAndReportsEveryStageOnceInOrder | 29.6 | 23.5 | 33.2 |
| StarInjectionTests.AMeasureTheFitCannotReachIsShrunkTowardIndependent | 29.5 | 32.7 | 30.2 |
| PlanetaryCompositionTests.AStacksLimbEdgeIsReadUnturnedAtItsRegistrationAndNoGainOrSkyMovesIt | 10.0 | 6.0 | 30.3 |
| SaturnLimbFitTests.TheLimbFitFindsAnEdgeOnSaturnWhereItWasPut(instant: "2025-09-21T03:00:00Z") | 21.2 | 21.2 | 30.3 |
| PlanetarySharpeningTests.AtStrengthTwoNothingInsideTheOutlineIsLeftAtTheSkyInTheBatchOrTheLiveView | 16.8 | 11.4 | 30.0 |
| N2nDenoiserTests.TheChunkSeamsDoNotShowInTheStitchedOutput(overlap: 128) | 8.1 | 8.0 | 30.0 |
| PlanetarySharpeningTests.ADerivedSharpeningComesCloserToTheTruthThanTheStackAndDoesNotRing(fix: Floored) | 4.4 | 3.2 | 29.0 |
| ViewerWaveletDeriveTests.DeriveOnAColourCaptureMovesEveryLaterMastersColoursOntoGreen(dpi: 1.5) | 10.7 | 28.4 | 16.4 |
| PlanetaryColourTests.ASaturnsGlobeIsBalancedToSaturnsColourWhereItsRingsLeaveItClear | 28.4 | 22.4 | 21.2 |
| PlanetarySharpeningTests.TheDefaultTargetUndoesTheTelescopeHoweverFineThePixels | 13.9 | 8.3 | 27.9 |
| ViewerPlanetaryLookTests.TheLiveStackedViewDrawsTheLookOnTheMastersADeriveBalanced(dpi: 1) | 13.3 | 27.3 | 14.0 |
| StarInjectionTests.AFlatTruthReadsTheSameGainInEveryBandOnceTheSkyBiasIsTakenOff | 18.1 | 27.1 | 22.7 |
| Cr2ImportTests.Cr2_OpensViaImageTryReadImageFile_WithMatrixAndRgbRender | 19.5 | 12.4 | 27.0 |
| ViewerPlanetaryLookTests.TheLiveStackedViewDrawsTheLookOnTheMastersADeriveBalanced(dpi: 1.5) | 11.4 | 27.0 | 14.0 |
| ClassicalStarRemoverTests.AColourFieldWithUnequalChannelSkiesStillHasItsStarsSubtracted | 16.8 | 23.9 | 26.7 |
| SyntheticBackgroundTests.TailsFromASmoothPlateDrawASmootherTextureThanTheOneWidth | 26.6 | 13.5 | 15.3 |
| SyntheticBackgroundTests.ASteeredTextureRunsAlongThePlatesCoarseContoursWhereAnIsotropicOneDoesNot | 5.2 | 4.3 | 25.9 |
| PlanetaryDegradeTests.TheLimbsEdgeWidthIsTheBlursForAUniformDisk | 6.9 | 6.0 | 25.6 |
| ViewerWaveletDeriveTests.DeriveOnAColourCaptureMovesEveryLaterMastersColoursOntoGreen(dpi: 1) | 10.9 | 25.0 | 16.0 |
| PlanetarySharpeningTests.WhereTheNoiseIsWhiteTheHalvesNoiseSharpensAsTheWhiteLevelDoes | 8.2 | 5.6 | 24.4 |
| OnnxTianWenDeconvolverTests.TheShippedDeconvolverIsThePythonRuntime | 6.6 | 9.6 | 24.0 |
| PlanetarySpectralRatioTests.TheFitFindsTheR0ARatioWasMadeAt | 14.6 | 12.0 | 23.2 |
| SyntheticBackgroundTests.AWanderLoosensTheTieToTheCoarseContoursButKeepsTheTextureOneWay | 21.1 | 17.2 | 10.3 |
| PlateSolverTests.GivenSyntheticCatalogImageWhenCatalogPlateSolvingThenSolutionMatchesTarget(targetRA: 5.5899999999999999, targetDec: -5.3899999999999997, targetName: "M42", focalLengthMm: 200, accuracy: 0.050000000000000003, cameraDeviceId: 1) | 17.5 | 9.4 | 20.9 |

### functional: 109 classes, Linux before 0 s summed, Linux 1348 s summed, Windows 1315 s (65 at 2 s or more shown)

(run 42751729433 printed no per-test times for this suite, so its before column is empty)

| class | Linux before s | Linux s | Windows s | W/L | tests L/W | failed on W | Windows shard |
|---|---:|---:|---:|---:|---:|---:|---|
| FakeCameraMountCouplingTests | 0.0 | 155.1 | 139.9 | 0.9 | 15/15 |  | functional-3 |
| NodeRunsProcessTests | 0.0 | 140.4 | 136.7 | 1.0 | 2/2 |  | functional-1 |
| SessionObservationLoopTests | 0.0 | 94.7 | 96.0 | 1.0 | 18/18 |  | functional-2 |
| EquipmentFlowProcessTests | 0.0 | 74.4 | 73.3 | 1.0 | 1/1 |  | functional-3 |
| SessionImagingTests | 0.0 | 66.9 | 58.3 | 0.9 | 16/16 |  | functional-2 |
| PlanetarySequenceThroughTheGuiTests | 0.0 | 59.2 | 32.4 | 0.5 | 1/1 |  | functional-2 |
| GuideDarkLibraryNodeTests | 0.0 | 26.4 | 54.8 | 2.1 | 4/4 |  | functional-4 |
| QuitThroughTheNodeTests | 0.0 | 50.6 | 43.3 | 0.9 | 11/11 |  | functional-4 |
| MotionOperationTests | 0.0 | 43.0 | 47.9 | 1.1 | 23/23 |  | functional-3 |
| NodePlanetaryTests | 0.0 | 45.6 | 42.5 | 0.9 | 11/11 |  | functional-1 |
| NodePolarAlignmentTests | 0.0 | 40.1 | 37.9 | 0.9 | 7/7 |  | functional-1 |
| LocalNodeLauncherTests | 0.0 | 17.9 | 35.3 | 2.0 | 20/20 |  | functional-4 |
| SlewLessSessionTests | 0.0 | 30.2 | 26.6 | 0.9 | 3/3 |  | functional-4 |
| GuiContextGatingTests | 0.0 | 19.3 | 25.2 | 1.3 | 9/9 |  | functional-4 |
| EquipmentThroughTheNodeTests | 0.0 | 23.2 | 10.7 | 0.5 | 10/10 |  | functional-3 |
| NodeKeeperProcessTests | 0.0 | 20.8 | 14.1 | 0.7 | 7/7 |  | functional-1 |
| PlanetaryThroughTheNodeTests | 0.0 | 20.3 | 10.2 | 0.5 | 5/5 |  | functional-2 |
| MirrorParityTests | 0.0 | 13.8 | 18.7 | 1.4 | 1/1 |  | functional-3 |
| MeridianFlipVerificationSessionTests | 0.0 | 18.7 | 18.3 | 1.0 | 5/5 |  | functional-4 |
| NodeLiveViewTests | 0.0 | 17.8 | 17.7 | 1.0 | 6/6 |  | functional-1 |
| TuiEquipmentSlotClickTests | 0.0 | 17.5 | 13.0 | 0.7 | 8/8 |  | functional-4 |
| NodeGuidingTests | 0.0 | 8.5 | 17.0 | 2.0 | 4/4 |  | functional-1 |
| NodeJournalServiceTests | 0.0 | 14.0 | 16.9 | 1.2 | 19/19 |  | functional-1 |
| LiveSessionThroughTheNodeTests | 0.0 | 16.4 | 14.9 | 0.9 | 7/7 |  | functional-4 |
| GuideLoopTests | 0.0 | 16.3 | 12.7 | 0.8 | 19/19 |  | functional-4 |
| RunsThroughTheNodeTests | 0.0 | 16.1 | 10.8 | 0.7 | 6/6 |  | functional-4 |
| LocalNodeConnectionTests | 0.0 | 14.8 | 15.7 | 1.1 | 8/8 |  | functional-4 |
| SessionCloudTests | 0.0 | 15.5 | 13.8 | 0.9 | 4/4 |  | functional-2 |
| NodeJournalProcessTests | 0.0 | 15.4 | 9.7 | 0.6 | 3/3 |  | functional-1 |
| NodeAccessTests | 0.0 | 13.7 | 15.2 | 1.1 | 17/17 |  | functional-1 |
| NodePresenceTests | 0.0 | 15.1 | 15.2 | 1.0 | 2/2 |  | functional-1 |
| PlanetaryRecordingThroughTheNodeTests | 0.0 | 14.5 | 10.5 | 0.7 | 4/4 |  | functional-2 |
| CliVerbsThroughTheNodeTests | 0.0 | 10.7 | 13.3 | 1.2 | 14/14 |  | functional-4 |
| RigSiteTests | 0.0 | 13.1 | 13.0 | 1.0 | 6/6 |  | functional-2 |
| NodeFrameStreamTests | 0.0 | 12.6 | 11.4 | 0.9 | 6/6 |  | functional-1 |
| DeviceStateTests | 0.0 | 8.4 | 12.5 | 1.5 | 3/3 |  | functional-4 |
| LocalProfileAtConnectTests | 0.0 | 11.6 | 6.4 | 0.6 | 1/1 |  | functional-4 |
| SessionPhaseTests | 0.0 | 9.5 | 9.6 | 1.0 | 6/6 |  | functional-2 |
| SharedMemoryProcessTests | 0.0 | 9.4 | 6.6 | 0.7 | 1/1 |  | functional-4 |
| AlpacaServerRoundTripTests | 0.0 | 5.5 | 9.0 | 1.6 | 15/15 |  | functional-4 |
| RigCommandRoutingTests | 0.0 | 8.3 | 7.0 | 0.8 | 3/3 |  | functional-2 |
| HostingApiTests | 0.0 | 1.3 | 8.3 | 6.3 | 30/30 |  | functional-4 |
| GuiNodeHarnessAskAgainTests | 0.0 | 7.5 | 6.9 | 0.9 | 3/3 |  | functional-4 |
| NodeRunLifecycleTests | 0.0 | 6.4 | 7.2 | 1.1 | 13/13 |  | functional-1 |
| PlanetaryFilterChangeTests | 0.0 | 3.7 | 6.6 | 1.8 | 2/2 |  | functional-2 |
| NodeSolveTests | 0.0 | 5.7 | 5.0 | 0.9 | 4/4 |  | functional-1 |
| NodePreviewTests | 0.0 | 0.2 | 5.3 | 26.0 | 3/3 |  | functional-1 |
| SessionAutoFocusTests | 0.0 | 5.1 | 1.9 | 0.4 | 7/7 |  | functional-2 |
| NodeDarkLibraryTests | 0.0 | 4.9 | 4.0 | 0.8 | 5/5 |  | functional-1 |
| NodePreviewExposureTests | 0.0 | 4.8 | 4.7 | 1.0 | 5/5 |  | functional-1 |
| RigControlTests | 0.0 | 4.4 | 4.6 | 1.1 | 2/2 |  | functional-2 |
| TuiLiveSessionRigRowsTests | 0.0 | 4.5 | 3.0 | 0.7 | 2/2 |  | functional-4 |
| RemoteRigContactTests | 0.0 | 2.1 | 4.3 | 2.0 | 1/1 |  | functional-2 |
| ControlRequestOnThisComputerTests | 0.0 | 4.1 | 2.8 | 0.7 | 2/2 |  | functional-4 |
| NodeActiveProfileTests | 0.0 | 4.0 | 2.7 | 0.7 | 3/3 |  | functional-1 |
| PlanetaryFilterThroughTheNodeTests | 0.0 | 3.7 | 2.5 | 0.7 | 1/1 |  | functional-2 |
| NodeFrameTests | 0.0 | 3.5 | 2.5 | 0.7 | 8/8 |  | functional-1 |
| CameraOperationTests | 0.0 | 0.6 | 3.4 | 5.7 | 9/9 |  | functional-4 |
| PlanetaryReadoutsThroughTheNodeTests | 0.0 | 3.4 | 2.3 | 0.7 | 1/1 |  | functional-2 |
| ProfileFlowProcessTests | 0.0 | 2.6 | 3.3 | 1.3 | 1/1 |  | functional-4 |
| SessionFilterTests | 0.0 | 3.0 | 2.4 | 0.8 | 3/3 |  | functional-2 |
| SessionLifecycleTests | 0.0 | 2.4 | 2.7 | 1.1 | 31/31 |  | functional-2 |
| NodePlanetaryFindTests | 0.0 | 2.5 | 2.1 | 0.8 | 1/1 |  | functional-1 |
| NodeGuideLoopTests | 0.0 | 2.2 | 2.4 | 1.1 | 3/3 |  | functional-1 |
| RemoteRigLiveFrameTests | 0.0 | 1.6 | 2.1 | 1.3 | 1/1 |  | functional-2 |

#### functional: the 30 slowest tests

| test | Linux before s | Linux s | Windows s |
|---|---:|---:|---:|
| EquipmentFlowProcessTests.TheEquipmentTabsFlowsRunOverTheSocketAgainstASpawnedNode |  | 74.4 | 73.3 |
| NodeRunsProcessTests.AnInteractiveRunStopsAGraceAfterItsLastClientGoesAndAClientBackWithinItKeepsIt |  | 71.8 | 69.0 |
| NodeRunsProcessTests.ASessionAndADarkLibraryGoOnWithTheirLastClientGone |  | 68.6 | 67.6 |
| PlanetarySequenceThroughTheGuiTests.AStartedSequenceRunsOnTheNodeAndThePanelsStateFollowsItToItsEnd |  | 59.2 | 32.4 |
| GuideDarkLibraryNodeTests.GuideDarkLibraryTakesAFrameSetPerExposureInTheGuideCamerasFolderAndTheLoopAppliesThem |  | 24.2 | 51.3 |
| FakeCameraMountCouplingTests.GivenBuiltInGuiderWithMisalignedSkywatcherAndCameraPulseWhenCalibrateAndGuideThenStaysLockedWhileMountDrifts |  | 40.1 | 37.0 |
| NodePolarAlignmentTests.AWatchedRunGoesOnAClientBackWithinTheGraceKeepsItAndAnUnwatchedOneStops |  | 33.3 | 32.3 |
| FakeCameraMountCouplingTests.FreshNeuralModelTrainsStablyAndSavesNewSample |  | 23.3 | 22.4 |
| FakeCameraMountCouplingTests.GivenCloudDuringFirstCalibrationWhenItClearsThenRetrySucceedsAndGuides |  | 22.9 | 18.3 |
| FakeCameraMountCouplingTests.GivenGuidingWhenDitherThenSettleCompletesViaGetSettleProgressAlone |  | 22.6 | 21.7 |
| QuitThroughTheNodeTests.WithACameraCoolingItOffersToWarmItUpAndTheNodeDoes |  | 21.4 | 22.0 |
| FakeCameraMountCouplingTests.GivenCloudWhenGuidingRestartsWithReusedCalibrationThenAcquisitionFailsLoudly |  | 21.5 | 18.4 |
| FakeCameraMountCouplingTests.GivenCloudDuringGuidingThenStatusReportsLostLockAndRecoversOnClear |  | 21.1 | 19.2 |
| MirrorParityTests.ASessionSeenThroughAMirrorMatchesItSeenInProcess |  | 13.8 | 18.7 |
| SessionObservationLoopTests.GivenSkywatcherWithLimitsWhenTargetCrossesMeridianThenItFlipsAndTheLimitStaysClear |  | 17.2 | 15.7 |
| SessionObservationLoopTests.GivenSkywatcherWithAFlipConfiguredLaterThanTheLimitThenTheLimitStopsTheRunAsALimit |  | 16.4 | 15.0 |
| NodePresenceTests.APromptWaitsForAWindowThatKeepsDrawingAndIsLetGoOnceItFreezes |  | 15.0 | 15.0 |
| LocalNodeConnectionTests.AWindowWhoseNodeWentFindsItsNodeAgain |  | 14.3 | 14.5 |
| MotionOperationTests.AGotoAtTheSitesPoleGoesHomeAndAnyOtherDoesNot(dec: 70, expectedHomeCalls: 0, expectedStep: null) |  | 13.7 | 13.8 |
| MotionOperationTests.AGotoAwayFromThePoleNeverReadsTheSiteLatitude |  | 13.6 | 13.7 |
| SessionImagingTests.GivenFilterLadderAlternatingEveryFrameWhenFocusDriftsSlowlyThenRefocusFires |  | 13.6 | 12.3 |
| NodePlanetaryTests.ARecordingFinishesItsDurationThoughNobodyWatchesAndTheLiveViewStopsAGraceAfter |  | 13.3 | 13.3 |
| SessionObservationLoopTests.GivenNonGermanMountWhenTargetCrossesMeridianThenImagesWithoutFlipping(alignment: Polar) |  | 5.6 | 13.0 |
| SlewLessSessionTests.AFlipTurnsRaAHalfTurnAtOnceThenWaitsForTheOperatorToSwingDecOverThePole |  | 12.4 | 10.5 |
| SlewLessSessionTests.ARigThatCannotGotoIsCentredWithItsOwnRaMotorAndTheOperatorsDec |  | 12.3 | 10.4 |
| SessionObservationLoopTests.GivenAcrossMeridianTargetWhenHACrossesDeadbandThenFlipAndContinueImaging |  | 11.8 | 4.8 |
| LocalProfileAtConnectTests.TheProfileIsNeverKnownAsNoneWhileTheFramesPoll |  | 11.6 | 6.4 |
| NodeLiveViewTests.ALiveViewNobodyWatchesStopsOnceTheGraceIsSpent |  | 10.3 | 10.4 |
| NodePlanetaryTests.ACaptureNobodyWatchesStopsOnceTheGraceIsSpent |  | 10.3 | 10.3 |
| NodeAccessTests.APromptHeldForTheOwnerIsLetGoOnceOnlyAWatcherIsLeft |  | 10.1 | 9.0 |

