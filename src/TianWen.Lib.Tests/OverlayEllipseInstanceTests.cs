using System;
using System.Collections.Generic;
using System.Numerics;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions.Overlays;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// <see cref="OverlayEllipseInstances"/> is the one description of what the instanced overlay
    /// pipelines draw -- desktop Vulkan and browser WebGL both fill their buffer from it, and both
    /// shaders read the layout it writes.
    ///
    /// <para><b>Nothing downstream can catch a mistake here.</b> The consumer is a vertex shader on
    /// two backends: a size in the wrong unit, a swapped semi-axis or a stride that disagrees with
    /// the attribute declaration renders something plausible-looking rather than failing, and the
    /// only surface that could show it is a GPU. So the arithmetic is pinned at the boundary where
    /// it is still ordinary CPU code.</para>
    /// </summary>
    public class OverlayEllipseInstanceTests(ITestOutputHelper output)
    {
        // 900 px tall viewport at 60 degrees -- an ordinary sky-map frame.
        private static readonly float ArcminToPx =
            (float)(TianWen.UI.Abstractions.SkyMapProjection.PixelsPerRadian(900f, 60.0) * Math.PI / (180.0 * 60.0));

        private static OverlayCandidate Candidate(
            OverlayCandidateMarker marker, bool pinned = false, double ra = 5.5, double dec = 10.0)
            => new()
            {
                CatalogIndex = default,  // nothing on this path reads it
                ObjectType = ObjectType.Galaxy,
                RA = ra,
                Dec = dec,
                UnitVec = Vector3.Normalize(new Vector3(0.3f, 0.5f, 0.81f)),
                Color = (0.5f, 0.6f, 0.7f),
                Marker = marker,
                LabelLines = ["x"],
                IsPinned = pinned,
                LabelPriority = 1f,
                LabelSlotHint = 0,
                ScreenSizeFilterArcmin = float.NaN,
            };

        private static List<float> Build(
            IReadOnlyList<OverlayCandidate> candidates, float fovAlpha = 1f,
            bool dimBelowHorizon = false, SiteContext site = default)
        {
            var instances = new List<float>();
            OverlayEllipseInstances.Build(
                candidates, instances, ArcminToPx, dpiScale: 1f, fovAlpha,
                dimBelowHorizon, site,
                OverlayEngine.PinnedMarkerColor, OverlayEngine.PinnedHaloColor);
            return instances;
        }

        private static (float MajArcmin, float MinArcmin, float PaRad, float Thickness, float A) At(
            List<float> instances, int index)
        {
            var o = index * OverlayEllipseInstances.FloatsPerInstance;
            return (instances[o + 3], instances[o + 4], instances[o + 5], instances[o + 6], instances[o + 10]);
        }

        /// <summary>
        /// The buffer is a whole number of instances of the declared width. Both pipelines derive the
        /// instance COUNT by dividing the float count by this, so a stride that drifted from the
        /// attribute declaration would not fail -- it would silently shear every marker's geometry
        /// into the next one's.
        /// </summary>
        [Fact]
        public void TheStrideMatchesTheDeclaredInstanceWidth()
        {
            var instances = Build([
                Candidate(new OverlayCandidateMarker.Ellipse(10f, 4f, (Half)30f)),
                Candidate(new OverlayCandidateMarker.Circle(8f)),
            ]);

            OverlayEllipseInstances.FloatsPerInstance.ShouldBe(11);
            instances.Count.ShouldBe(2 * OverlayEllipseInstances.FloatsPerInstance);
        }

        /// <summary>
        /// A cross has no angular extent, so it is not an instance -- the caller draws it with line
        /// primitives. Emitting one would draw a ring where a star's cross belongs.
        /// </summary>
        [Fact]
        public void ACrossIsNotAnInstance()
        {
            var instances = Build([
                Candidate(new OverlayCandidateMarker.Cross(6f)),
                Candidate(new OverlayCandidateMarker.Ellipse(10f, 4f, (Half)0f)),
                Candidate(new OverlayCandidateMarker.Cross(6f)),
            ]);

            instances.Count.ShouldBe(OverlayEllipseInstances.FloatsPerInstance);
        }

        /// <summary>
        /// A pinned target emits its halo BEFORE its marker, because the two overlap and the painter
        /// order is the buffer order -- a halo emitted second draws over the ring it is meant to sit
        /// behind. The halo is identified by its own stroke width.
        /// </summary>
        [Fact]
        public void ThePinnedHaloPrecedesItsMarker()
        {
            var instances = Build([Candidate(new OverlayCandidateMarker.Ellipse(10f, 4f, (Half)0f), pinned: true)]);

            instances.Count.ShouldBe(2 * OverlayEllipseInstances.FloatsPerInstance);
            At(instances, 0).Thickness.ShouldBe(OverlayEngine.PinnedHaloStrokePx);
            At(instances, 1).Thickness.ShouldBe(OverlayEllipseInstances.MarkerStrokePx);
        }

        /// <summary>
        /// The halo scales BOTH semi-axes by one factor and keeps the marker's position angle, so a
        /// pinned edge-on galaxy wears an elongated halo rather than a wide circle. This is the exact
        /// defect that was fixed on the two rasterisation paths separately, one of them long after the
        /// other -- which is the argument for the geometry living in one place.
        /// </summary>
        [Fact]
        public void ThePinnedHaloKeepsTheObjectsAxisRatioAndAngle()
        {
            const float maj = 12f, min = 3f;
            var instances = Build([Candidate(new OverlayCandidateMarker.Ellipse(maj, min, (Half)45f), pinned: true)]);

            var halo = At(instances, 0);
            var marker = At(instances, 1);

            (halo.MajArcmin / halo.MinArcmin).ShouldBe(maj / min, 1e-4f);
            halo.MajArcmin.ShouldBeGreaterThan(marker.MajArcmin);
            halo.PaRad.ShouldBe(marker.PaRad, 1e-6f);
            output.WriteLine($"marker {marker.MajArcmin:0.##}x{marker.MinArcmin:0.##}', halo {halo.MajArcmin:0.##}x{halo.MinArcmin:0.##}'");
        }

        /// <summary>
        /// A circle's radius is authored in SCREEN PIXELS but the shader only speaks arcminutes, so
        /// the builder divides by the current scale. It has to come back out at the pixel size it
        /// went in as, or every default marker changes size with the zoom instead of staying put.
        /// </summary>
        [Theory]
        [InlineData(180.0)]
        [InlineData(60.0)]
        [InlineData(5.0)]
        public void ACircleRoundTripsThroughArcminutesAtItsPixelSize(double fovDeg)
        {
            const float radiusPx = 8f;
            var arcminToPx = (float)(
                TianWen.UI.Abstractions.SkyMapProjection.PixelsPerRadian(900f, fovDeg) * Math.PI / (180.0 * 60.0));

            var instances = new List<float>();
            OverlayEllipseInstances.Build(
                [Candidate(new OverlayCandidateMarker.Circle(radiusPx))], instances,
                arcminToPx, dpiScale: 1f, fovAlpha: 1f, dimBelowHorizon: false, site: default,
                OverlayEngine.PinnedMarkerColor, OverlayEngine.PinnedHaloColor);

            var c = At(instances, 0);
            (c.MajArcmin * arcminToPx).ShouldBe(radiusPx, 1e-3f);
            c.MajArcmin.ShouldBe(c.MinArcmin);
        }

        /// <summary>
        /// The wide-FOV fade dims ordinary markers and leaves pinned ones alone -- that exemption is
        /// what makes a planner target a landmark at any zoom.
        /// </summary>
        [Fact]
        public void TheWideFovFadeSparesAPinnedTarget()
        {
            const float fovAlpha = 0.55f;
            var plain = Build([Candidate(new OverlayCandidateMarker.Ellipse(10f, 4f, (Half)0f))], fovAlpha);
            var pinned = Build([Candidate(new OverlayCandidateMarker.Ellipse(10f, 4f, (Half)0f), pinned: true)], fovAlpha);

            At(plain, 0).A.ShouldBe(fovAlpha, 1e-6f);
            At(pinned, 1).A.ShouldBe(1f, 1e-6f);
        }

        /// <summary>
        /// An unknown catalog position angle draws unrotated. NaN would propagate through the
        /// shader's rotation into a degenerate quad -- the marker would vanish, not tilt.
        /// </summary>
        [Fact]
        public void AnUnknownPositionAngleDrawsUnrotated()
        {
            var instances = Build([Candidate(new OverlayCandidateMarker.Ellipse(10f, 4f, Half.NaN))]);

            At(instances, 0).PaRad.ShouldBe(0f);
        }

        /// <summary>
        /// A shape with a zero minor axis still gets a sub-pixel floor on both axes, so it traces a
        /// thin ring instead of collapsing to a line the ellipse SDF cannot stroke.
        /// </summary>
        [Fact]
        public void ADegenerateShapeStillHasBothAxes()
        {
            var instances = Build([Candidate(new OverlayCandidateMarker.Ellipse(0f, 0f, (Half)0f))]);

            var m = At(instances, 0);
            (m.MajArcmin * ArcminToPx).ShouldBe(1f, 1e-3f);
            (m.MinArcmin * ArcminToPx).ShouldBe(0.5f, 1e-3f);
        }

        /// <summary>Rebuilding clears first: the buffer is reused across frames.</summary>
        [Fact]
        public void ARebuildReplacesRatherThanAppends()
        {
            var instances = new List<float>();
            var candidates = new[] { Candidate(new OverlayCandidateMarker.Ellipse(10f, 4f, (Half)0f)) };
            for (var i = 0; i < 3; i++)
            {
                OverlayEllipseInstances.Build(
                    candidates, instances, ArcminToPx, 1f, 1f, false, default,
                    OverlayEngine.PinnedMarkerColor, OverlayEngine.PinnedHaloColor);
            }

            instances.Count.ShouldBe(OverlayEllipseInstances.FloatsPerInstance);
        }
    }
}
