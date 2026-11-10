# CI test times: Windows against Linux, per class

Issue #2054 added the unit and functional suites on `windows-latest`. This page keeps the per-class table the
Windows legs were judged by, from the PR's first run of them (CI/CD run 42804689190), and how to read a newer one.

    python tools/ci-test-timings.py <run id> --min-seconds 2

A time is the SUM of a class's test durations, not wall-clock: four tests run at once, so a class waiting on a
starved thread pool reads slow here even when its own work is not. The Linux column is `test-unit (ubuntu-latest)`
and `test-functional`; the Windows column the `test-windows` shard named beside it.

## What the table said, and what was done

- **The unit suite is not slower on Windows** (3531 s summed against 4577 s). Its classes over 2x Linux are small
  (at most 18 s) and are filed with their numbers in #2092, most of them a short real delay paying the Windows
  timer's 15.6 ms quantum.
- **The functional suite's slowness is one shard's first three minutes.** Shard 3 of 3 summed 1083 s against 529 s
  for the same classes on Linux, and its seven failures (all timeouts) fall between 11:19:06 and 11:21:50, with 1.5 s
  GC pauses, Kestrel's thread-pool starvation warning and nodes taking up to 35 s to start. The same shard held to
  four cores on a desktop passed whole in 6 minutes. The cause is not yet named; #2092 holds the evidence and the
  next suspects.
- **The build left every shard.** Each shard built its test project itself, 3 to 5 minutes of jobs that took 7 to
  14.5 minutes, so no shard could stay under 10. `build-windows` now builds both projects once and the shards unpack
  its bin folder, which also takes the build's resident servers off the machine the tests run on. The two heaviest
  shards were split (unit Dataset+Viewer+Orrery+Stretch summed 1001 s, functional "the rest" 1083 s): five unit
  shards and four functional ones, `.github/test-shards.json`.

## Run 42804689190, classes at 2 s or more on either OS

### unit: 1036 classes, Linux 4577 s summed, Windows 3531 s summed (173 at 2 s or more shown)

| class | Linux s | Windows s | W/L | tests L/W | failed on W | Windows shard |
|---|---:|---:|---:|---:|---:|---|
| SyntheticBackgroundTests | 352.1 | 229.1 | 0.7 | 35/35 |  | unit-2 |
| ViewerWaveletDeriveTests | 344.7 | 121.1 | 0.4 | 21/21 |  | unit-3 |
| PlanetarySharpeningTests | 201.0 | 276.6 | 1.4 | 39/39 |  | unit-1 |
| StarInjectionTests | 259.3 | 170.5 | 0.7 | 66/66 |  | unit-2 |
| ClassicalStarRemoverTests | 237.9 | 209.1 | 0.9 | 17/17 |  | unit-2 |
| DatasetBuildRunnerTests | 110.8 | 196.1 | 1.8 | 18/18 |  | unit-3 |
| ViewerPlanetaryLookTests | 177.8 | 53.8 | 0.3 | 6/6 |  | unit-3 |
| ViewerPlanetaryMasterTests | 160.1 | 107.4 | 0.7 | 8/8 |  | unit-3 |
| DatasetDegradationExporterTests | 136.1 | 133.6 | 1.0 | 48/49 |  | unit-3 |
| PlanetaryLimbFitTests | 123.3 | 105.5 | 0.9 | 13/13 |  | unit-1 |
| PlanetaryCompositionTests | 88.3 | 106.3 | 1.2 | 7/7 |  | unit-1 |
| PlanetaryRenderTests | 103.4 | 61.0 | 0.6 | 12/13 |  | unit-1 |
| SaturnLimbFitTests | 99.0 | 88.9 | 0.9 | 7/7 |  | unit-1 |
| PlateSolverTests | 91.3 | 71.9 | 0.8 | 21/31 |  | unit-4 |
| ColourAlignmentStackingTests | 82.6 | 48.7 | 0.6 | 9/9 |  | unit-4 |
| PlanetaryDegradeTests | 65.0 | 72.7 | 1.1 | 29/29 |  | unit-1 |
| ViewerAutoViewTests | 69.9 | 25.9 | 0.4 | 5/5 |  | unit-3 |
| DatasetSessionRegistrarTests | 56.6 | 69.5 | 1.2 | 13/13 |  | unit-3 |
| SkyTextureTests | 52.0 | 64.8 | 1.2 | 8/8 |  | unit-4 |
| ViewerBestStackTests | 62.9 | 15.7 | 0.2 | 12/12 |  | unit-3 |
| PlanetaryPictureTests | 55.2 | 30.9 | 0.6 | 6/6 |  | unit-1 |
| PlanetaryBestStackTests | 54.2 | 32.7 | 0.6 | 3/3 |  | unit-1 |
| PlanetaryColourTests | 53.6 | 32.1 | 0.6 | 18/18 |  | unit-1 |
| OrreryTests | 53.1 | 52.4 | 1.0 | 101/101 |  | unit-3 |
| DatasetTileExporterTests | 47.1 | 28.2 | 0.6 | 11/11 |  | unit-3 |
| VelaMosaicFieldTests | 46.9 | 24.1 | 0.5 | 53/53 |  | unit-4 |
| N2nDenoiserTests | 46.8 | 26.3 | 0.6 | 18/18 |  | unit-4 |
| PlanetarySpectralRatioTests | 40.1 | 26.9 | 0.7 | 3/3 |  | unit-1 |
| OnnxTianWenDeconvolverTests | 39.4 | 13.7 | 0.3 | 19/19 |  | unit-4 |
| NightCalendarTests | 35.9 | 13.9 | 0.4 | 23/23 |  | unit-4 |
| StretchTests_DebayeredImageUnlinked | 35.7 | 30.8 | 0.9 | 4/4 |  | unit-3 |
| StarRemovalEvalTests | 33.8 | 33.1 | 1.0 | 10/10 |  | unit-2 |
| PlanetaryGhostTests | 32.3 | 20.8 | 0.6 | 11/11 |  | unit-1 |
| Planetary.PlanetaryChannelAlignmentTests | 30.1 | 17.7 | 0.6 | 11/11 |  | unit-1 |
| DatasetCrossNightExporterTests | 29.9 | 13.2 | 0.4 | 17/17 |  | unit-3 |
| Cr2ImportTests | 29.0 | 17.3 | 0.6 | 3/3 |  | unit-4 |
| StretchTests_NewPipeline | 26.2 | 28.8 | 1.1 | 15/15 |  | unit-3 |
| PlanetaryLookFitsTests | 28.4 | 19.8 | 0.7 | 2/2 |  | unit-1 |
| OrreryCometTests | 26.2 | 19.3 | 0.7 | 35/35 |  | unit-3 |
| PoleMasterFrameSolveTests | 26.0 | 8.6 | 0.3 | 2/2 |  | unit-4 |
| GuideStarAcceptanceTests | 26.0 | 12.8 | 0.5 | 53/53 |  | unit-4 |
| ApertureFromSpectrumTests | 25.5 | 20.2 | 0.8 | 8/8 |  | unit-4 |
| CometModelTests | 23.8 | 13.5 | 0.6 | 10/10 |  | unit-4 |
| ExternalPlateSolverProcessTests | 20.2 | 22.6 | 1.1 | 4/4 |  | unit-4 |
| PlanetaryFrameTimeOrderTests | 19.2 | 15.5 | 0.8 | 6/6 |  | unit-1 |
| DALCameraVideoTests | 1.8 | 17.8 | 10.1 | 35/35 |  | unit-4 |
| AlignmentPointMatchingTests | 17.8 | 8.3 | 0.5 | 3/3 |  | unit-4 |
| CorruptReadoutTests | 5.6 | 16.3 | 2.9 | 5/5 |  | unit-4 |
| Tycho2PartialCatalogTests | 16.1 | 12.9 | 0.8 | 9/9 |  | unit-4 |
| PlanetaryPointKeepTests | 2.9 | 15.1 | 5.2 | 2/2 |  | unit-1 |
| ViewerAutoFrameDemandTests | 11.8 | 14.9 | 1.3 | 8/8 |  | unit-3 |
| SimbadMergeSnapshotTests | 12.2 | 14.7 | 1.2 | 20/20 |  | unit-4 |
| SaturnDerotationTests | 14.6 | 13.3 | 0.9 | 2/2 |  | unit-1 |
| GpuStretchPipelineTests | 14.1 | 0.0 | 0.0 | 26/0 |  |  |
| FindBestFocusTests | 10.1 | 13.7 | 1.4 | 5/5 |  | unit-4 |
| InheritedWhiteBalanceStampTests | 13.2 | 2.4 | 0.2 | 2/2 |  | unit-4 |
| StackingIntermediateDumpTests | 12.3 | 13.1 | 1.1 | 3/3 |  | unit-4 |
| ViewerBestViewTests | 8.4 | 12.8 | 1.5 | 2/2 |  | unit-3 |
| TiffRoundTripTests | 12.7 | 6.3 | 0.5 | 6/6 |  | unit-4 |
| PlanetaryTrainingPairsTests | 12.6 | 10.0 | 0.8 | 2/2 |  | unit-1 |
| StackingPipelineRgbBayerSyntheticTest | 12.0 | 9.4 | 0.8 | 2/2 |  | unit-4 |
| LightGradingTests | 8.8 | 12.0 | 1.4 | 42/42 |  | unit-4 |
| DebayerRegressionTests | 11.9 | 11.5 | 1.0 | 5/5 |  | unit-4 |
| Tycho2RegionSelectorTests | 11.4 | 6.1 | 0.5 | 7/7 |  | unit-4 |
| SaturationStackingTests | 7.9 | 11.2 | 1.4 | 7/7 |  | unit-4 |
| PlanetaryInverseTests | 11.1 | 5.9 | 0.5 | 8/8 |  | unit-1 |
| PlanetaryIdentificationTests | 10.9 | 6.8 | 0.6 | 10/10 |  | unit-1 |
| PlanetaryCaptureStreamingTests | 6.4 | 10.9 | 1.7 | 8/8 |  | unit-1 |
| Tycho2RegionBakeProbe | 6.8 | 10.7 | 1.6 | 2/3 |  | unit-4 |
| CanonPreviewRobustnessTests | 10.4 | 5.9 | 0.6 | 19/19 |  | unit-4 |
| BiasPedestalFallbackTests | 9.7 | 9.6 | 1.0 | 1/1 |  | unit-4 |
| ContrastBoostTests | 3.8 | 9.6 | 2.6 | 15/15 |  | unit-4 |
| DrizzleParallelBitIdentityTests | 8.9 | 2.4 | 0.3 | 13/13 |  | unit-4 |
| StarRemoverRunnerTests | 8.8 | 8.3 | 0.9 | 17/17 |  | unit-2 |
| OrreryCompassTests | 5.2 | 8.8 | 1.7 | 7/7 |  | unit-3 |
| SkyMapDarkNebulaScreenFilterTests | 8.8 | 3.9 | 0.4 | 6/6 |  | unit-4 |
| PlanetaryLimbKernelTests | 8.5 | 3.2 | 0.4 | 1/1 |  | unit-1 |
| FindStarsFromFitsFileTests | 6.3 | 8.4 | 1.3 | 12/12 |  | unit-4 |
| Cr3ImportTests | 8.3 | 2.5 | 0.3 | 2/2 |  | unit-4 |
| DrizzleOutlierRejectionTests | 3.9 | 8.1 | 2.1 | 5/5 |  | unit-4 |
| DatasetPsfNoiseReportTests | 8.0 | 6.6 | 0.8 | 17/17 |  | unit-3 |
| StackingPipelineRgbBayerDrizzleTest | 7.9 | 2.5 | 0.3 | 3/3 |  | unit-4 |
| PlanetaryCaptureTests | 0.7 | 7.8 | 11.0 | 8/8 |  | unit-1 |
| PlanetaryFinishingTests | 4.8 | 7.6 | 1.6 | 7/7 |  | unit-1 |
| NightSummaryTests | 7.5 | 3.3 | 0.4 | 10/10 |  | unit-4 |
| PolarAlignmentRampIntegrationTests | 7.4 | 6.2 | 0.8 | 1/1 |  | unit-4 |
| SkyMapPinnedOnlyGatherTests | 7.1 | 3.1 | 0.4 | 7/7 |  | unit-4 |
| SkyMapPlanetDiscTests | 7.1 | 2.5 | 0.4 | 36/36 |  | unit-4 |
| ViewerDisplayMemoryE2ETests | 0.5 | 7.0 | 14.3 | 16/16 |  | unit-3 |
| CometRepositoryTests | 2.6 | 6.9 | 2.6 | 17/17 |  | unit-4 |
| SkyMapDeepLinkTests | 6.9 | 3.0 | 0.4 | 7/7 |  | unit-4 |
| StandardErrorPlaneTests | 6.7 | 1.7 | 0.2 | 22/22 |  | unit-4 |
| RegistrationComparisonTests | 6.6 | 2.5 | 0.4 | 10/10 |  | unit-4 |
| SaveTargetExtensionTests | 2.9 | 6.6 | 2.3 | 10/10 |  | unit-4 |
| OrreryGpuTests | 6.3 | 0.0 | 0.0 | 7/0 |  |  |
| SharedAppDataFileTests | 1.8 | 6.3 | 3.5 | 4/5 |  | unit-4 |
| InfoPanelLiveStackViewerTests | 5.8 | 2.6 | 0.4 | 6/6 |  | unit-4 |
| DatasetStarlessReportTests | 4.0 | 5.8 | 1.4 | 1/1 |  | unit-3 |
| SolverSamplingFloorTests | 3.9 | 5.8 | 1.5 | 6/6 |  | unit-4 |
| TexturedHoleFillTests | 5.7 | 5.3 | 0.9 | 1/1 |  | unit-4 |
| ViewerRepaintResidueTests | 0.6 | 5.7 | 9.4 | 7/7 |  | unit-3 |
| FaintFrameStretchStatsTests | 5.7 | 2.2 | 0.4 | 8/8 |  | unit-4 |
| Tycho2LiteLookupParityTests | 5.6 | 2.3 | 0.4 | 1/1 |  | unit-4 |
| SolveHintCacheTests | 5.0 | 5.5 | 1.1 | 4/4 |  | unit-4 |
| TonightsBestTests | 5.4 | 3.2 | 0.6 | 15/15 |  | unit-4 |
| RealFrameSolveTests | 5.4 | 2.8 | 0.5 | 2/2 |  | unit-4 |
| Tycho2BulkInjectionTests | 5.4 | 3.8 | 0.7 | 5/5 |  | unit-4 |
| DebayerRegionTests | 5.4 | 4.2 | 0.8 | 2/2 |  | unit-4 |
| LayoutRectSweep | 5.4 | 2.7 | 0.5 | 1/1 |  | unit-4 |
| StretchTests_MonoImage | 3.4 | 5.3 | 1.5 | 2/2 |  | unit-3 |
| PhotometricTransparencyTests | 5.3 | 2.1 | 0.4 | 8/8 |  | unit-4 |
| ClassicalBackgroundExtractorTests | 3.7 | 5.3 | 1.4 | 19/19 |  | unit-2 |
| MoffatCompositionTests | 5.1 | 1.6 | 0.3 | 14/14 |  | unit-4 |
| ViewerSkyBackdropAgreementTests | 2.1 | 5.0 | 2.4 | 10/10 |  | unit-3 |
| PlanetaryCeilingsTests | 4.5 | 5.0 | 1.1 | 8/8 |  | unit-1 |
| NeuralGuideTrainerTests | 4.9 | 1.9 | 0.4 | 4/4 |  | unit-4 |
| StarDetectionRetryProbe | 4.8 | 4.2 | 0.9 | 7/7 |  | unit-2 |
| FakeTimePumpTests | 1.7 | 4.8 | 2.8 | 6/6 |  | unit-4 |
| CometMaskTests | 4.4 | 1.2 | 0.3 | 10/10 |  | unit-4 |
| StackingCloudEmptiedGroupTest | 4.4 | 0.7 | 0.2 | 3/3 |  | unit-4 |
| SyntheticClusterExportTests | 4.3 | 4.1 | 0.9 | 4/4 |  | unit-2 |
| StarDetectionSkyTriggerTests | 1.6 | 4.3 | 2.7 | 1/1 |  | unit-2 |
| StarMagnitudeIndexTests | 4.2 | 3.0 | 0.7 | 14/14 |  | unit-2 |
| OrreryPanelTests | 3.0 | 4.2 | 1.4 | 12/12 |  | unit-3 |
| SyntheticStarDetectionTests | 3.0 | 4.1 | 1.4 | 26/26 |  | unit-2 |
| StretchedNoiseTests | 4.1 | 1.6 | 0.4 | 10/10 |  | unit-3 |
| StretchTests_ColorImageLuma | 4.1 | 1.5 | 0.4 | 1/1 |  | unit-3 |
| DatasetSplitAndParityTests | 3.5 | 3.9 | 1.1 | 7/7 |  | unit-3 |
| TiffReadoutTileTests | 3.9 | 1.6 | 0.4 | 52/52 |  | unit-4 |
| SyntheticClusterSceneTests | 3.2 | 3.9 | 1.2 | 14/14 |  | unit-2 |
| PlanetaryMoonProbeTests | 3.8 | 2.0 | 0.5 | 8/8 |  | unit-1 |
| PlanetaryReframeTests | 2.3 | 3.8 | 1.6 | 3/3 |  | unit-1 |
| PlanetaryQualityCurveTests | 0.8 | 3.8 | 4.5 | 11/11 |  | unit-1 |
| PlanetarySequenceTests | 1.5 | 3.7 | 2.5 | 9/9 |  | unit-1 |
| StackingPipelineStarlessTest | 3.6 | 1.7 | 0.5 | 3/3 |  | unit-4 |
| SaturnRingFaceTests | 1.0 | 3.4 | 3.3 | 5/5 |  | unit-1 |
| ViewerWaveletDialRangeTests | 3.3 | 2.9 | 0.9 | 5/5 |  | unit-3 |
| SpccWhiteReferenceProbe | 3.3 | 1.7 | 0.5 | 1/1 |  | unit-4 |
| GuiWindowTests | 0.0 | 3.2 |  | 0/1 |  | unit-4 |
| CaptureStatisticsFileTests | 3.2 | 1.2 | 0.4 | 3/3 |  | unit-4 |
| RichardsonLucyTests | 3.2 | 1.2 | 0.4 | 9/9 |  | unit-4 |
| SpccBeforeEnhanceTests | 1.0 | 3.2 | 3.1 | 30/30 |  | unit-4 |
| PlanetaryBayerTwinTests | 3.1 | 1.7 | 0.5 | 7/7 |  | unit-1 |
| PlanetaryApStackTests | 3.1 | 0.8 | 0.3 | 12/12 |  | unit-1 |
| SkyMapHoverAndPictureTests | 3.1 | 2.5 | 0.8 | 38/38 |  | unit-4 |
| SessionAutoFocusFrameCaptureTests | 3.1 | 2.1 | 0.7 | 2/2 |  | unit-4 |
| SyntheticCatalogProjectionTests | 2.5 | 3.1 | 1.2 | 6/6 |  | unit-2 |
| PlanetaryPowerSpectrumTests | 3.1 | 1.0 | 0.3 | 2/2 |  | unit-1 |
| EdgeSpreadProfileTests | 3.0 | 1.2 | 0.4 | 5/5 |  | unit-4 |
| CelestialObjectDBTests | 3.0 | 2.3 | 0.8 | 356/356 |  | unit-4 |
| ExpectedSkyModelTests | 1.9 | 3.0 | 1.6 | 8/8 |  | unit-4 |
| FakeFieldSolveProbe | 2.9 | 1.0 | 0.4 | 6/6 |  | unit-4 |
| ImageTransformTests | 2.9 | 0.5 | 0.2 | 5/5 |  | unit-4 |
| CelestialObjectDBBenchmarkTests | 2.9 | 2.3 | 0.8 | 4/4 |  | unit-4 |
| DatasetGradientFrameExporterTests | 2.8 | 2.8 | 1.0 | 7/7 |  | unit-3 |
| SkyMapAtlasNightCurveTests | 2.8 | 1.2 | 0.4 | 20/20 |  | unit-4 |
| QuadScaleRecoveryTests | 2.7 | 1.6 | 0.6 | 5/5 |  | unit-4 |
| CometObservabilityTests | 2.7 | 1.7 | 0.6 | 2/2 |  | unit-4 |
| GpuVngDebayerParityTests | 2.7 | 0.0 | 0.0 | 20/0 |  |  |
| PlanetaryBlurProbesTests | 2.5 | 0.4 | 0.2 | 3/3 |  | unit-1 |
| RaDecCellEnumerationTests | 2.5 | 1.7 | 0.7 | 2/2 |  | unit-4 |
| PlanetaryCaptureLiveViewTests | 0.2 | 2.5 | 15.0 | 10/10 |  | unit-1 |
| ChannelOffsetsTests | 1.8 | 2.4 | 1.3 | 15/15 |  | unit-4 |
| HistogramSelectionParityTests | 2.4 | 1.8 | 0.7 | 5/5 |  | unit-4 |
| PlateSolveParityRaceTests | 2.2 | 2.4 | 1.1 | 4/4 |  | unit-4 |
| StarDetectionWarmPixelTests | 2.0 | 2.3 | 1.2 | 4/4 |  | unit-2 |
| ReadoutTileTests | 0.6 | 2.3 | 3.7 | 31/31 |  | unit-4 |
| ViewerGradeVerdictE2ETests | 1.7 | 2.3 | 1.3 | 8/8 |  | unit-3 |
| ViewerEnhancePopoverTests | 2.3 | 0.9 | 0.4 | 41/41 |  | unit-3 |
| CoverageEdgeWalkTests | 2.1 | 2.2 | 1.1 | 11/11 |  | unit-4 |
| ViewerObjectSelectionTests | 1.8 | 2.2 | 1.2 | 32/32 |  | unit-3 |
| NoiseEstimatorFineBandProbe | 2.1 | 1.9 | 0.9 | 2/2 |  | unit-4 |
| RollingWindowStackerTests | 2.0 | 0.4 | 0.2 | 15/15 |  | unit-4 |

### functional: 109 classes, Linux 1132 s summed, Windows 1696 s summed (73 at 2 s or more shown)

| class | Linux s | Windows s | W/L | tests L/W | failed on W | Windows shard |
|---|---:|---:|---:|---:|---:|---|
| FakeCameraMountCouplingTests | 143.5 | 175.6 | 1.2 | 15/15 |  | functional-3 |
| MotionOperationTests | 42.8 | 163.8 | 3.8 | 23/23 | 1 | functional-3 |
| NodeRunsProcessTests | 134.8 | 134.6 | 1.0 | 2/2 |  | functional-1 |
| GuiContextGatingTests | 12.5 | 98.4 | 7.9 | 9/9 | 3 | functional-3 |
| SessionObservationLoopTests | 79.5 | 83.3 | 1.0 | 18/18 |  | functional-2 |
| MirrorParityTests | 9.8 | 75.7 | 7.7 | 1/1 |  | functional-3 |
| EquipmentFlowProcessTests | 73.8 | 75.1 | 1.0 | 1/1 |  | functional-3 |
| FrameStreamCloseBudgetTests | 0.3 | 61.9 | 215.7 | 2/2 | 1 | functional-3 |
| SessionImagingTests | 53.2 | 50.6 | 1.0 | 16/16 |  | functional-2 |
| QuitThroughTheNodeTests | 40.4 | 47.0 | 1.2 | 11/11 |  | functional-3 |
| NodePlanetaryTests | 41.4 | 42.9 | 1.0 | 11/11 |  | functional-1 |
| PlanetarySequenceThroughTheGuiTests | 40.8 | 22.1 | 0.5 | 1/1 |  | functional-2 |
| GuideDarkLibraryTests | 0.8 | 38.1 | 47.5 | 3/3 |  | functional-3 |
| NodePolarAlignmentTests | 37.8 | 37.6 | 1.0 | 7/7 |  | functional-1 |
| GuideDarkLibraryNodeTests | 34.7 | 16.7 | 0.5 | 4/4 |  | functional-3 |
| OnStepWifiTransportTests | 0.4 | 33.4 | 91.9 | 4/4 | 1 | functional-3 |
| ExeBesideTests | 0.3 | 30.2 | 110.7 | 1/2 | 1 | functional-3 |
| LocalNodeLauncherTests | 14.2 | 29.1 | 2.0 | 20/20 |  | functional-3 |
| SlewLessSessionTests | 18.4 | 26.4 | 1.4 | 3/3 |  | functional-3 |
| NodeLiveViewTests | 16.8 | 17.3 | 1.0 | 6/6 |  | functional-1 |
| NodeJournalServiceTests | 14.3 | 16.9 | 1.2 | 19/19 |  | functional-1 |
| CliVerbsThroughTheNodeTests | 10.2 | 16.7 | 1.6 | 14/14 |  | functional-3 |
| NodeAccessTests | 14.2 | 16.6 | 1.2 | 17/17 |  | functional-1 |
| MeridianFlipVerificationSessionTests | 12.5 | 16.6 | 1.3 | 5/5 |  | functional-3 |
| NodeKeeperProcessTests | 15.9 | 15.8 | 1.0 | 7/7 |  | functional-1 |
| NodeGuidingTests | 5.0 | 15.9 | 3.2 | 4/4 |  | functional-1 |
| LocalNodeConnectionTests | 14.7 | 15.5 | 1.1 | 8/8 |  | functional-3 |
| NodePresenceTests | 15.1 | 15.3 | 1.0 | 2/2 |  | functional-1 |
| EquipmentThroughTheNodeTests | 13.5 | 14.8 | 1.1 | 10/10 |  | functional-3 |
| LiveSessionThroughTheNodeTests | 13.1 | 13.8 | 1.1 | 7/7 |  | functional-3 |
| NodeJournalProcessTests | 10.8 | 11.7 | 1.1 | 3/3 |  | functional-1 |
| SessionCloudTests | 10.8 | 11.7 | 1.1 | 4/4 |  | functional-2 |
| PlanetaryThroughTheNodeTests | 11.5 | 5.9 | 0.5 | 5/5 |  | functional-2 |
| NodeFrameStreamTests | 11.5 | 10.8 | 0.9 | 6/6 |  | functional-1 |
| TuiEquipmentSlotClickTests | 10.6 | 11.4 | 1.1 | 8/8 |  | functional-3 |
| FakeSkywatcherMountDriverTests | 0.4 | 11.3 | 28.6 | 62/62 |  | functional-3 |
| RunsThroughTheNodeTests | 11.3 | 10.6 | 0.9 | 6/6 |  | functional-3 |
| AlpacaServerRoundTripTests | 3.9 | 10.3 | 2.6 | 15/15 |  | functional-3 |
| HostingApiTests | 0.9 | 10.0 | 11.1 | 30/30 |  | functional-3 |
| PlanetaryRecordingThroughTheNodeTests | 9.6 | 8.3 | 0.9 | 4/4 |  | functional-2 |
| DeviceStateTests | 8.4 | 9.2 | 1.1 | 3/3 |  | functional-3 |
| GuiderCalibrationOnFakeGuideCameraTests | 1.2 | 9.1 | 7.7 | 1/1 |  | functional-3 |
| SessionPhaseTests | 8.3 | 9.0 | 1.1 | 6/6 |  | functional-2 |
| CameraCoolingRampTests | 0.0 | 8.6 |  | 86/86 |  | functional-3 |
| GuideLoopTests | 7.9 | 5.7 | 0.7 | 19/19 |  | functional-3 |
| RigSiteTests | 7.7 | 7.5 | 1.0 | 6/6 |  | functional-2 |
| LocalProfileAtConnectTests | 6.3 | 7.6 | 1.2 | 1/1 |  | functional-3 |
| NodeRunLifecycleTests | 5.4 | 7.2 | 1.3 | 13/13 |  | functional-1 |
| GuiNodeHarnessAskAgainTests | 4.2 | 6.9 | 1.6 | 3/3 |  | functional-3 |
| RigCommandRoutingTests | 6.8 | 6.3 | 0.9 | 3/3 |  | functional-2 |
| SharedMemoryProcessTests | 6.1 | 6.0 | 1.0 | 1/1 |  | functional-3 |
| MeadeLX200BasedMountTests | 0.0 | 5.3 |  | 19/19 |  | functional-3 |
| PlanetaryFilterChangeTests | 3.9 | 5.2 | 1.3 | 2/2 |  | functional-2 |
| NodePreviewExposureTests | 4.7 | 5.0 | 1.1 | 5/5 |  | functional-1 |
| NodeSolveTests | 4.9 | 4.8 | 1.0 | 4/4 |  | functional-1 |
| RigControlTests | 4.1 | 4.5 | 1.1 | 2/2 |  | functional-2 |
| GuiderCalibrationTests | 0.3 | 4.3 | 15.8 | 20/20 |  | functional-3 |
| RemoteRigContactTests | 2.0 | 4.2 | 2.1 | 1/1 |  | functional-2 |
| NodeDarkLibraryTests | 3.7 | 3.9 | 1.0 | 5/5 |  | functional-1 |
| NodeActiveProfileTests | 2.2 | 3.5 | 1.6 | 3/3 |  | functional-1 |
| NodePreviewTests | 0.1 | 3.4 | 24.0 | 3/3 |  | functional-1 |
| SessionAutoFocusTests | 3.2 | 1.5 | 0.5 | 7/7 |  | functional-2 |
| TuiLiveSessionRigRowsTests | 3.0 | 2.9 | 1.0 | 2/2 |  | functional-3 |
| CameraOperationTests | 0.5 | 2.8 | 5.3 | 9/9 |  | functional-3 |
| ProfileFlowProcessTests | 2.2 | 2.8 | 1.3 | 1/1 |  | functional-3 |
| ControlRequestOnThisComputerTests | 2.7 | 2.7 | 1.0 | 2/2 |  | functional-3 |
| NodeGuideLoopTests | 2.2 | 2.5 | 1.1 | 3/3 |  | functional-1 |
| PlanetaryFilterThroughTheNodeTests | 2.4 | 2.0 | 0.8 | 1/1 |  | functional-2 |
| SessionLifecycleTests | 2.2 | 2.3 | 1.1 | 31/31 |  | functional-2 |
| NodePlanetaryFindTests | 2.1 | 2.0 | 0.9 | 1/1 |  | functional-1 |
| NodeFrameTests | 2.0 | 2.1 | 1.0 | 8/8 |  | functional-1 |
| RemoteRigLiveFrameTests | 2.1 | 1.5 | 0.7 | 1/1 |  | functional-2 |
| IdleRigDevicesTests | 1.2 | 2.0 | 1.6 | 3/3 |  | functional-3 |

