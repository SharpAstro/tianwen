using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// End-to-end pin for <c>tianwen dataset coverage</c> over a synthetic archive on disk: one
    /// session with a shared calibration library must produce one TSV row whose resolved dark /
    /// flat / pedestal / bias / BPM columns say what the production matcher would actually do.
    /// The selectors themselves are pinned by <see cref="CalibrationResolverTests"/> and
    /// <see cref="StackingMasterMatchTests"/>; this covers the report's assembly: the row exists
    /// at all (min-subs floor of one), the flags flip the right way, and the TSV stays parsable
    /// (every row exactly as wide as its header).
    /// </summary>
    public class CalibrationCoverageReportTests : IDisposable
    {
        private const int Size = 32;
        private readonly string _baseDir;
        private readonly string _rootDir;
        private readonly string _reportDir;

        public CalibrationCoverageReportTests()
        {
            _baseDir = Path.Combine(Path.GetTempPath(), $"CalibrationCoverageReportTests_{Guid.NewGuid():N}");
            _rootDir = Path.Combine(_baseDir, "archive");
            _reportDir = Path.Combine(_baseDir, "reports");
            Directory.CreateDirectory(_rootDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_baseDir, recursive: true); }
            catch { /* best-effort; leak to %TEMP% if handles linger */ }
        }

        [Fact]
        public async Task OneSessionWithASharedLibrary_ProducesOneFullyResolvedRow()
        {
            var sessionDate = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
            WriteFrames(Path.Combine(_rootDir, "Session1", "LIGHT"), "light", 8, FrameType.Light,
                TimeSpan.FromSeconds(60), -5f, gain: 121, offset: 25, sessionDate, objectName: "T1");
            // Shared library: frame-type folders directly under the root are sessionless by design.
            WriteFrames(Path.Combine(_rootDir, "DARK"), "dark", 2, FrameType.Dark,
                TimeSpan.FromSeconds(60), -5f, gain: 121, offset: 25, sessionDate.AddDays(-1));
            WriteFrames(Path.Combine(_rootDir, "FLAT"), "flat", 2, FrameType.Flat,
                TimeSpan.FromSeconds(3), -5f, gain: 121, offset: 25, sessionDate);
            // N.I.N.A. writes flat-matched short darks as IMAGETYP=DARK; the pedestal pool must
            // pick them up by EXPOSURE, so this is written as Dark on purpose.
            WriteFrames(Path.Combine(_rootDir, "DARKFLAT"), "darkflat", 2, FrameType.Dark,
                TimeSpan.FromSeconds(3), -5f, gain: 121, offset: 25, sessionDate.AddDays(-1));
            WriteFrames(Path.Combine(_rootDir, "BIAS"), "bias", 2, FrameType.Bias,
                TimeSpan.Zero, -5f, gain: 121, offset: 25, sessionDate.AddDays(-2));
            WriteFrames(_rootDir, "BPM-ASI-test", 1, FrameType.Light,
                TimeSpan.FromSeconds(1), -5f, gain: 121, offset: 25, sessionDate.AddDays(-3));

            var options = new DatasetBuildOptions
            {
                ArchiveRoots = [_rootDir],
                OutputDir = _reportDir,
                MinExposure = TimeSpan.FromSeconds(10),
                MinSubsPerSession = 10,
            };
            var result = await CalibrationCoverageReport.WriteAsync(options, _reportDir, cancellationToken: TestContext.Current.CancellationToken);

            result.Sessions.ShouldBe(1);
            var lines = await File.ReadAllLinesAsync(result.TsvPath, TestContext.Current.CancellationToken);
            lines.Length.ShouldBe(2);
            var header = lines[0].Split('\t');
            var row = lines[1].Split('\t');
            row.Length.ShouldBe(header.Length, "every row must be exactly as wide as the header, or the TSV is unparsable");
            string Cell(string name)
            {
                var index = Array.IndexOf(header, name);
                index.ShouldBeGreaterThanOrEqualTo(0, $"column '{name}' missing from the header");
                return row[index];
            }

            Cell("lights").ShouldBe("8");
            Cell("gain").ShouldBe("121");
            Cell("offset").ShouldBe("25");
            Cell("filter_source").ShouldBe("none");
            // 8 lights against the bake threshold of 10: the row exists AND says a bake would skip it.
            Cell("below_bake_min_subs").ShouldBe("true");

            Cell("dark_found").ShouldBe("true");
            Cell("dark_gain_match").ShouldBe("true");
            Cell("dark_exposure_s").ShouldBe("60");
            Cell("dark_age_days").ShouldBe("1");
            // Both the 60s library and the 3s flat-matched set are gain/dimension-compatible darks,
            // but only the 60s one is exposure-compatible with 60s lights.
            Cell("dark_candidates").ShouldBe("1");
            // Same exposure as the lights, so no thermal rescale and no bias consult.
            Cell("dark_bias_needed").ShouldBe("false");

            Cell("flat_found").ShouldBe("true");
            Cell("flat_filter_match").ShouldBe("true");
            Cell("flat_within_30d").ShouldBe("true");
            // The 3s IMAGETYP=DARK set must beat the bias for the flat's pedestal, by exposure and
            // never by label, and must be counted as the one exposure-gated candidate.
            Cell("pedestal_kind").ShouldBe("dark");
            Cell("pedestal_exposure_s").ShouldBe("3");
            Cell("darkflat_candidates").ShouldBe("1");

            Cell("bias_groups").ShouldBe("1");
            Cell("bias_frames").ShouldBe("2");
            Cell("bpm_files").ShouldBe("1");

            var summary = await File.ReadAllTextAsync(result.SummaryPath, TestContext.Current.CancellationToken);
            summary.ShouldContain("Dark resolved: **1/1**");
        }

        [Fact]
        public async Task AWrongGainLibrary_LeavesTheDarkColumnEmpty_ButCountsNothingCompatible()
        {
            var sessionDate = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
            WriteFrames(Path.Combine(_rootDir, "Session1", "LIGHT"), "light", 8, FrameType.Light,
                TimeSpan.FromSeconds(60), -5f, gain: 121, offset: 25, sessionDate, objectName: "T1");
            WriteFrames(Path.Combine(_rootDir, "DARK"), "dark", 2, FrameType.Dark,
                TimeSpan.FromSeconds(60), -5f, gain: 252, offset: 25, sessionDate.AddDays(-1));

            var options = new DatasetBuildOptions { ArchiveRoots = [_rootDir], OutputDir = _reportDir };
            var result = await CalibrationCoverageReport.WriteAsync(options, _reportDir, cancellationToken: TestContext.Current.CancellationToken);

            var lines = await File.ReadAllLinesAsync(result.TsvPath, TestContext.Current.CancellationToken);
            var header = lines[0].Split('\t');
            var row = lines[1].Split('\t');
            row[Array.IndexOf(header, "dark_found")].ShouldBe("false");
            // Zero candidates is the WHY beside the miss: nothing gain-compatible exists, which is
            // the actionable difference between "shoot a dark library" and "loosen a gate".
            row[Array.IndexOf(header, "dark_candidates")].ShouldBe("0");
            row[Array.IndexOf(header, "dark_slug")].ShouldBe("");
        }

        private static void WriteFrames(
            string dir, string prefix, int count, FrameType type, TimeSpan exposure,
            float tempC, short gain, int offset, DateTimeOffset start, string objectName = "")
        {
            Directory.CreateDirectory(dir);
            var pixels = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    pixels[y, x] = 100f;
                }
            }
            for (var i = 0; i < count; i++)
            {
                var meta = new ImageMeta
                {
                    Instrument = "SynthCam",
                    ExposureStartTime = start.AddMinutes(i),
                    ExposureDuration = exposure,
                    FrameType = type,
                    Telescope = "SynthScope",
                    FocalLength = 135,
                    PixelSizeX = 3.76f,
                    PixelSizeY = 3.76f,
                    BinX = 1,
                    BinY = 1,
                    CCDTemperature = tempC,
                    SensorType = SensorType.Monochrome,
                    ObjectName = objectName,
                    Gain = gain,
                    Offset = offset,
                };
                var image = new Image([pixels], BitDepth.Int16, maxValue: 4096f, minValue: 0f, pedestal: 0f, imageMeta: meta);
                image.WriteToFitsFile(Path.Combine(dir, count == 1 ? $"{prefix}.fits" : $"{prefix}_{i:D2}.fits"));
            }
        }
    }
}
