using FC.SDK.Canon;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Canon;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Phase E of the live planetary plan: the three pure decisions behind the Canon EVF magnified ROI, which is
/// the DSLR planetary regime. A requested window size picks one of the body's discrete zoom levels
/// (<see cref="CanonCameraDriver.ZoomForWindow"/>); the crop the body reports back becomes the live ROI
/// (<see cref="CanonCameraDriver.WindowFor"/>); and a pan is clamped into the range the body will act on
/// (<see cref="CanonCameraDriver.ClampPan"/>). No camera, no USB.
///
/// <para>The numbers here are the ones measured on an EOS 6D: a 5472x3648 sensor whose nominal 5x is a
/// (2184,1456) 1104x736 crop, a 4.96x magnification. Everything that could silently do nothing on real
/// hardware is pinned, because every failure mode on this path is silent: the zoom operation ACKs whether or
/// not it magnified, and a pan coordinate past the accepted range is discarded rather than refused.</para>
/// </summary>
public class CanonEvfZoomTests
{
    private const int SensorW = 5472;
    private const int SensorH = 3648;

    /// <summary>The 6D's measured 5x crop: centred, 1104x736, which is 4.96x rather than an exact fifth.</summary>
    private static readonly CanonEvfZoomRect Magnified5x = new(2184, 1456, 1104, 736, SensorW, SensorH);

    /// <summary>1x: the crop is the whole frame, so there is nothing to magnify and nowhere to pan.</summary>
    private static readonly CanonEvfZoomRect FullFrame = new(0, 0, SensorW, SensorH, SensorW, SensorH);

    // ── ZoomForWindow: a requested size snaps to a level the body actually offers ──────────────────

    [Fact]
    public void An_unset_window_asks_for_the_full_frame()
    {
        // NumX defaults to 0 before anything sets it, and that must mean "no magnification" rather than
        // dividing by zero into the deepest crop.
        CanonCameraDriver.ZoomForWindow(0, SensorW).ShouldBe(CanonEvfZoom.Fit);
    }

    [Theory]
    [InlineData(SensorW)]      // exactly the sensor
    [InlineData(SensorW + 64)] // more than the sensor, which a caller may well ask for
    public void A_window_at_or_past_the_sensor_asks_for_the_full_frame(int requested)
        => CanonCameraDriver.ZoomForWindow(requested, SensorW).ShouldBe(CanonEvfZoom.Fit);

    [Fact]
    public void A_fifth_of_the_sensor_asks_for_5x()
        => CanonCameraDriver.ZoomForWindow(SensorW / 5, SensorW).ShouldBe(CanonEvfZoom.X5);

    [Fact]
    public void The_crop_5x_actually_produces_asks_for_5x_again()
    {
        // The round trip that matters: feed back the width the body reported for its own 5x and the same
        // level comes out. A threshold placed on the nominal factor rather than between them would answer
        // Fit here, because 4.96 is not 5, and the stream would drop out of magnification on the first
        // resize check after it zoomed.
        CanonCameraDriver.ZoomForWindow((int)Magnified5x.Width, SensorW).ShouldBe(CanonEvfZoom.X5);
    }

    [Fact]
    public void A_tenth_of_the_sensor_asks_for_10x()
        => CanonCameraDriver.ZoomForWindow(SensorW / 10, SensorW).ShouldBe(CanonEvfZoom.X10);

    [Fact]
    public void A_half_frame_window_is_not_worth_magnifying()
    {
        // There is no 2x, so a request between the levels has to land somewhere; below the midpoint it stays
        // at full frame rather than jumping to a 5x crop a quarter the size that was asked for.
        CanonCameraDriver.ZoomForWindow(SensorW / 2, SensorW).ShouldBe(CanonEvfZoom.Fit);
    }

    [Fact]
    public void A_zero_sensor_width_asks_for_the_full_frame()
    {
        // Sensor size is populated from a table or the first decoded image, so it can still be 0 here.
        CanonCameraDriver.ZoomForWindow(1104, 0).ShouldBe(CanonEvfZoom.Fit);
    }

    // ── WindowFor: the reported crop becomes the ROI, and only a placeable one is pannable ─────────

    [Fact]
    public void A_magnified_crop_on_a_pannable_body_is_the_roi_and_can_be_panned()
    {
        var window = CanonCameraDriver.WindowFor(Magnified5x, bodySupportsPan: true, SensorW, SensorH);

        window.Roi.ShouldBe(new RoiRect(2184, 1456, 1104, 736));
        window.SensorWidth.ShouldBe(SensorW);
        window.SensorHeight.ShouldBe(SensorH);
        window.CanPan.ShouldBeTrue();
        // The pan range the recenter loop has to work with, which is what makes this worth reporting at all.
        (window.SensorWidth - window.Roi.Width).ShouldBe(4368);
        (window.SensorHeight - window.Roi.Height).ShouldBe(2912);
    }

    [Fact]
    public void A_body_that_cannot_pan_reports_a_crop_that_cannot_be_panned()
    {
        // The crop is still the right ROI to report; only the actuator is missing.
        var window = CanonCameraDriver.WindowFor(Magnified5x, bodySupportsPan: false, SensorW, SensorH);

        window.Roi.ShouldBe(new RoiRect(2184, 1456, 1104, 736));
        window.CanPan.ShouldBeFalse();
    }

    [Fact]
    public void At_1x_the_crop_is_the_whole_frame_and_cannot_be_panned()
    {
        var window = CanonCameraDriver.WindowFor(FullFrame, bodySupportsPan: true, SensorW, SensorH);

        window.Roi.ShouldBe(new RoiRect(0, 0, SensorW, SensorH));
        window.CanPan.ShouldBeFalse();
    }

    [Fact]
    public void No_reported_rect_falls_back_to_a_full_frame_that_cannot_be_panned()
    {
        var window = CanonCameraDriver.WindowFor(null, bodySupportsPan: true, 640, 480);

        window.Roi.ShouldBe(new RoiRect(0, 0, 640, 480));
        window.SensorWidth.ShouldBe(640);
        window.SensorHeight.ShouldBe(480);
        window.CanPan.ShouldBeFalse();
    }

    [Fact]
    public void A_crop_with_no_sensor_bounds_cannot_be_panned()
    {
        // The body sent the crop record but not the sensor-size one, so the crop cannot be placed: there is
        // no way to say whether it is magnified or how far it may travel. Trusting it half way would let the
        // recenter loop jog a window against a range it invented, which is the one outcome to rule out.
        var window = CanonCameraDriver.WindowFor(
            new CanonEvfZoomRect(2184, 1456, 1104, 736, 0, 0), bodySupportsPan: true, SensorW, SensorH);

        window.Roi.ShouldBe(new RoiRect(0, 0, SensorW, SensorH));
        window.CanPan.ShouldBeFalse();
    }

    [Fact]
    public void A_zero_sized_crop_cannot_be_panned()
    {
        var window = CanonCameraDriver.WindowFor(
            new CanonEvfZoomRect(0, 0, 0, 0, SensorW, SensorH), bodySupportsPan: true, SensorW, SensorH);

        window.Roi.ShouldBe(new RoiRect(0, 0, SensorW, SensorH));
        window.CanPan.ShouldBeFalse();
    }

    // ── ClampPan: a coordinate past the range is discarded by the body, so never send one ──────────

    [Fact]
    public void A_pan_inside_the_range_lands_exactly_where_it_was_asked_to()
    {
        var window = CanonCameraDriver.WindowFor(Magnified5x, bodySupportsPan: true, SensorW, SensorH);

        CanonCameraDriver.ClampPan(window, 100, -200).ShouldBe((2284, 1256));
    }

    [Fact]
    public void A_pan_past_the_far_edge_is_clamped_to_the_far_edge_rather_than_discarded()
    {
        // Asking for "as far right as possible" with a big number is exactly the case the body silently
        // ignores, so the far corner is computed here: sensor minus crop, per axis.
        var window = CanonCameraDriver.WindowFor(Magnified5x, bodySupportsPan: true, SensorW, SensorH);

        CanonCameraDriver.ClampPan(window, 999999, 999999).ShouldBe((4368, 2912));
    }

    [Fact]
    public void A_pan_past_the_near_edge_is_clamped_to_the_origin()
    {
        var window = CanonCameraDriver.WindowFor(Magnified5x, bodySupportsPan: true, SensorW, SensorH);

        CanonCameraDriver.ClampPan(window, -999999, -999999).ShouldBe((0, 0));
    }

    [Fact]
    public void Each_axis_clamps_on_its_own()
    {
        // A big offset on one axis must not drag the other one, which is the same per-axis rule the recenter
        // controller applies to its deadband.
        var window = CanonCameraDriver.WindowFor(Magnified5x, bodySupportsPan: true, SensorW, SensorH);

        CanonCameraDriver.ClampPan(window, 999999, 8).ShouldBe((4368, 1464));
    }

    [Fact]
    public void At_1x_the_range_collapses_to_a_point()
    {
        // The crop is the whole sensor, so [0, sensor - crop] is [0, 0] and there is nowhere to go. CanPan is
        // already false here; this pins that even a stray call cannot move the window off the origin.
        var window = CanonCameraDriver.WindowFor(FullFrame, bodySupportsPan: true, SensorW, SensorH);

        CanonCameraDriver.ClampPan(window, 500, 500).ShouldBe((0, 0));
        CanonCameraDriver.ClampPan(window, -500, -500).ShouldBe((0, 0));
    }
}
