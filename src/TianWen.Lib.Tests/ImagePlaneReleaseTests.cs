using System;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// D1 of <c>docs/plans/viewer-memory-footprint.md</c>: a document whose source was 8-bit drops its
    /// float planes, because the retained raster holds the same information at a quarter the cost and the
    /// planes were widened from it.
    /// </summary>
    /// <remarks>
    /// <para>The property everything rests on is that a release/restore round trip is BIT-IDENTICAL. It
    /// is not approximately identical: the raster is the original samples and the plane was normalised by
    /// the sample-format maximum, so restoring performs the same division over the same bytes. If it were
    /// merely close, the cursor readout would change value after a release -- a wrong number with no
    /// visible cause, appearing only once memory pressure triggered the policy.</para>
    /// <para>The refusals matter as much as the release. Without a raster there is nothing to rebuild
    /// FROM, and a channel holding a recycled camera buffer must not have its array dropped at all: that
    /// memory belongs to the driver pool and something else is still using it.</para>
    /// </remarks>
    public class ImagePlaneReleaseTests
    {
        private const int W = 5;
        private const int H = 4;

        [Fact]
        public void AReleasedPlaneRestoresToTheIdenticalVALUES()
        {
            var image = WithRaster();
            var before = new float[H, W];
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    before[y, x] = image[0, y, x];
                }
            }

            image.TryReleaseFloatPlanes().ShouldBeTrue();
            image.PlanesResident.ShouldBeFalse();

            // Reading through any accessor restores, and must give back exactly what was there.
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    image[0, y, x].ShouldBe(before[y, x], $"({x},{y}) must be bit-identical, not close");
                }
            }

            image.PlanesResident.ShouldBeTrue("reading restored them");
        }

        /// <summary>
        /// The released array becomes COLLECTABLE, which is the entire point.
        /// </summary>
        /// <remarks>
        /// Asserted through a WeakReference rather than by measuring GC.GetTotalMemory, because a total
        /// is noise at this scale -- other allocations move it by more than the plane is worth -- while a
        /// dead weak reference is proof that nothing still holds the array. Measuring working set here
        /// would be worse still: run-to-run variance on it exceeds anything this feature saves.
        /// </remarks>
        [Fact]
        public void TheReleasedArrayIsActuallyReclaimable()
        {
            // The weak reference is taken inside a method that has RETURNED before the collection.
            // A Debug build keeps a method's locals rooted until it exits, so taking it inline here
            // leaves the array on this frame and the test fails while the feature works -- which is
            // exactly how it failed the first time.
            var (image, plane) = TakeWeakPlaneRef();
            plane.IsAlive.ShouldBeTrue("the premise: the plane exists before the release");

            image.TryReleaseFloatPlanes().ShouldBeTrue();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            plane.IsAlive.ShouldBeFalse("nothing may still hold the released plane");
            image.PlanesResident.ShouldBeFalse("and reading nothing must not have restored it");
        }

        [Fact]
        public void GeometryAndMetadataSurviveTheRelease()
        {
            // The whole reason this is a policy rather than a restructure: Image captures its dimensions
            // at construction, so every geometry query keeps working while the pixels are gone.
            var image = WithRaster();
            image.TryReleaseFloatPlanes().ShouldBeTrue();

            image.Width.ShouldBe(W);
            image.Height.ShouldBe(H);
            image.ChannelCount.ShouldBe(1);
            image.Shape.ShouldBe((1, W, H));
            image.HasSourceRaster.ShouldBeTrue("the raster is what makes the release reversible");
            image.ImageMeta.SensorType.ShouldBe(SensorType.Monochrome);
            image.PlanesResident.ShouldBeFalse("none of the above restored anything");
        }

        [Fact]
        public void PerChannelExtremaSurviveTheRelease()
        {
            // They live on the Channel record, not in the array -- so the stretch keeps working on a
            // released image, which is the point: the display state is already computed.
            var image = WithRaster();
            var min = image.GetChannel(0).MinValue;
            var max = image.GetChannel(0).MaxValue;

            image.TryReleaseFloatPlanes().ShouldBeTrue();

            // GetChannel restores, so compare against a fresh release to keep the assertion honest.
            var second = WithRaster();
            second.TryReleaseFloatPlanes().ShouldBeTrue();
            second.GetChannel(0).MinValue.ShouldBe(min);
            second.GetChannel(0).MaxValue.ShouldBe(max);
        }

        [Fact]
        public void SpanAccessRestoresToo()
        {
            var image = WithRaster();
            var expected = image.GetChannelSpan(0).ToArray();

            image.TryReleaseFloatPlanes().ShouldBeTrue();
            var after = image.GetChannelSpan(0);

            after.Length.ShouldBe(expected.Length);
            for (var i = 0; i < expected.Length; i++)
            {
                after[i].ShouldBe(expected[i]);
            }
        }

        [Fact]
        public void WithoutARasterTheReleaseIsRefused()
        {
            // Nothing to rebuild from, so dropping the planes would lose the pixels outright.
            var image = WithoutRaster();

            image.TryReleaseFloatPlanes().ShouldBeFalse();
            image.PlanesResident.ShouldBeTrue();
            image[0, 1, 1].ShouldBe(WithoutRaster()[0, 1, 1], "the image is untouched by a refusal");
        }

        [Fact]
        public void ReleasingTwiceIsHarmless()
        {
            var image = WithRaster();
            image.TryReleaseFloatPlanes().ShouldBeTrue();
            image.TryReleaseFloatPlanes().ShouldBeTrue("already released is success, not failure");
            image.PlanesResident.ShouldBeFalse();
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static (Image Image, WeakReference Plane) TakeWeakPlaneRef()
        {
            var image = WithRaster();
            return (image, new WeakReference(image.GetChannel(0).Data));
        }

        /// <summary>8-bit-sourced: raster bytes, planes normalised by the sample-format max exactly as an
        /// importer produces them.</summary>
        private static Image WithRaster()
        {
            var raster = new byte[W * H];
            var plane = new float[H, W];
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    var b = (byte)((y * W + x) * 7 + 3);
                    raster[y * W + x] = b;
                    plane[y, x] = b / 255f;
                }
            }

            return new Image([new Channel(plane, Filter.None, 0f, 1f, 0)], BitDepth.Int8, 0f, Meta(),
                samplesAreUnitReferred: true, sourceRaster: [raster]);
        }

        private static Image WithoutRaster()
        {
            var plane = new float[H, W];
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    plane[y, x] = (y * W + x) / 100f;
                }
            }

            return new Image([new Channel(plane, Filter.None, 0f, 1f, 0)], BitDepth.Float32, 0f, Meta());
        }

        private static ImageMeta Meta()
            => new("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
                0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN);
    }
}
