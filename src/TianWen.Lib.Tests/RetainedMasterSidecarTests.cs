using System;
using System.IO;
using System.Linq;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// What a retained bake master carries BESIDE its pixels, which for every bake before this one was
    /// nothing at all.
    ///
    /// <para>The two absences cost the same way and were both invisible in the master itself. With no
    /// coverage plane, <c>ViewerActions.ScanForCrop</c> cannot reach its exact tier and every consumer
    /// falls back to the edge-noise ESTIMATE, which refuses an edge whose band never settles and keeps
    /// the partial-coverage ramp a background model then fits. With no WCS,
    /// <c>MasterPreviewRenderer</c> never runs SPCC, so a preview falls back to sky-background white
    /// balance -- which neutralises the BACKGROUND and leaves the signal on the raw OSC balance.</para>
    ///
    /// <para>Both are asserted on the FILES, not on a return value, because the failure mode is a write
    /// that quietly did not happen: the old code passed <c>wcs: null</c> and dropped the map, and every
    /// call still reported success.</para>
    /// </summary>
    [Collection("Imaging")]
    public class RetainedMasterSidecarTests : IDisposable
    {
        private const int W = 96;
        private const int H = 80;
        private const string SessionId = "TestCam/None/Target/2026-01-01|TestCam|Target|None";

        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "tianwen-retained-sidecar", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A locked temp file must not fail a green test run.
            }
        }

        private static Image Frame(float level)
        {
            var planes = new float[3][,];
            for (var c = 0; c < 3; c++)
            {
                var p = new float[H, W];
                for (var y = 0; y < H; y++)
                {
                    for (var x = 0; x < W; x++)
                    {
                        p[y, x] = level + c + (x * 0.01f);
                    }
                }
                planes[c] = p;
            }
            return new Image(planes, BitDepth.Float32, maxValue: 65535f, minValue: 0f, pedestal: 0f,
                new ImageMeta { SensorType = SensorType.Color });
        }

        [Fact]
        public void TheCoveragePlaneIsWrittenBesideTheMasterAndSaysWhichKindOfMapItIs()
        {
            var master = Frame(1000f);
            var coverage = Frame(24f);

            RetainedMasterStore.Write(_root, SessionId, master, frameCount: 24,
                strategy: IntegrationStrategyKind.BayerDrizzle,
                rejectionMap: coverage, rejectionMapIsCoverage: true, meanRejectionRate: 0.0)
                .ShouldBeTrue();

            var masterPath = RetainedMasterStore.PathFor(_root, SessionId);
            // Through the writer's own rule, never by appending the suffix to the master path:
            // it strips the .fits stem first, so a hand-built path is one directory listing away
            // from concluding the file was never written.
            var sidecar = IntegrationFitsWriter.RejectionPathFor(masterPath);
            File.Exists(sidecar).ShouldBeTrue("the coverage plane must sit beside the master under the stacker's own name");

            // MAPKIND is the whole point of the file: drizzle writes accumulated WEIGHT here and every
            // other strategy a rejection FRACTION, and the two are opposite in sense. A reader that
            // wants coverage has to see it stated, and its absence is never read as either.
            Image.TryReadFitsFile(sidecar, out var readBack, out _).ShouldBeTrue();
            Image.TryReadFitsHeader(sidecar, out var meta).ShouldBeTrue();
            readBack.Width.ShouldBe(W);
            readBack.Height.ShouldBe(H);
            var cards = File.ReadAllText(sidecar, System.Text.Encoding.ASCII);
            cards.ShouldContain(IntegrationFitsWriter.MapKindCard);
            cards.ShouldContain(IntegrationFitsWriter.CoverageMapKind);
        }

        [Fact]
        public void TheStoreEnumeratesMastersAndNotTheSidecarsBesideThem()
        {
            // The sidecar is a .fits in the SAME folder, so the moment one was written beside a
            // master every `*.fits` glob started reporting twice as many masters as the bake made.
            // Two of DatasetBuildRunnerTests' assertions were exactly that glob and went red on the
            // change that added the sidecar -- red for the right reason, and in CI rather than here,
            // because the suite I ran locally was chosen from the files I had edited rather than
            // from the files my change AFFECTED.
            RetainedMasterStore.Write(_root, SessionId, Frame(1000f), frameCount: 24,
                strategy: IntegrationStrategyKind.BayerDrizzle,
                rejectionMap: Frame(24f), rejectionMapIsCoverage: true, meanRejectionRate: 0.0)
                .ShouldBeTrue();

            var dir = Path.Combine(_root, RetainedMasterStore.DirectoryName);
            Directory.GetFiles(dir, "*.fits").Length
                .ShouldBe(2, "the master and its sidecar, which is what makes a bare glob wrong");

            var masters = RetainedMasterStore.EnumerateMasters(_root).ToArray();
            masters.Length.ShouldBe(1);
            masters[0].ShouldBe(RetainedMasterStore.PathFor(_root, SessionId));
            IntegrationFitsWriter.IsRejectionMapPath(masters[0]).ShouldBeFalse();
            IntegrationFitsWriter.IsRejectionMapPath(IntegrationFitsWriter.RejectionPathFor(masters[0])).ShouldBeTrue();
        }

        [Fact]
        public void AMasterRetainedWithASolutionCarriesItAndOneWithoutIsStillWritten()
        {
            var solved = new WCS(10.5, -59.7)
            {
                CRPix1 = W / 2.0,
                CRPix2 = H / 2.0,
                CD1_1 = -0.001,
                CD1_2 = 0.0,
                CD2_1 = 0.0,
                CD2_2 = 0.001,
            };

            RetainedMasterStore.Write(_root, SessionId, Frame(1000f), frameCount: 8, wcs: solved).ShouldBeTrue();
            var path = RetainedMasterStore.PathFor(_root, SessionId);
            Image.TryReadFitsFile(path, out _, out var readWcs).ShouldBeTrue();
            readWcs.ShouldNotBeNull("a solved master must read back solved");
            readWcs.Value.CenterRA.ShouldBe(10.5, 1e-6);
            readWcs.Value.CenterDec.ShouldBe(-59.7, 1e-6);

            // The header is the ONE place the pixel conventions differ: WriteToHeader adds one and
            // stamps PIXORIG, FromHeader subtracts it, so a round trip must land back on the
            // detected-centroid, 0-based value rather than one pixel away.
            readWcs.Value.CRPix1.ShouldBe(W / 2.0, 1e-6);
            readWcs.Value.CRPix2.ShouldBe(H / 2.0, 1e-6);

            // The retain block is best-effort by contract, so a session whose master did not solve is
            // retained anyway: the master is the job and the WCS is a bonus. Before this change EVERY
            // master took this path, which is why it has to keep working.
            const string Unsolved = "TestCam/None/Other/2026-01-02|TestCam|Other|None";
            RetainedMasterStore.Write(_root, Unsolved, Frame(900f), frameCount: 8).ShouldBeTrue();
            var plain = RetainedMasterStore.PathFor(_root, Unsolved);
            File.Exists(plain).ShouldBeTrue();
            File.Exists(IntegrationFitsWriter.RejectionPathFor(plain))
                .ShouldBeFalse("no map was handed over, so no sidecar should be invented");
        }
    }
}
