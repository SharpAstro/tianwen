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
    public class DatasetQualityGateTests(Xunit.ITestOutputHelper output)
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

            // Explicit 0.2 floor (the dataset default is 0.5) to exercise the floor mechanic:
            // 5 flagged of 15 > 20% -> capped to worst 3 by severity.
            var result = SessionFrameAnalyzer.ApplyGate(frames, sigma: 3f, maxRejectFraction: 0.2f);

            result.KeepFloorTriggered.ShouldBeTrue();
            result.Rejected.Length.ShouldBe(3); // floor: at most 20% of 15
        }

        /// <summary>
        /// Diagnostic (not an assertion): measures the hand-flagged BAD LIGHT set and a healthy
        /// session of the same rig, dumps their PSF-metric distributions to
        /// &lt;archive&gt;/dataset-metrics-dump.json + a percentile summary, and caches per-frame
        /// metrics so the exit-criterion test can run instantly afterwards. Opt-in via
        /// TIANWEN_DATASET_ARCHIVE (skips clean otherwise). This is how the gate thresholds are
        /// calibrated against real numbers rather than guessed.
        /// </summary>
        [Fact(Timeout = 3_600_000)]
        public async Task DumpBadVsGoodMetricDistributions()
        {
            var archive = Environment.GetEnvironmentVariable("TIANWEN_DATASET_ARCHIVE");
            Assert.SkipWhen(string.IsNullOrEmpty(archive), "TIANWEN_DATASET_ARCHIVE not set");
            var badDir = Path.Combine(archive!, "2026", "2026-02-20 BAD LIGHT EXAMPLES");
            var goodDir = Path.Combine(archive!, "2026", "2026-02-16");
            Assert.SkipWhen(!Directory.Exists(badDir) || !Directory.Exists(goodDir), "expected archive sessions not present");

            var ct = TestContext.Current.CancellationToken;
            var cachePath = Path.Combine(archive!, "dataset-metrics-dump.json");
            var cache = LoadMetricCache(cachePath);

            // Cap the good sample for speed (measuring 3008^2 frames is the bottleneck); the bad
            // set (33) is measured fully. Cache is saved after EACH frame so a kill loses nothing.
            var goodCap = int.TryParse(Environment.GetEnvironmentVariable("TIANWEN_DATASET_GOODCAP"), out var g) ? g : 40;

            async Task<List<(string Path, FrameMetrics M)>> MeasureDir(string dir, int cap)
            {
                var options = new DatasetBuildOptions { ArchiveRoots = [archive!], OutputDir = Path.GetTempPath() };
                var frames = new List<(FrameInfo Frame, string Root)>();
                await foreach (var frame in new FitsFolderFrameSource(dir, true).EnumerateAsync(ct))
                {
                    frames.Add((frame, archive!));
                }
                var (sessions, _) = SessionDiscovery.GroupSessions(frames, options with { MinSubsPerSession = 1 });
                var lights = sessions.SelectMany(s => s.Lights).Where((_, i) => cap <= 0 || i % Math.Max(1, sessions.Sum(s => s.Lights.Length) / cap) == 0).Take(cap > 0 ? cap : int.MaxValue).ToList();
                var measured = new List<(string, FrameMetrics)>(lights.Count);
                foreach (var light in lights)
                {
                    if (!cache.TryGetValue(light.Path, out var m))
                    {
                        m = (await SessionFrameAnalyzer.MeasureAsync(light, cancellationToken: ct)).Metrics;
                        cache[light.Path] = m;
                        SaveMetricCache(cachePath, cache);
                    }
                    measured.Add((light.Path, m));
                }
                return measured;
            }

            var bad = await MeasureDir(badDir, cap: 0);
            var good = await MeasureDir(goodDir, goodCap);

            string Summarize(string label, List<(string Path, FrameMetrics M)> set)
            {
                float[] Pcts(Func<FrameMetrics, float> sel)
                {
                    var v = set.Select(x => sel(x.M)).OrderBy(x => x).ToArray();
                    float P(double q) => v[Math.Clamp((int)(q * (v.Length - 1)), 0, v.Length - 1)];
                    return [P(0.10), P(0.50), P(0.90)];
                }
                var hfd = Pcts(m => m.MedianHfd);
                var ecc = Pcts(m => m.MedianEllipticity);
                var stars = Pcts(m => m.StarCount);
                return $"{label} (n={set.Count}): HFD p10/50/90={hfd[0]:F2}/{hfd[1]:F2}/{hfd[2]:F2}  " +
                    $"ecc={ecc[0]:F3}/{ecc[1]:F3}/{ecc[2]:F3}  stars={stars[0]:F0}/{stars[1]:F0}/{stars[2]:F0}";
            }
            // Write the summary to a file too — ITestOutputHelper is lost if the run is killed.
            var lines = new[]
            {
                Summarize("GOOD 2026-02-16", good),
                Summarize("BAD  2026-02-20", bad),
                "BAD ecc sorted: " + string.Join(" ", bad.Select(x => x.M.MedianEllipticity).OrderBy(x => x).Select(x => x.ToString("F3"))),
            };
            File.WriteAllLines(Path.Combine(archive!, "dataset-metrics-summary.txt"), lines);
            foreach (var line in lines)
            {
                output.WriteLine(line);
            }
        }

        /// <summary>
        /// Exit criterion (docs/plans/ai-denoise-deconv.md P0), run against the metrics cached by
        /// DumpBadVsGoodMetricDistributions (instant — no re-measure). Mixes the 33 hand-flagged
        /// BAD frames into the full healthy session as a realistic ~8% minority and asserts the
        /// gate: (a) rejects EVERY bad frame whose star count is clearly below the good floor
        /// (transparency loss — the reason the gate measures), (b) rejects the large majority of
        /// bad frames, (c) keeps the overwhelming majority of good frames. It deliberately does
        /// NOT require 100% bad rejection — a handful of bad frames are metrically identical to
        /// good (bad for reasons no PSF metric sees). Skips if the metrics cache isn't present.
        /// </summary>
        [Fact]
        public void GivenBadFramesAsARealisticMinority_WhenGating_ThenTransparencyBadAreRejectedAndGoodKept()
        {
            var archive = Environment.GetEnvironmentVariable("TIANWEN_DATASET_ARCHIVE");
            Assert.SkipWhen(string.IsNullOrEmpty(archive), "TIANWEN_DATASET_ARCHIVE not set");
            var cachePath = Path.Combine(archive!, "dataset-metrics-dump.json");
            Assert.SkipWhen(!File.Exists(cachePath), "run DumpBadVsGoodMetricDistributions first to populate the metrics cache");

            var cache = LoadMetricCache(cachePath);
            var bad = cache.Where(kv => kv.Key.Contains("BAD LIGHT", StringComparison.OrdinalIgnoreCase)).ToList();
            var good = cache.Where(kv => !kv.Key.Contains("BAD LIGHT", StringComparison.OrdinalIgnoreCase)).ToList();
            good.Count.ShouldBeGreaterThanOrEqualTo(150, "measure the full good session first");
            bad.Count.ShouldBeGreaterThanOrEqualTo(30);

            static SessionFrameAnalyzer.AnalyzedFrame Frame(KeyValuePair<string, FrameMetrics> kv)
            {
                var meta = new ImageMeta("C", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(60), FrameType.Light,
                    "T", 3.76f, 3.76f, 135, -1, Filter.None, 1, 1, -10f, SensorType.RGGB, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
                return new SessionFrameAnalyzer.AnalyzedFrame(
                    new FrameInfo(kv.Key, 3008, 3008, 1, BitDepth.Int16, meta), kv.Value, StarList.Empty);
            }

            var population = good.Concat(bad).Select(Frame).ToList();
            (bad.Count / (double)population.Count).ShouldBeLessThan(0.15, "bad must be a realistic minority");

            var result = SessionFrameAnalyzer.ApplyGate(population, sigma: 3f, maxRejectFraction: 0.5f);

            var badPaths = bad.Select(kv => kv.Key).ToHashSet();
            var keptBad = result.Kept.Where(f => badPaths.Contains(f.Frame.Path)).ToList();
            var rejectedGood = result.Rejected.Count(r => !badPaths.Contains(r.Frame.Frame.Path));

            // Good-session star-count floor (p10) — anything well below it is a transparency drop.
            var goodStarP10 = good.Select(kv => kv.Value.StarCount).OrderBy(x => x).ElementAt(good.Count / 10);
            var transparencyBadKept = keptBad.Where(f => f.Metrics.StarCount < goodStarP10 * 0.6f).ToList();

            var goodRejReasons = result.Rejected.Where(r => !badPaths.Contains(r.Frame.Frame.Path))
                .GroupBy(r => r.Reason).Select(g => $"{g.Key}:{g.Count()}");
            output.WriteLine($"population={population.Count} (good={good.Count}, bad={bad.Count}); " +
                $"badRejected={bad.Count - keptBad.Count}/{bad.Count}, keptBad starcounts=[{string.Join(",", keptBad.Select(f => f.Metrics.StarCount).OrderBy(x => x))}]; " +
                $"goodRejected={rejectedGood} ({string.Join(" ", goodRejReasons)}); goodStarP10={goodStarP10}");

            transparencyBadKept.ShouldBeEmpty(
                $"every clearly-low-transparency bad frame must be rejected; kept: " +
                string.Join(", ", transparencyBadKept.Select(f => $"{Path.GetFileName(f.Frame.Path)}({f.Metrics.StarCount})")));
            keptBad.Count.ShouldBeLessThanOrEqualTo(bad.Count / 8, "the large majority of bad frames must be rejected");

            // The gate also trims the session's soft-focus + hazy tail — DESIRABLE for a training
            // set (purity > yield). What must hold is that this rejection is PRINCIPLED (targets the
            // measurably-worse tail), not random, and stays a minority. Rejected good frames must be
            // softer (higher HFD) and/or thinner (lower star count) than the kept good median.
            var keptGood = result.Kept.Where(f => !badPaths.Contains(f.Frame.Path)).ToList();
            var rejectedGoodFrames = result.Rejected.Where(r => !badPaths.Contains(r.Frame.Frame.Path)).Select(r => r.Frame).ToList();
            float MedHfd(IEnumerable<SessionFrameAnalyzer.AnalyzedFrame> s) => s.Select(f => f.Metrics.MedianHfd).OrderBy(x => x).ElementAt(s.Count() / 2);
            float MedStars(IEnumerable<SessionFrameAnalyzer.AnalyzedFrame> s) => s.Select(f => (float)f.Metrics.StarCount).OrderBy(x => x).ElementAt(s.Count() / 2);

            (rejectedGood / (double)good.Count).ShouldBeLessThan(0.15, "trimming the tail is fine; slaughtering the session is not");
            (MedHfd(rejectedGoodFrames) > MedHfd(keptGood) || MedStars(rejectedGoodFrames) < MedStars(keptGood))
                .ShouldBeTrue("rejected good frames must be the measurably-worse tail (softer or thinner), not random");
        }

        private static Dictionary<string, FrameMetrics> LoadMetricCache(string path)
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, FrameMetrics>();
            }
            var dict = new Dictionary<string, FrameMetrics>();
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split('\t');
                if (parts.Length == 5)
                {
                    dict[parts[0]] = new FrameMetrics(
                        float.Parse(parts[1]), float.Parse(parts[2]), float.Parse(parts[3]), int.Parse(parts[4]));
                }
            }
            return dict;
        }

        private static void SaveMetricCache(string path, Dictionary<string, FrameMetrics> cache)
        {
            using var w = new StreamWriter(path, append: false);
            foreach (var (p, m) in cache)
            {
                w.WriteLine($"{p}\t{m.MedianHfd}\t{m.MedianFwhm}\t{m.MedianEllipticity}\t{m.StarCount}");
            }
        }
    }
}
