using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests
{
    public class DatasetQualityGateTests
    {
        private static SessionFrameAnalyzer.AnalyzedFrame Make(float hfd, float ellipticity, int starCount, int index = 0)
        {
            var meta = new ImageMeta(
                Instrument: "TestCam",
                ExposureStartTime: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(index),
                ExposureDuration: TimeSpan.FromSeconds(60),
                FrameType: FrameType.Light,
                Telescope: "T",
                PixelSizeX: 3.76f,
                PixelSizeY: 3.76f,
                FocalLength: 135,
                FocusPos: -1,
                Filter: Filter.None,
                BinX: 1,
                BinY: 1,
                CCDTemperature: -10f,
                SensorType: SensorType.RGGB,
                BayerOffsetX: 0,
                BayerOffsetY: 0,
                RowOrder: RowOrder.TopDown,
                Latitude: float.NaN,
                Longitude: float.NaN);
            var frame = new FrameInfo($"frame_{index:D3}.fits", 100, 100, 1, BitDepth.Int16, meta);
            return new SessionFrameAnalyzer.AnalyzedFrame(
                frame, new FrameMetrics(hfd, hfd * 1.2f, ellipticity, starCount), StarList.Empty);
        }

        [Fact]
        public void GivenDefocusedOutliers_WhenGating_ThenTheyAreRejectedForBroadHfd()
        {
            var frames = new List<SessionFrameAnalyzer.AnalyzedFrame>();
            for (var i = 0; i < 30; i++)
            {
                frames.Add(Make(2.5f + 0.02f * (i % 5), 0.10f, 900 + i, i));
            }
            frames.Add(Make(6.0f, 0.10f, 850, 30));
            frames.Add(Make(7.5f, 0.11f, 820, 31));

            var result = SessionFrameAnalyzer.ApplyGate(frames, sigma: 3f);

            result.Rejected.Length.ShouldBe(2);
            result.Rejected.ShouldAllBe(r => r.Reason.HasFlag(FrameRejectReason.HfdTooBroad));
            result.Kept.Length.ShouldBe(30);
        }

        [Fact]
        public void GivenCloudedFrames_WhenGating_ThenLowStarCountsAreRejected()
        {
            var frames = new List<SessionFrameAnalyzer.AnalyzedFrame>();
            for (var i = 0; i < 30; i++)
            {
                frames.Add(Make(2.5f, 0.10f, 1000 + 10 * (i % 3), i));
            }
            frames.Add(Make(2.4f, 0.09f, 80, 30));  // clouds: sharp but starved

            var result = SessionFrameAnalyzer.ApplyGate(frames, sigma: 3f);

            var rejected = result.Rejected.ShouldHaveSingleItem();
            rejected.Reason.ShouldBe(FrameRejectReason.StarCountTooLow);
        }

        [Fact]
        public void GivenAZeroStarFrame_WhenGateIsDisabled_ThenItIsStillRejected()
        {
            var frames = new List<SessionFrameAnalyzer.AnalyzedFrame>
            {
                Make(2.5f, 0.1f, 900, 0),
                Make(0f, 0f, 0, 1),
                Make(2.6f, 0.1f, 950, 2),
            };

            var result = SessionFrameAnalyzer.ApplyGate(frames, sigma: 0f);

            var rejected = result.Rejected.ShouldHaveSingleItem();
            rejected.Reason.ShouldBe(FrameRejectReason.StarCountTooLow);
            rejected.Frame.Metrics.StarCount.ShouldBe(0);
            result.Kept.Length.ShouldBe(2);
        }

        [Fact]
        public void GivenATinySession_WhenGating_ThenAllMeasurableFramesAreKept()
        {
            var frames = new List<SessionFrameAnalyzer.AnalyzedFrame>
            {
                Make(2.5f, 0.1f, 900, 0),
                Make(9.0f, 0.5f, 100, 1), // would be an outlier in a big session
                Make(2.6f, 0.1f, 950, 2),
            };

            var result = SessionFrameAnalyzer.ApplyGate(frames, sigma: 3f);

            result.Kept.Length.ShouldBe(3);
            result.Rejected.ShouldBeEmpty();
        }

        [Fact]
        public void GivenTooManyOutliers_WhenGating_ThenKeepFloorLimitsRejectionAndIsSurfaced()
        {
            var frames = new List<SessionFrameAnalyzer.AnalyzedFrame>();
            for (var i = 0; i < 10; i++)
            {
                frames.Add(Make(2.5f + 0.01f * i, 0.10f, 900, i));
            }
            for (var i = 0; i < 5; i++)
            {
                frames.Add(Make(6f + i, 0.4f, 300, 10 + i)); // 5 bad of 15 > 20% floor
            }

            var result = SessionFrameAnalyzer.ApplyGate(frames, sigma: 3f);

            result.KeepFloorTriggered.ShouldBeTrue();
            result.Rejected.Length.ShouldBe(3); // floor: at most 20% of 15
        }

        /// <summary>
        /// Exit criterion from docs/plans/ai-denoise-deconv.md P0: the hand-flagged
        /// "BAD LIGHT EXAMPLES" frames must be 100% rejected when gated against a healthy
        /// session of the same camera/exposure. Opt-in: requires a real archive via the
        /// TIANWEN_DATASET_ARCHIVE env var (skips cleanly otherwise, simulator-suite pattern).
        /// </summary>
        [Fact(Timeout = 1_800_000)]
        public async Task GivenBadLightExamplesMixedIntoAHealthySession_WhenGating_ThenAllBadFramesAreRejected()
        {
            var archive = Environment.GetEnvironmentVariable("TIANWEN_DATASET_ARCHIVE");
            Assert.SkipWhen(string.IsNullOrEmpty(archive), "TIANWEN_DATASET_ARCHIVE not set");
            var badDir = Path.Combine(archive!, "2026", "2026-02-20 BAD LIGHT EXAMPLES");
            var goodDir = Path.Combine(archive!, "2026", "2026-02-16");
            Assert.SkipWhen(!Directory.Exists(badDir) || !Directory.Exists(goodDir), "expected archive sessions not present");

            var ct = TestContext.Current.CancellationToken;
            var options = new DatasetBuildOptions { ArchiveRoots = [archive!], OutputDir = Path.GetTempPath() };

            async Task<List<FrameInfo>> LightsOf(string dir)
            {
                var frames = new List<(FrameInfo Frame, string Root)>();
                await foreach (var frame in new FitsFolderFrameSource(dir, true).EnumerateAsync(ct))
                {
                    frames.Add((frame, archive!));
                }
                var (sessions, _) = SessionDiscovery.GroupSessions(frames, options with { MinSubsPerSession = 1 });
                return [.. sessions.SelectMany(s => s.Lights)];
            }

            var bad = await LightsOf(badDir);
            var good = await LightsOf(goodDir);
            bad.Count.ShouldBeGreaterThanOrEqualTo(30);
            good.Count.ShouldBeGreaterThanOrEqualTo(150);

            // Every 2nd good light keeps the runtime sane while staying far above the
            // 20% keep-floor so a full 33-frame rejection is reachable.
            var population = good.Where((_, i) => i % 2 == 0).Concat(bad).ToList();
            var analyzed = new List<SessionFrameAnalyzer.AnalyzedFrame>(population.Count);
            foreach (var frame in population)
            {
                analyzed.Add(await SessionFrameAnalyzer.MeasureAsync(frame, cancellationToken: ct));
            }

            var result = SessionFrameAnalyzer.ApplyGate(analyzed, sigma: 3f);

            var badPaths = bad.Select(f => f.Path).ToHashSet();
            var badKept = result.Kept.Where(f => badPaths.Contains(f.Frame.Path)).ToList();
            var goodRejected = result.Rejected.Count(r => !badPaths.Contains(r.Frame.Frame.Path));

            badKept.ShouldBeEmpty($"all hand-flagged bad frames must be rejected; kept: {string.Join(", ", badKept.Select(f => Path.GetFileName(f.Frame.Path)))}");
            goodRejected.ShouldBeLessThan(good.Count / 4, "the gate must not slaughter healthy frames");
        }
    }
}
