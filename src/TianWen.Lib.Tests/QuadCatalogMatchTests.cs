using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins phase C1: a quad match against a CATALOG has to compare the five scale-free ratios, and
    /// window <see cref="StarQuad.Dist1"/> multiplicatively rather than in absolute pixels.
    /// </summary>
    /// <remarks>
    /// <para>The failure this guards is silent and was live for a long time: the shipped matcher
    /// applies one <c>quadTolerance</c> to six values of which <c>Dist1</c> is a length in pixels and
    /// the rest are dimensionless. Against another frame off the same camera that is right -- the
    /// lengths genuinely agree -- and against a catalog it rejects correct quads, because the
    /// projected length carries the plate scale the solve is trying to recover.</para>
    /// <para>Asserted on RAW PAIRS rather than only on the lock, because the pair count is the quantity
    /// C1 changed (73 -> 1,368 over 24 panels) and a lock count does not measure it: the shipped path
    /// locks 6 of 24 off a 73-pair noise floor, and that 6 is deterministic (the RANSAC is seeded) but
    /// fragile against any change to the INPUT set -- a detector tweak, a different cut -- so a bare
    /// "it locks now, it did not before" pin would go red for reasons unrelated to the matcher.</para>
    /// </remarks>
    [Collection("Astrometry")]
    public class QuadCatalogMatchTests
    {
        private const int TopK = 500;

        [Fact]
        public void MatchingOnTheRatiosFindsTheQuadsTheMixedUnitTestRejects()
        {
            var manifest = VelaMosaicStarLists.Manifest;
            var panel = manifest.Panels[0];
            var frame = panel.Frames[0];

            var imageQuads = VelaProjection.BuildQuads(frame.DetectedPoints(TopK));
            var catalogQuads = VelaProjection.BuildQuads(
                VelaProjection.ProjectTopK(manifest, panel, frame, TopK));

            var (_, shippedDiag) = StarReferenceTable.FindFitWithDiagnostics(
                imageQuads, catalogQuads, minimumCount: 3, quadTolerance: 0.008f, scaleTolerance: null);
            var (ratioOnly, ratioDiag) = StarReferenceTable.FindFitWithDiagnostics(
                imageQuads, catalogQuads, minimumCount: 3, quadTolerance: 0.008f, scaleTolerance: 0.01f);

            ratioDiag.RawPairs.ShouldBeGreaterThan(
                shippedDiag.RawPairs * 5,
                $"the ratio-only match must find the quads the mixed-unit one rejects "
                + $"({shippedDiag.RawPairs} -> {ratioDiag.RawPairs} raw pairs; measured 73 -> 1,368 over 24 panels, "
                + "and the bar is 5x so an unrelated change cannot fail this by a few percent)");

            ratioOnly.ShouldNotBeNull("all 24 frozen panels lock once the descriptor is scale-free");
        }

        /// <summary>
        /// The other half, and the reason this could ship: an unspecified <c>scaleTolerance</c> leaves
        /// the shipped path byte-identical, which is what every stacking caller depends on.
        /// </summary>
        [Fact]
        public void WithoutAScaleToleranceNothingAboutTheStackingPathMoves()
        {
            var manifest = VelaMosaicStarLists.Manifest;
            var panel = manifest.Panels[0];
            var frame = panel.Frames[0];

            // The same field against ITSELF is the stacking question -- one camera, one scale -- and
            // it must still match on the absolute test, with no scale tolerance anywhere.
            var quads = VelaProjection.BuildQuads(frame.DetectedPoints(TopK));

            var (table, diag) = StarReferenceTable.FindFitWithDiagnostics(
                quads, quads, minimumCount: 6, quadTolerance: 0.008f);

            table.ShouldNotBeNull("a field must still register against itself through the default path");
            diag.RawPairs.ShouldBeGreaterThanOrEqualTo(quads.Count,
                "every quad is its own match under an absolute window, so the default path must be untouched");
        }
    }
}
