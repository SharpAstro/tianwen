using System;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The sky drawn behind a photograph has to line up WITH it, and the only way to say that is to
/// project the frame's own corners through the sky map's projection and check they land where the
/// viewer drew them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every case here would pass on a sky pointing at the right place and turned the wrong way</b>
/// if it only checked the centre, which is why nothing below asserts the centre alone: the centre is
/// the one point a wrong roll, a wrong scale and a wrong parity all leave exactly where it was.
/// </para>
/// <para>
/// The tolerance is a real quantity, not a fudge. A frame's WCS is gnomonic and the map is
/// stereographic, so they agree exactly at the view centre and drift by about theta squared over
/// four of the distance out to a point theta away: a hundredth of a pixel at the corners of these
/// one-degree frames, and 1.85 px on the ten-degree one below.
/// </para>
/// </remarks>
public class SkyBackdropViewTests
{
    private const int ImageWidth = 2000;
    private const int ImageHeight = 1500;

    /// <summary>Two arcseconds per pixel, so the whole frame spans about 1.1 by 0.8 degrees.</summary>
    private const double ScaleDeg = 2.0 / 3600.0;

    private static readonly RectF32 Pane = new(100f, 50f, 1000f, 800f);

    /// <summary>
    /// A frame that is drawn the way a star chart is: screen-up is celestial north, screen-right is
    /// west. <c>CD2_2</c> is NEGATIVE for that, because a drawn frame's rows run DOWNWARD -- image row
    /// 0 is the top of the screen (the quad's v=0 edge) -- so declination has to fall with +Y for
    /// north to end up at the top.
    /// </summary>
    private static WCS ChartOriented(double raHours = 5.0, double decDeg = -25.0) => new WCS(raHours, decDeg)
    {
        CRPix1 = ImageWidth / 2.0,
        CRPix2 = ImageHeight / 2.0,
        CD1_1 = -ScaleDeg,
        CD1_2 = 0.0,
        CD2_1 = 0.0,
        CD2_2 = -ScaleDeg,
    };

    /// <summary>
    /// The chart-oriented field turned through <paramref name="angleDeg"/> on the sensor: the base
    /// matrix times a rotation, which keeps the determinant (so the scale stays uniform and the
    /// handedness stays put). Writing the four entries by hand instead is how this test first went
    /// wrong -- <c>[[-c, -s], [-s, -c]]</c> looks like a rotation and is a shear whose determinant
    /// goes through ZERO at 45 degrees, which no rigid view can match and none should.
    /// </summary>
    private static WCS Rotated(double angleDeg)
    {
        var (sin, cos) = Math.SinCos(double.DegreesToRadians(angleDeg));
        return new WCS(5.0, -25.0)
        {
            CRPix1 = ImageWidth / 2.0,
            CRPix2 = ImageHeight / 2.0,
            CD1_1 = -ScaleDeg * cos,
            CD1_2 = ScaleDeg * sin,
            CD2_1 = -ScaleDeg * sin,
            CD2_2 = -ScaleDeg * cos,
        };
    }

    /// <summary>
    /// The other handedness, which is just as ordinary: <c>CD2_2</c> positive, so north is DOWN the
    /// screen while east stays on the same side. That combination is a reflection of the chart, not a
    /// rotation of it, and no roll can turn one into the other -- which is the whole reason the map
    /// carries a mirror. Roughly half the world's frames are each, depending on how many reflections
    /// the light path has and which way the capture software numbers its rows.
    /// </summary>
    private static WCS ReflectedOrientation() => new WCS(5.0, -25.0)
    {
        CRPix1 = ImageWidth / 2.0,
        CRPix2 = ImageHeight / 2.0,
        CD1_1 = -ScaleDeg,
        CD1_2 = 0.0,
        CD2_1 = 0.0,
        CD2_2 = ScaleDeg,
    };

    /// <summary>
    /// Projects the frame's four corners through the solved sky view and reports the largest distance,
    /// in screen pixels, between where the sky puts each one and where the viewer drew it.
    /// </summary>
    private static double WorstCornerErrorPx(in WCS wcs, float originX, float originY, float scale)
    {
        var solution = SkyBackdropView.Solve(in wcs, Pane, originX, originY, scale);
        solution.ShouldNotBeNull();

        var state = new SkyMapState();
        SkyBackdropView.ApplyTo(state, solution.Value);

        var view = state.ComputeViewMatrix();
        var pixelsPerRadian = SkyMapProjection.PixelsPerRadian(Pane.Height, state.FieldOfViewDeg);
        var centreX = Pane.X + Pane.Width * 0.5f;
        var centreY = Pane.Y + Pane.Height * 0.5f;

        var worst = 0.0;
        foreach (var (x, y) in new (double X, double Y)[]
                 {
                     (0, 0), (ImageWidth - 1, 0), (0, ImageHeight - 1), (ImageWidth - 1, ImageHeight - 1),
                 })
        {
            var sky = wcs.PixelToSky(x, y);
            sky.ShouldNotBeNull();

            SkyMapProjection.ProjectWithMatrix(sky.Value.RA, sky.Value.Dec, in view, pixelsPerRadian,
                centreX, centreY, out var sx, out var sy).ShouldBeTrue();

            // Where the image quad actually puts that pixel's centre.
            var drawnX = originX + (x + 0.5) * scale;
            var drawnY = originY + (y + 0.5) * scale;
            worst = Math.Max(worst, Math.Sqrt((sx - drawnX) * (sx - drawnX) + (sy - drawnY) * (sy - drawnY)));
        }

        return worst;
    }

    /// <summary>
    /// How far one IMAGE pixel lands from where the viewer draws it, in screen pixels, through the
    /// solved sky view.
    /// </summary>
    private static double PlacementErrorPx(in WCS wcs, double pixelX, double pixelY,
        float originX, float originY, float scale)
    {
        var solution = SkyBackdropView.Solve(in wcs, Pane, originX, originY, scale);
        solution.ShouldNotBeNull();

        var state = new SkyMapState();
        SkyBackdropView.ApplyTo(state, solution.Value);

        var view = state.ComputeViewMatrix();
        var pixelsPerRadian = SkyMapProjection.PixelsPerRadian(Pane.Height, state.FieldOfViewDeg);
        var sky = wcs.PixelToSky(pixelX, pixelY);
        sky.ShouldNotBeNull();

        SkyMapProjection.ProjectWithMatrix(sky.Value.RA, sky.Value.Dec, in view, pixelsPerRadian,
            Pane.X + Pane.Width * 0.5f, Pane.Y + Pane.Height * 0.5f, out var sx, out var sy).ShouldBeTrue();

        var drawnX = originX + (pixelX + 0.5) * scale;
        var drawnY = originY + (pixelY + 0.5) * scale;
        return Math.Sqrt((sx - drawnX) * (sx - drawnX) + (sy - drawnY) * (sy - drawnY));
    }

    /// <summary>Fitted to the pane: the frame fills it, which is what the viewer opens a file into.</summary>
    [Fact]
    public void AFittedFrame_HasItsCornersWhereTheSkyPutsThem()
    {
        // scale 0.5 draws 2000x1500 as 1000x750, centred in the 1000x800 pane.
        WorstCornerErrorPx(ChartOriented(), originX: 100f, originY: 75f, scale: 0.5f).ShouldBeLessThan(1.0);
    }

    /// <summary>
    /// Panned and zoomed in: the sky follows the picture rather than the pane, so a corner that has
    /// been dragged off screen is still where the sky says it is.
    /// </summary>
    [Fact]
    public void APannedAndZoomedFrame_KeepsTheSkyUnderIt()
    {
        WorstCornerErrorPx(ChartOriented(), originX: -340f, originY: -180f, scale: 1.6f).ShouldBeLessThan(1.0);
    }

    /// <summary>
    /// A rotated sensor. This is what the roll solves, and the failure it guards is silent: a frame
    /// and a sky that agree at the centre and are turned relative to each other everywhere else.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(30.0)]
    [InlineData(90.0)]
    [InlineData(-120.0)]
    [InlineData(179.0)]
    public void ARotatedFrame_TurnsTheSkyWithIt(double angleDeg)
    {
        WorstCornerErrorPx(Rotated(angleDeg), originX: 100f, originY: 75f, scale: 0.5f).ShouldBeLessThan(1.0);
    }

    /// <summary>
    /// A mirrored light path. A rotation cannot fix handedness, so without the mirror this test fails
    /// by roughly the frame's whole width -- the stars line up along one axis and walk off along the
    /// other, which reads as a wrong solution rather than as parity.
    /// </summary>
    [Fact]
    public void AMirroredFrame_IsMatchedByMirroringTheSkyRatherThanTurningIt()
    {
        var wcs = ReflectedOrientation();

        SkyBackdropView.Solve(in wcs, Pane, 100f, 75f, 0.5f)!.Value.Mirror.ShouldBeTrue();
        WorstCornerErrorPx(wcs, originX: 100f, originY: 75f, scale: 0.5f).ShouldBeLessThan(1.0);
    }

    /// <summary>An ordinary frame is NOT mirrored, or the flag would be a constant rather than a test.</summary>
    [Fact]
    public void AnOrdinaryFrame_IsNotMirrored()
    {
        var wcs = ChartOriented();

        SkyBackdropView.Solve(in wcs, Pane, 100f, 75f, 0.5f)!.Value.Mirror.ShouldBeFalse();
    }

    /// <summary>
    /// Near the pole, where RA stops being a direction: the reference frame the roll is solved in is
    /// well conditioned there by construction, and this is what says so.
    /// </summary>
    [Fact]
    public void AFrameNearTheCelestialPole_StillLinesUp()
    {
        WorstCornerErrorPx(ChartOriented(raHours: 2.0, decDeg: 88.5), originX: 100f, originY: 75f, scale: 0.5f)
            .ShouldBeLessThan(1.0);
    }

    /// <summary>
    /// A wide field, where the two projections genuinely differ. Ten degrees across is far wider than
    /// any telescope frame and about a 200 mm camera lens; the corner drift MEASURES 1.85 px, which is
    /// the number that says the projection difference does not need modelling -- and it is only ever
    /// visible at the frame's border, since the photograph is drawn opaque over everything inside it.
    /// </summary>
    [Fact]
    public void AWideFieldFrame_DriftsByUnderTwoPixelsAtTheCorners()
    {
        var wideScaleDeg = 18.0 / 3600.0; // 2000 px x 18 arcsec = 10 degrees
        var wcs = new WCS(5.0, -25.0)
        {
            CRPix1 = ImageWidth / 2.0,
            CRPix2 = ImageHeight / 2.0,
            CD1_1 = -wideScaleDeg,
            CD1_2 = 0.0,
            CD2_1 = 0.0,
            CD2_2 = wideScaleDeg,
        };

        WorstCornerErrorPx(wcs, originX: 100f, originY: 75f, scale: 0.5f).ShouldBeLessThan(2.0);
    }

    /// <summary>
    /// The field of view states what the PANE covers, not what the frame does. Zoomed out to a
    /// quarter, the pane holds four times the sky, and that is the number the star field is drawn at.
    /// </summary>
    [Fact]
    public void TheFieldOfView_DescribesThePaneRatherThanTheFrame()
    {
        var wcs = ChartOriented();

        var fitted = SkyBackdropView.Solve(in wcs, Pane, 100f, 75f, 0.5f)!.Value.FieldOfViewDeg;
        var zoomedOut = SkyBackdropView.Solve(in wcs, Pane, 350f, 213f, 0.125f)!.Value.FieldOfViewDeg;

        // 800 pane pixels at 0.5 covers 1600 image pixels, at 0.125 covers 6400: four times the sky.
        fitted.ShouldBe(1600 * ScaleDeg, 0.002);
        zoomedOut.ShouldBe(4 * fitted, 0.01);
    }

    /// <summary>
    /// Zoomed far out, where the frame is a postage stamp on a whole-sky view. Reported from the app:
    /// the sky flipped through 180 degrees as the view widened, and kept flipping.
    /// </summary>
    /// <remarks>
    /// <b>The cause was a probe outside the sensor.</b> The solver used to ask the WCS what was at the
    /// PANE's centre, which at these zooms is tens of thousands of virtual pixels off the frame -- 65
    /// degrees away on this fixture at 2%. A gnomonic deprojection is meaningless that far out and
    /// past 90 degrees it wraps to the antipode, so the view centre jumped to the other side of the
    /// sky. It now probes the frame's own reference pixel and fits the rotation to where those probes
    /// are DRAWN, which is exactly as valid at 2% as at 200%.
    /// <para>
    /// Asserted as a CONTINUOUS walk rather than at one zoom, because a flip is a discontinuity: any
    /// single zoom looks fine on its own, and the bug was one step to the next.
    /// </para>
    /// </remarks>
    [Fact]
    public void ZoomingRightOut_NeverFlipsTheSky()
    {
        var wcs = ChartOriented();

        // 0.5 down to about 0.008, the viewer's floor, in steps small enough that a real rotation
        // would move less between them than the tolerance below.
        for (var scale = 0.5f; scale > 0.008f; scale *= 0.9f)
        {
            // Zoomed around a fixed anchor rather than re-centred, which is what a wheel zoom does:
            // the frame shrinks toward a corner of the pane and the pane's own centre ends up well
            // OFF it. That is the condition the bug needed -- centred, the old probe landed on the
            // frame and behaved -- and the viewer allows it, since its placement only confines the
            // image to the pane, not to the middle of it.
            var originX = Pane.X + 40f;
            var originY = Pane.Y + 30f;

            var solution = SkyBackdropView.Solve(in wcs, Pane, originX, originY, scale);
            solution.ShouldNotBeNull($"scale {scale}");

            // WHERE THE FRAME IS, at every zoom: this is what a flip breaks, and it is exact by
            // construction when the view is right, because the solver fits the rotation through this
            // very pixel. Under the old probe it lands on the far side of the sky.
            PlacementErrorPx(wcs, wcs.CRPix1, wcs.CRPix2, originX, originY, scale)
                .ShouldBeLessThan(1.0, $"the frame is not where it is drawn at scale {scale}");

            // Its full extent, while the frame is still big enough on screen for its extent to mean
            // anything. Below that the two projections' scales differ by a few percent where the
            // frame has drifted tens of degrees off the view axis -- 1.1 px on an 18 px frame at 0.9%
            // zoom -- which is the gnomonic-against-stereographic difference this file documents,
            // showing up as SIZE rather than position because the frame is far off-axis. Asserting a
            // flat pixel there would be asserting the projections are the same, which they are not.
            if (ImageWidth * scale >= 100f)
            {
                WorstCornerErrorPx(wcs, originX, originY, scale).ShouldBeLessThan(1.0, $"scale {scale}");
            }
        }
    }

    /// <summary>
    /// The same trap one step further: a frame PANNED so the pane's centre is off it entirely. The old
    /// solver read the sky at a pixel the sensor never covered; this one never asks.
    /// </summary>
    [Fact]
    public void AFramePannedOffTheCentre_IsStillPlacedWhereItIsDrawn()
    {
        WorstCornerErrorPx(ChartOriented(), originX: 900f, originY: 700f, scale: 0.5f).ShouldBeLessThan(1.0);
    }

    [Fact]
    public void AFrameWithNoSolution_DrivesNothing()
    {
        var noSolution = new WCS(5.0, -25.0);

        SkyBackdropView.Solve(in noSolution, Pane, 100f, 75f, 0.5f).ShouldBeNull();
    }

    [Fact]
    public void ADegeneratePlacement_DrivesNothing()
    {
        var wcs = ChartOriented();

        SkyBackdropView.Solve(in wcs, Pane, 100f, 75f, scale: 0f).ShouldBeNull();
        SkyBackdropView.Solve(in wcs, new RectF32(0f, 0f, 0f, 0f), 100f, 75f, 0.5f).ShouldBeNull();
    }

    /// <summary>
    /// The roll servo is what walks a matched rotation back to celestial north over about a second,
    /// and a driven view has to be exempt from it or a rotated frame slowly comes unstuck from its
    /// own sky. Deliberately asserted through the same call the UBO writer makes every frame.
    /// </summary>
    [Fact]
    public void ADrivenView_KeepsTheRollItWasGiven()
    {
        var wcs = Rotated(30.0);
        var state = new SkyMapState { ViewDrivenExternally = true };
        SkyBackdropView.ApplyTo(state, SkyBackdropView.Solve(in wcs, Pane, 100f, 75f, 0.5f)!.Value);
        var driven = state.CenterRoll;

        for (var frame = 0; frame < 60; frame++)
        {
            state.UpdateRollForReference(deltaSeconds: 1.0 / 60.0).ShouldBeFalse();
        }

        state.CenterRoll.ShouldBe(driven);
    }
}
