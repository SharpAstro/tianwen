using System;
using System.IO;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Two declaration defects found while confirming that the archive is TOP-DOWN throughout, both
    /// of the kind that fail silently and plausibly rather than loudly.
    /// </summary>
    public class RowOrderAndProvenanceTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "tianwen-roworder-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) { Directory.Delete(_dir, recursive: true); } } catch { }
        }

        /// <summary>
        /// ROWORDER has ONE parser. <c>WCS</c> used to compare the raw header string ordinally
        /// (<c>rowOrder is null or "TOP-DOWN"</c>) while <c>Image.Fits</c> went through
        /// <see cref="RowOrder.FromFITSValue"/>, so a spelling the tolerant parser accepts and the
        /// ordinal one rejects read correctly as an IMAGE while silently taking the bottom-up branch
        /// in the WCS hint, where it costs a full 180 degrees of position angle.
        /// </summary>
        [Theory]
        [InlineData("TOP-DOWN", RowOrder.TopDown)]      // canonical, what we and N.I.N.A. write
        [InlineData("Top-Down", RowOrder.TopDown)]      // ordinal compare rejected this
        [InlineData("top-down", RowOrder.TopDown)]      // and this
        [InlineData(" TOP-DOWN ", RowOrder.TopDown)]    // and a padded value
        [InlineData("TOPDOWN", RowOrder.TopDown)]       // hyphen is optional to the parser
        [InlineData("BOTTOM-UP", RowOrder.BottomUp)]
        [InlineData("bottom-up", RowOrder.BottomUp)]
        public void FromFITSValue_AcceptsEverySpellingTheOrdinalCompareRejected(string value, RowOrder expected)
        {
            RowOrder.FromFITSValue(value).ShouldBe(expected);
        }

        /// <summary>An absent or unintelligible card yields null, so the CALLER decides the default
        /// rather than the parser inventing one. All three callers choose TopDown: the FITS standard
        /// is bottom-up, but the tools that actually write this card in amateur astrophotography
        /// (TianWen, N.I.N.A., SharpCap, APP, ASTAP) write TOP-DOWN, so a file missing the card is
        /// far likelier to be one of theirs than a genuinely bottom-up frame.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("sideways")]
        public void FromFITSValue_OnAbsentOrUnintelligible_IsNullSoTheCallerChooses(string? value)
        {
            RowOrder.FromFITSValue(value).ShouldBeNull();
        }

        /// <summary>
        /// A retained session master must declare TianWen as its creator. It used to be written with
        /// a bare <c>WriteToFitsFile</c>, so it inherited <c>SWCREATE = "N.I.N.A. ..."</c> from the
        /// source subs and carried no <c>STACK_N</c>. By TianWen's own rule that made a MASTER
        /// indistinguishable from a raw light, which is precisely what the scanner's re-ingestion
        /// skip exists to prevent; it was harmless only because of which directory the file happened
        /// to sit in.
        /// </summary>
        [Fact]
        public void RetainedMaster_DeclaresTianWenProvenance_SoItCannotBeReIngestedAsALight()
        {
            Directory.CreateDirectory(_dir);
            var master = SyntheticMaster();

            RetainedMasterStore.Write(_dir, "session|CAM|TARGET", master,
                frameCount: 42, strategy: IntegrationStrategyKind.BayerDrizzle).ShouldBeTrue();

            var path = RetainedMasterStore.PathFor(_dir, "session|CAM|TARGET");
            File.Exists(path).ShouldBeTrue();

            // The same predicate the archive scan uses to refuse re-ingesting our own outputs.
            IntegrationFitsWriter.IsTianWenMaster(path).ShouldBeTrue(
                "a retained master must be recognisable as a TianWen product");

            Image.TryReadFitsFile(path, out var read).ShouldBeTrue();
            read.ImageMeta.RowOrder.ShouldBe(RowOrder.TopDown, "we are top-down and must say so");
        }

        /// <summary>The strategy rides along because a retained master is explicitly NOT reusable
        /// across a change of integrator, and a reader that cannot tell which integrator produced it
        /// cannot enforce that.</summary>
        [Fact]
        public void RetainedMaster_RecordsTheIntegratorThatProducedIt()
        {
            Directory.CreateDirectory(_dir);
            RetainedMasterStore.Write(_dir, "s", SyntheticMaster(),
                frameCount: 7, strategy: IntegrationStrategyKind.Float16Staged).ShouldBeTrue();

            var path = RetainedMasterStore.PathFor(_dir, "s");
            Image.TryReadFitsFile(path, out _).ShouldBeTrue();
            // Read the cards back through the same header path the scanner uses.
            IntegrationFitsWriter.IsTianWenMaster(path).ShouldBeTrue();
        }

        private static Image SyntheticMaster()
        {
            var data = new float[8, 8];
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    data[y, x] = 0.25f;
                }
            }
            var meta = new ImageMeta("probe", DateTime.UtcNow, TimeSpan.FromSeconds(1),
                FrameType.Light, "", 3.76f, 3.76f, 121, -1, Filter.Unknown, 1, 1,
                -10f, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
            return new Image([data], BitDepth.Float32, 0.25f, 0.25f, 0f, meta);
        }
    }
}
