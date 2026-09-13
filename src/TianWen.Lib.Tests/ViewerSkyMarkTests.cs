using System;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The sky mark is Crux, five baked stars with no joining lines, and its star size is DERIVED
    /// from the geometry rather than chosen.
    /// </summary>
    /// <remarks>
    /// <para><b>Two of the five merged into one blob</b> because the size was picked by eye. On this
    /// table Beta and Epsilon sit closer to each other than any other pair, so they alone decide how
    /// large every star may be -- and at the size first tried, Gamma and Beta overlapped as well.
    /// This recomputes the bound from the table and fails if the constant drifts past it, which is
    /// the only thing that stops the same mistake being made twice.</para>
    /// <para>The other half was not geometry at all: a 13 px mask was being squeezed into a 4 px box,
    /// and <c>DrawCoverageMask</c> floors every run to one pixel, so the glyph came out fatter than
    /// it should. Hence the small-bake ladder these tests also check for.</para>
    /// </remarks>
    public class ViewerSkyMarkTests
    {
        /// <summary>
        /// How far the star glyph's ink reaches from the centre of its box, as a fraction of that
        /// box. A property of the OUTLINE, recorded rather than measured off the shipped ladder.
        /// </summary>
        /// <remarks>
        /// <b>Measuring this from the bakes in <see cref="SkyIcons"/> gives the wrong answer, and the
        /// reason is the whole point of the mark.</b> Those rungs stop at 20 px because that is all
        /// the mark ever draws, and at 20 px the star's points are already partly quantised away: the
        /// ladder measures 0.3953 where the outline is 0.4268. Believing the small rung makes the
        /// overlap bound almost 6 percent too generous -- enough to re-admit the very size that merged
        /// Beta and Epsilon. Taken instead from a 39 px bake of the same U+2605 in the same DejaVu
        /// Sans, where the points are fully resolved; re-measure there if the glyph or face changes.
        /// </remarks>
        private const float StarInkRadius = 0.4268f;

        /// <summary>
        /// What the shipped ladder's own top rung measures, kept as a guard on the paragraph above:
        /// if this ever reaches <see cref="StarInkRadius"/> the ladder has grown a rung big enough to
        /// measure from, and the recorded constant can go.
        /// </summary>
        private static float LadderInkRadius()
        {
            var mask = SkyIcons.Star5.OrderByDescending(m => m.Size).First();
            var centre = mask.Size / 2f;
            var furthest = 0f;
            foreach (var run in mask.Runs)
            {
                for (var x = run.X; x < run.X + run.Width; x++)
                {
                    var dx = x + 0.5f - centre;
                    var dy = run.Y + 0.5f - centre;
                    furthest = MathF.Max(furthest, MathF.Sqrt((dx * dx) + (dy * dy)));
                }
            }

            return furthest / mask.Size;
        }

        /// <summary>
        /// The largest half-box at which no two stars' ink can touch, solved over every pair. The
        /// shipped constant has to be at or under this.
        /// </summary>
        private static float MaxHalfWithoutOverlap(float gap)
        {
            var stars = ImageRendererBase<RgbaImage>.CruxStarsForTest;
            var aspect = ImageRendererBase<RgbaImage>.CruxAspectForTest;
            var ink = StarInkRadius;

            // Rotation is rigid, so pairwise distances do not depend on the tilt; the SPAN the layout
            // normalises by does, and the widest span is the one the fit divides through.
            var span = MathF.Max(
                stars.Max(s => s.X * aspect) - stars.Min(s => s.X * aspect),
                stars.Max(s => s.Y) - stars.Min(s => s.Y));

            var best = 0.5f;
            for (var i = 0; i < stars.Length; i++)
            {
                for (var j = i + 1; j < stars.Length; j++)
                {
                    var dx = (stars[i].X - stars[j].X) * aspect;
                    var dy = stars[i].Y - stars[j].Y;
                    var d = MathF.Sqrt((dx * dx) + (dy * dy)) / span;
                    var weights = stars[i].Weight + stars[j].Weight;
                    var h = d / ((2f * ink * weights * (1f + gap)) + (2f * d));
                    best = MathF.Min(best, h);
                }
            }

            return best;
        }

        /// <summary>
        /// <b>No two stars overlap, at the size actually shipped.</b> The bound is recomputed here
        /// rather than restated, so changing the star table or the glyph moves the bound with it.
        /// </summary>
        [Fact]
        public void TheStarSizeLeavesEveryPairClearOfItsNeighbour()
        {
            var shipped = ImageRendererBase<RgbaImage>.CruxStarHalfForTest;
            var bound = MaxHalfWithoutOverlap(gap: 0f);

            shipped.ShouldBeLessThanOrEqualTo(bound,
                $"a full-weight star at half={shipped:F4} overlaps its nearest neighbour; the table " +
                $"allows at most {bound:F4}");
        }

        /// <summary>
        /// And it is not needlessly small either: a mark whose stars are far under the bound is
        /// throwing away the only pixels it has.
        /// </summary>
        [Fact]
        public void TheStarSizeIsNotLeavingPixelsOnTheTable()
        {
            var shipped = ImageRendererBase<RgbaImage>.CruxStarHalfForTest;
            var bound = MaxHalfWithoutOverlap(gap: 0f);

            shipped.ShouldBeGreaterThan(bound * 0.8f,
                $"half={shipped:F4} is well under the {bound:F4} the geometry allows, so the stars " +
                "are smaller than they need to be at every DPI");
        }

        /// <summary>
        /// <b>The ladder covers what the mark asks for, so no mask is ever scaled far.</b> A run is a
        /// row of pixels: squeezing a 13 px star into a 4 px box is what fattened the glyph against
        /// DrawCoverageMask's one-pixel floor and merged neighbours in the first place.
        /// </summary>
        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        [InlineData(2f)]
        [InlineData(3f)]
        public void EveryStarBoxHasABakeNearIt(float dpiScale)
        {
            var mark = 13f * dpiScale;
            var half = ImageRendererBase<RgbaImage>.CruxStarHalfForTest;

            foreach (var star in ImageRendererBase<RgbaImage>.CruxStarsForTest)
            {
                var box = 2f * half * mark * star.Weight;
                var chosen = IconBaker.NearestSize(SkyIcons.Star5, box);

                // Within a quarter of the box: close enough that the snapping in DrawCoverageMask is
                // rounding, not resampling.
                var drift = MathF.Abs(chosen.Size - box) / box;
                drift.ShouldBeLessThan(0.25f,
                    $"at dpi {dpiScale} a star box of {box:F2} px picks the {chosen.Size} px bake");
            }
        }

        /// <summary>
        /// The ladder's top rung still under-measures the outline, which is why
        /// <see cref="StarInkRadius"/> is recorded rather than computed. If this ever fails, the
        /// ladder has a rung big enough to measure from and the constant should be deleted in favour
        /// of measuring it.
        /// </summary>
        [Fact]
        public void TheShippedLadderCannotMeasureTheGlyphItself()
        {
            LadderInkRadius().ShouldBeLessThan(StarInkRadius * 0.99f,
                "the top rung now resolves the star's points, so measure the radius instead of " +
                "carrying it as a constant");
        }

        /// <summary>
        /// The bake is a real star, not an empty mask: a face that does not carry the codepoint bakes
        /// nothing at all, and a mark that draws nothing is the failure baking exists to prevent.
        /// </summary>
        [Fact]
        public void TheStarBakeCarriesInkAtEverySize()
        {
            SkyIcons.Star5.ShouldNotBeEmpty();

            foreach (var mask in SkyIcons.Star5)
            {
                mask.IsEmpty.ShouldBeFalse($"the {mask.Size} px bake has no coverage");
            }
        }
    }
}
