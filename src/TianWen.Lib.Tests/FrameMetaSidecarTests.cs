using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The sidecar exists because N.I.N.A. models a motorised filter wheel and not a filter screwed
    /// on by hand, so those frames carry no FILTER card at all. Everything here drives real files on
    /// disk, because the cascade IS filesystem behaviour and a stubbed one would pin nothing.
    /// </summary>
    [Collection("Imaging")]
    public class FrameMetaSidecarTests
    {
        private static Image MakeSynthetic(FrameType type, Filter filter, int minute)
        {
            var channel = new float[4, 4];
            for (var h = 0; h < 4; h++)
                for (var w = 0; w < 4; w++)
                    channel[h, w] = 0.1f * (h * 4 + w);

            var meta = new ImageMeta(
                Instrument: "synthetic",
                ExposureStartTime: new DateTimeOffset(2026, 5, 14, 21, 0, 0, TimeSpan.Zero).AddMinutes(minute),
                ExposureDuration: TimeSpan.FromSeconds(120),
                FrameType: type,
                Telescope: "TestScope",
                PixelSizeX: 3.76f,
                PixelSizeY: 3.76f,
                FocalLength: 400,
                FocusPos: -1,
                Filter: filter,
                BinX: 1,
                BinY: 1,
                CCDTemperature: -10f,
                SensorType: SensorType.Monochrome,
                BayerOffsetX: 0,
                BayerOffsetY: 0,
                RowOrder: RowOrder.TopDown,
                Latitude: float.NaN,
                Longitude: float.NaN);

            return new Image([channel], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, imageMeta: meta);
        }

        // Every frame needs a distinct start: discovery dedupes on
        // (camera, DATE-OBS, exposure, dimensions), so same-instant frames collapse into one.
        private static int _minute;

        private static void WriteFrame(string folder, string name, FrameType type, Filter filter)
        {
            Directory.CreateDirectory(folder);
            MakeSynthetic(type, filter, Interlocked.Increment(ref _minute)).WriteToFitsFile(Path.Combine(folder, name));
        }

        private static void WriteSidecar(string folder, string json)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, FrameMetaSidecarResolver.FileName), json);
        }

        private static string CreateTempDir([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), "TianWen.SidecarTests", name ?? "unnamed", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static async Task<System.Collections.Generic.List<FrameInfo>> CollectAsync(
            FitsFolderFrameSource source, CancellationToken ct)
        {
            var list = new System.Collections.Generic.List<FrameInfo>();
            await foreach (var frame in source.EnumerateAsync(ct))
            {
                list.Add(frame);
            }
            return list;
        }

        [Fact]
        public async Task GivenAFrameWithNoFilterAndADeclaration_WhenScanning_ThenTheFilterIsFilledIn()
        {
            var root = CreateTempDir();
            WriteFrame(root, "l1.fits", FrameType.Light, Filter.None);
            WriteSidecar(root, """{ "filter": "Antlia ALP-T" }""");

            var source = new FitsFolderFrameSource(root, recursive: true);
            var frames = await CollectAsync(source, TestContext.Current.CancellationToken);

            frames.ShouldHaveSingleItem().Meta.Filter.IdentityKey.ShouldBe("Antlia ALP-T");
            source.SidecarStats.Files.ShouldBe(1);
            source.SidecarStats.FilterFilled.ShouldBe(1);
            source.SidecarStats.Malformed.ShouldBe(0);
        }

        [Fact]
        public async Task GivenNoDeclaration_WhenScanning_ThenAFilterlessFrameStaysFilterless()
        {
            // The precondition the whole mechanism rests on: a Filter.None frame round-trips through
            // FITS to an empty IdentityKey, which is what "this needs a declaration" looks like.
            var root = CreateTempDir();
            WriteFrame(root, "l1.fits", FrameType.Light, Filter.None);

            var source = new FitsFolderFrameSource(root, recursive: true);
            var frames = await CollectAsync(source, TestContext.Current.CancellationToken);

            frames.ShouldHaveSingleItem().Meta.Filter.IdentityKey.ShouldBe("");
            source.SidecarStats.IsEmpty.ShouldBeTrue();
        }

        [Fact]
        public async Task GivenAFrameThatRecordedItsOwnFilter_WhenScanning_ThenTheDeclarationDoesNotOverrideIt()
        {
            var root = CreateTempDir();
            WriteFrame(root, "l1.fits", FrameType.Light, Filter.OxygenIII);
            WriteSidecar(root, """{ "filter": "Antlia ALP-T" }""");

            var source = new FitsFolderFrameSource(root, recursive: true);
            var frames = await CollectAsync(source, TestContext.Current.CancellationToken);

            frames.ShouldHaveSingleItem().Meta.Filter.IdentityKey.ShouldBe("OxygenIII");
            source.SidecarStats.FilterFilled.ShouldBe(0);
            // Surfaced rather than swallowed: a declaration doing nothing is usually a misplaced file.
            source.SidecarStats.FilterAlreadyPresent.ShouldBe(1);
        }

        [Fact]
        public async Task GivenLightsAndFlatsUnderOneDeclaration_WhenScanning_ThenBothLearnTheFilter()
        {
            // The reason this lives on the frame source and not in SessionDiscovery. BestFlat scores
            // a filter mismatch at +1000, so lights-with-filter plus flats-without would be worse
            // than leaving both blank.
            var root = CreateTempDir();
            WriteSidecar(root, """{ "filter": "L-eXtreme" }""");
            WriteFrame(Path.Combine(root, "LIGHT"), "l1.fits", FrameType.Light, Filter.None);
            WriteFrame(Path.Combine(root, "FLAT"), "f1.fits", FrameType.Flat, Filter.None);

            var source = new FitsFolderFrameSource(root, recursive: true);
            var frames = await CollectAsync(source, TestContext.Current.CancellationToken);

            frames.Count.ShouldBe(2);
            frames.Select(f => f.Meta.Filter.IdentityKey).Distinct().ShouldHaveSingleItem().ShouldBe("L-eXtreme");
            var light = frames.Single(f => f.FrameType == FrameType.Light);
            var flat = frames.Single(f => f.FrameType == FrameType.Flat);
            MasterGroupKey.FromFrame(light).FilterName.ShouldBe(MasterGroupKey.FromFrame(flat).FilterName);
        }

        [Fact]
        public async Task GivenADeclarationAtTheRootAndADeeperOne_WhenScanning_ThenTheNearestWins()
        {
            var root = CreateTempDir();
            WriteSidecar(root, """{ "filter": "L-eXtreme" }""");
            WriteFrame(Path.Combine(root, "night1", "LIGHT"), "a.fits", FrameType.Light, Filter.None);
            WriteSidecar(Path.Combine(root, "night2"), """{ "filter": "Antlia ALP-T" }""");
            WriteFrame(Path.Combine(root, "night2", "LIGHT"), "b.fits", FrameType.Light, Filter.None);

            var source = new FitsFolderFrameSource(root, recursive: true);
            var frames = await CollectAsync(source, TestContext.Current.CancellationToken);

            frames.Single(f => f.Path.EndsWith("a.fits", StringComparison.Ordinal))
                .Meta.Filter.IdentityKey.ShouldBe("L-eXtreme");
            // Inherited two levels down, and replaced wholesale by the nearer declaration.
            frames.Single(f => f.Path.EndsWith("b.fits", StringComparison.Ordinal))
                .Meta.Filter.IdentityKey.ShouldBe("Antlia ALP-T");
            source.SidecarStats.Files.ShouldBe(2);
            source.SidecarStats.FilterFilled.ShouldBe(2);
        }

        [Fact]
        public async Task GivenARecognisedFilterName_WhenDeclared_ThenItCanonicalisesLikeARecordedOne()
        {
            // A declared "Ha" and a recorded "Ha" must be indistinguishable, or a manual night and a
            // filter-wheel night of the same line would never group or calibrate together.
            var root = CreateTempDir();
            WriteFrame(Path.Combine(root, "manual"), "m.fits", FrameType.Light, Filter.None);
            WriteSidecar(Path.Combine(root, "manual"), """{ "filter": "Ha" }""");
            WriteFrame(Path.Combine(root, "wheel"), "w.fits", FrameType.Light, Filter.HydrogenAlpha);

            var source = new FitsFolderFrameSource(root, recursive: true);
            var frames = await CollectAsync(source, TestContext.Current.CancellationToken);

            frames.Select(f => f.Meta.Filter.IdentityKey).Distinct()
                .ShouldHaveSingleItem().ShouldBe("HydrogenAlpha");
        }

        [Fact]
        public async Task GivenAMalformedDeclaration_WhenScanning_ThenItIsCountedAndTheScanContinues()
        {
            // A stray character must not abort a sweep over a whole archive, and must not pass
            // unnoticed either.
            var root = CreateTempDir();
            WriteFrame(root, "l1.fits", FrameType.Light, Filter.None);
            WriteSidecar(root, "{ this is not json");

            var source = new FitsFolderFrameSource(root, recursive: true);
            var frames = await CollectAsync(source, TestContext.Current.CancellationToken);

            frames.ShouldHaveSingleItem().Meta.Filter.IdentityKey.ShouldBe("");
            source.SidecarStats.Malformed.ShouldBe(1);
            source.SidecarStats.Files.ShouldBe(0);
        }

        [Fact]
        public async Task GivenCommentsAndTrailingCommas_WhenParsing_ThenTheHandWrittenFileStillLoads()
        {
            var root = CreateTempDir();
            WriteFrame(root, "l1.fits", FrameType.Light, Filter.None);
            WriteSidecar(root, """
                {
                  // screwed onto the nosepiece, NINA has no wheel configured
                  "filter": "Antlia ALP-T",
                }
                """);

            var source = new FitsFolderFrameSource(root, recursive: true);
            var frames = await CollectAsync(source, TestContext.Current.CancellationToken);

            frames.ShouldHaveSingleItem().Meta.Filter.IdentityKey.ShouldBe("Antlia ALP-T");
            source.SidecarStats.Malformed.ShouldBe(0);
        }

        [Fact]
        public async Task GivenSidecarsDisabled_WhenScanning_ThenTheDeclarationIsIgnored()
        {
            var root = CreateTempDir();
            WriteFrame(root, "l1.fits", FrameType.Light, Filter.None);
            WriteSidecar(root, """{ "filter": "Antlia ALP-T" }""");

            var source = new FitsFolderFrameSource(root, recursive: true, useSidecars: false);
            var frames = await CollectAsync(source, TestContext.Current.CancellationToken);

            frames.ShouldHaveSingleItem().Meta.Filter.IdentityKey.ShouldBe("");
            source.SidecarStats.IsEmpty.ShouldBeTrue();
        }

        [Fact]
        public async Task GivenAManualFilterNight_WhenDiscovering_ThenTheDeclarationReachesTheSessionKey()
        {
            // The whole point, end to end: a hand-fitted dual-band night carries no FILTER card, so
            // without a declaration it would group with the unfiltered broadband night beside it.
            var root = CreateTempDir();
            WriteSidecar(Path.Combine(root, "2026-03-02 dualband"), """{ "filter": "Antlia ALP-T" }""");
            WriteFrame(Path.Combine(root, "2026-03-02 dualband", "LIGHT"), "a.fits", FrameType.Light, Filter.None);
            WriteFrame(Path.Combine(root, "2026-03-02 dualband", "LIGHT"), "b.fits", FrameType.Light, Filter.None);
            WriteFrame(Path.Combine(root, "2026-03-05 broadband", "LIGHT"), "c.fits", FrameType.Light, Filter.None);

            var options = new DatasetBuildOptions
            {
                ArchiveRoots = [root],
                OutputDir = Path.Combine(root, "out"),
                MinSubsPerSession = 1,
            };
            var (sessions, stats) = await SessionDiscovery.DiscoverAsync(
                options, cancellationToken: TestContext.Current.CancellationToken);

            sessions.Length.ShouldBe(2);
            sessions.Single(s => s.RelativeDir == "2026-03-02 dualband").FilterName.ShouldBe("Antlia ALP-T");
            sessions.Single(s => s.RelativeDir == "2026-03-05 broadband").FilterName.ShouldBe("");
            stats.Sidecar.ShouldNotBeNull().FilterFilled.ShouldBe(2);
        }

        [Fact]
        public void GivenADirectoryOutsideTheRoot_WhenResolving_ThenNothingIsInherited()
        {
            // Two archive roots must not leak declarations into each other, so resolution stops at
            // the root it was constructed with rather than walking to the drive.
            var parent = CreateTempDir();
            var rootA = Path.Combine(parent, "a");
            var rootB = Path.Combine(parent, "b");
            Directory.CreateDirectory(rootA);
            Directory.CreateDirectory(rootB);
            WriteSidecar(parent, """{ "filter": "Antlia ALP-T" }""");

            new FrameMetaSidecarResolver(rootA).Resolve(rootA).ShouldBeNull();
            new FrameMetaSidecarResolver(rootA).Resolve(rootB).ShouldBeNull();
        }
    }
}
