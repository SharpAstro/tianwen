using Shouldly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    public class DatasetSessionDiscoveryTests
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "archive");
        private static readonly string OtherRoot = Path.Combine(Path.GetTempPath(), "workingcopy");

        private static DatasetBuildOptions Options(int minSubs = 1) => new()
        {
            ArchiveRoots = [Root, OtherRoot],
            OutputDir = Path.Combine(Path.GetTempPath(), "out"),
            MinSubsPerSession = minSubs,
        };

        private static FrameInfo Frame(
            string relativePath,
            string instrument = "ZWO ASI533MC Pro",
            double exposureSeconds = 120,
            FrameType frameType = FrameType.Light,
            DateTimeOffset? start = null,
            string swCreator = "N.I.N.A. 3.2",
            int stackedFrameCount = 0,
            int width = 3008,
            int height = 3008,
            string objectName = "",
            string root = "",
            bool isMaster = false)
        {
            var meta = new ImageMeta(
                Instrument: instrument,
                ExposureStartTime: start ?? new DateTimeOffset(2025, 8, 20, 12, 0, 0, TimeSpan.Zero),
                ExposureDuration: TimeSpan.FromSeconds(exposureSeconds),
                FrameType: frameType,
                Telescope: "Samyang 135",
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
                Longitude: float.NaN,
                ObjectName: objectName,
                SWCreator: swCreator)
            { IsMaster = isMaster };
            var fullPath = Path.Combine(root.Length > 0 ? root : Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            return new FrameInfo(fullPath, width, height, 1, BitDepth.Int16, meta, stackedFrameCount);
        }

        private static (ImmutableArray<ImagingSession> Sessions, SessionDiscovery.DiscoveryStats Stats) Group(
            DatasetBuildOptions options, IReadOnlyList<(FrameInfo Frame, string Root)> frames)
            => SessionDiscovery.GroupSessions(frames, options);

        [Fact]
        public void GivenLightsUnderLightDirs_WhenGrouping_ThenSessionIsTheirParentPerCamera()
        {
            var (sessions, stats) = Group(Options(),
            [
                (Frame("2025/2025-08-20 - Helix/LIGHT/l1.fits", start: T(0)), Root),
                (Frame("2025/2025-08-20 - Helix/LIGHT/l2.fits", start: T(1)), Root),
                (Frame("2025/2025-08-20 - Helix/LIGHT/m1.fits", instrument: "ASI1600MM", start: T(2)), Root),
            ]);

            sessions.Length.ShouldBe(2);
            var helixColor = sessions.Single(s => s.Camera == "ZWO ASI533MC Pro");
            helixColor.RelativeDir.ShouldBe("2025/2025-08-20 - Helix");
            helixColor.Lights.Length.ShouldBe(2);
            helixColor.Id.ShouldBe("2025/2025-08-20 - Helix|ZWO ASI533MC Pro");
            stats.Lights.ShouldBe(3);
        }

        [Fact]
        public void GivenOneLightFolderWithSeveralTargets_WhenGrouping_ThenOneSessionPerTarget()
        {
            // A single dated N.I.N.A. LIGHT folder holding three pointings (the real
            // 2026-01-23 shape: HD 71272 + RCW 27 + Vela SNR), distinguished only by OBJECT.
            var (sessions, stats) = Group(Options(),
            [
                (Frame("2026-01-23/LIGHT/a1.fits", objectName: "HD 71272", start: T(0)), Root),
                (Frame("2026-01-23/LIGHT/a2.fits", objectName: "HD 71272", start: T(1)), Root),
                (Frame("2026-01-23/LIGHT/b1.fits", objectName: "RCW 27", start: T(2)), Root),
                (Frame("2026-01-23/LIGHT/v1.fits", objectName: "Vela SNR", start: T(3)), Root),
            ]);

            sessions.Length.ShouldBe(3);
            sessions.Select(s => s.Target).ShouldBe(["HD 71272", "RCW 27", "Vela SNR"], ignoreOrder: true);
            var vela = sessions.Single(s => s.Target == "Vela SNR");
            vela.RelativeDir.ShouldBe("2026-01-23");
            vela.Id.ShouldBe("2026-01-23|ZWO ASI533MC Pro|Vela SNR");
            sessions.Single(s => s.Target == "HD 71272").Lights.Length.ShouldBe(2);
            stats.Lights.ShouldBe(4);
        }

        [Fact]
        public void GivenExcludeObjectPattern_WhenGrouping_ThenMatchingTargetIsDroppedAndCounted()
        {
            var options = Options() with { ExcludeObjectPattern = "*vela*" };
            var (sessions, stats) = Group(options,
            [
                (Frame("2026-01-23/LIGHT/a1.fits", objectName: "HD 71272", start: T(0)), Root),
                (Frame("2026-01-23/LIGHT/b1.fits", objectName: "RCW 27", start: T(1)), Root),
                (Frame("2026-01-23/LIGHT/v1.fits", objectName: "Vela SNR", start: T(2)), Root),
                (Frame("2026-01-23/LIGHT/v2.fits", objectName: "Vela SNR", start: T(3)), Root),
            ]);

            sessions.Select(s => s.Target).ShouldBe(["HD 71272", "RCW 27"], ignoreOrder: true);
            stats.ObjectExcluded.ShouldBe(2);
            stats.Lights.ShouldBe(2);
        }

        [Fact]
        public void GivenLooseLightsInATargetDir_WhenGrouping_ThenSessionIsTheContainingDir()
        {
            var (sessions, _) = Group(Options(),
                [(Frame("Tarantula Neb ZS61/l1.fits"), Root)]);

            sessions.ShouldHaveSingleItem().RelativeDir.ShouldBe("Tarantula Neb ZS61");
        }

        [Fact]
        public void GivenSharedLibraryAndRootLooseFiles_WhenGrouping_ThenNeitherFormsASession()
        {
            var (sessions, stats) = Group(Options(),
            [
                (Frame("LIGHT/stray.fits", start: T(0)), Root),  // frame-type dir directly under root
                (Frame("loose.fits", start: T(1)), Root),        // file directly in root
            ]);

            sessions.ShouldBeEmpty();
            stats.PathExcluded.ShouldBe(2);
        }

        [Fact]
        public void GivenNonLightAndProductAndSimulatorAndOutOfRangeFrames_WhenGrouping_ThenEachGateDropsThem()
        {
            var (sessions, stats) = Group(Options(),
            [
                (Frame("s/LIGHT/l1.fits", start: T(0)), Root),
                (Frame("s/FLAT/f1.fits", frameType: FrameType.Flat, start: T(1)), Root),
                (Frame("s/LIGHT/sim.fits", instrument: "Camera V3 simulator", start: T(2)), Root),
                (Frame("s/LIGHT/burst.fits", exposureSeconds: 0.034, start: T(3)), Root),
                (Frame("s/LIGHT/livestack.fits", exposureSeconds: 8280, start: T(4)), Root),
                (Frame("s/LIGHT/master.fits", stackedFrameCount: 50, start: T(5)), Root),
                (Frame("s/LIGHT/enhanced.fits", swCreator: "TianWen.Lib 1.0", start: T(6)), Root),
                // A FOREIGN integration (PixInsight IMAGETYP='Master Light': FrameType.Light +
                // IsMaster, no STACK_N / TianWen SWCREATE) is a product, never a raw sub.
                (Frame("s/LIGHT/masterLight_pi.fits", isMaster: true, swCreator: "", start: T(7)), Root),
            ]);

            sessions.ShouldHaveSingleItem().Lights.Length.ShouldBe(1);
            stats.NotLight.ShouldBe(1);
            stats.InstrumentExcluded.ShouldBe(1);
            stats.ExposureOutOfRange.ShouldBe(2);
            stats.ProductExcluded.ShouldBe(3);
        }

        [Fact]
        public void GivenProcessedPathSegments_WhenGrouping_ThenFramesUnderThemAreExcluded()
        {
            var (sessions, stats) = Group(Options(),
            [
                (Frame("s/LIGHT/l1.fits", start: T(0)), Root),
                (Frame("s/LIGHT/AutoSave/stack.fits", start: T(1)), Root),
                (Frame("s/PROC2/reworked.fits", start: T(2)), Root),
                (Frame("s/pixinsight/cal.fits", start: T(3)), Root),
            ]);

            sessions.ShouldHaveSingleItem().Lights.Length.ShouldBe(1);
            stats.PathExcluded.ShouldBe(3);
        }

        [Fact]
        public void GivenTheSameExposureInTwoRoots_WhenGrouping_ThenFirstRootWins()
        {
            var canonical = Frame("2025/Helix/LIGHT/l1.fits", start: T(0));
            var workingCopy = Frame("Helix Working/LIGHT/l1.fits", start: T(0), root: OtherRoot);

            var (sessions, stats) = Group(Options(), [(canonical, Root), (workingCopy, OtherRoot)]);

            stats.Duplicates.ShouldBe(1);
            sessions.ShouldHaveSingleItem().RelativeDir.ShouldBe("2025/Helix");
        }

        [Fact]
        public void GivenFewerLightsThanMinimum_WhenGrouping_ThenSessionIsSkippedAndCounted()
        {
            var (sessions, stats) = Group(Options(minSubs: 3),
            [
                (Frame("s/LIGHT/l1.fits", start: T(0)), Root),
                (Frame("s/LIGHT/l2.fits", start: T(1)), Root),
            ]);

            sessions.ShouldBeEmpty();
            stats.SessionsTooSmall.ShouldBe(1);
        }

        [Fact]
        public void GivenUnsortedLights_WhenGrouping_ThenLightsAreSortedByStartAndSessionsById()
        {
            var (sessions, _) = Group(Options(),
            [
                (Frame("b/LIGHT/late.fits", start: T(9)), Root),
                (Frame("b/LIGHT/early.fits", start: T(1)), Root),
                (Frame("a/LIGHT/only.fits", start: T(5)), Root),
            ]);

            sessions.Length.ShouldBe(2);
            sessions[0].RelativeDir.ShouldBe("a");
            sessions[1].Lights[0].Path.ShouldEndWith("early.fits");
            sessions[1].Lights[1].Path.ShouldEndWith("late.fits");
        }

        [Fact]
        public void GivenSoftwareIncludePattern_WhenGrouping_ThenOnlyMatchingLightsKept_AndCountedSeparately()
        {
            // Widening the archive to a second year pulls in non-N.I.N.A. captures: SharpCap
            // planetary/EAA frames carry Light-like headers but must not train the deep-sky denoiser.
            // --software "*N.I.N.A.*" keeps only NINA lights; the SharpCap one is dropped + counted.
            var options = Options() with { SoftwareIncludePattern = "*N.I.N.A.*" };
            var (sessions, stats) = Group(options,
            [
                (Frame("s/LIGHT/n1.fits", swCreator: "N.I.N.A. 3.2.0.9001", start: T(0)), Root),
                (Frame("s/LIGHT/n2.fits", swCreator: "N.I.N.A. 3.2.0.9001", start: T(1)), Root),
                (Frame("s/LIGHT/sc1.fits", swCreator: "SharpCap 4.1.11976.0", start: T(2)), Root),
            ]);

            sessions.ShouldHaveSingleItem().Lights.Length.ShouldBe(2);
            stats.SoftwareExcluded.ShouldBe(1);
            stats.Lights.ShouldBe(2);
        }

        private static DateTimeOffset T(int minutes) => new DateTimeOffset(2025, 8, 20, 12, 0, 0, TimeSpan.Zero).AddMinutes(minutes);
    }
}
