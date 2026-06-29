using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Phase C of the live planetary plan: the pure COM recenter decision (<see cref="PlanetaryRecenterController.Decide"/>).
/// Disk centre of mass -> per-axis offset -> deadband -> ROI jog (auto, while the window has pan range) or a
/// coarse mount nudge (opt-in, when the ROI is edge-blocked / the camera can't jog). No hardware, no driver.
/// </summary>
public class PlanetaryRecenterControllerTests
{
    // A 320x256 frame on a roomy 2000x2000 sensor with the ROI mid-sensor (so neither axis is edge-blocked).
    private const int FrameW = 320;
    private const int FrameH = 256;
    private const int SensorW = 2000;
    private const int SensorH = 2000;
    private static readonly RoiRect MidRoi = new((SensorW - FrameW) / 2, (SensorH - FrameH) / 2, FrameW, FrameH);

    private static (double X, double Y) Centre => (FrameW / 2.0, FrameH / 2.0);

    [Fact]
    public void Centred_disk_holds_position()
    {
        var d = PlanetaryRecenterController.Decide(
            Centre, FrameW, FrameH, MidRoi, SensorW, SensorH, canJogRoi: true, new RecenterOptions());

        d.Actuator.ShouldBe(RecenterActuator.None);
        d.RoiDx.ShouldBe(0);
        d.RoiDy.ShouldBe(0);
        d.MountRaArcsec.ShouldBe(0.0);
        d.MountDecArcsec.ShouldBe(0.0);
        d.OffsetX.ShouldBe(0.0, 1e-9);
        d.OffsetY.ShouldBe(0.0, 1e-9);
    }

    [Fact]
    public void Offset_within_deadband_holds_position()
    {
        // offset (2, 0) px, deadband 4 -> inside -> hold (but the offset is still reported for telemetry).
        var com = (Centre.X + 2.0, Centre.Y);
        var d = PlanetaryRecenterController.Decide(
            com, FrameW, FrameH, MidRoi, SensorW, SensorH, canJogRoi: true, new RecenterOptions(DeadbandPixels: 4));

        d.Actuator.ShouldBe(RecenterActuator.None);
        d.RoiDx.ShouldBe(0);
        d.OffsetX.ShouldBe(2.0, 1e-9);
    }

    [Fact]
    public void Disk_right_of_centre_jogs_the_roi_window_right()
    {
        // Disk 60 px right of centre, gain 0.5 -> jog the window +30 px in X (which pulls the disk back left).
        var com = (Centre.X + 60.0, Centre.Y);
        var d = PlanetaryRecenterController.Decide(
            com, FrameW, FrameH, MidRoi, SensorW, SensorH, canJogRoi: true,
            new RecenterOptions(DeadbandPixels: 4, Gain: 0.5));

        d.Actuator.ShouldBe(RecenterActuator.Roi);
        d.RoiDx.ShouldBe(30);
        d.RoiDy.ShouldBe(0);
        d.MountRaArcsec.ShouldBe(0.0);
        d.OffsetX.ShouldBe(60.0, 1e-9);
    }

    [Fact]
    public void Disk_below_centre_jogs_the_roi_window_down()
    {
        var com = (Centre.X, Centre.Y + 40.0);
        var d = PlanetaryRecenterController.Decide(
            com, FrameW, FrameH, MidRoi, SensorW, SensorH, canJogRoi: true,
            new RecenterOptions(DeadbandPixels: 4, Gain: 0.5));

        d.Actuator.ShouldBe(RecenterActuator.Roi);
        d.RoiDx.ShouldBe(0);
        d.RoiDy.ShouldBe(20);
    }

    [Fact]
    public void Single_jog_is_clamped_to_the_max_step()
    {
        // A huge offset (would-be jog 500 px) is clamped to MaxRoiStepPixels so one bad COM can't fling the window.
        var com = (Centre.X + 1000.0, Centre.Y);
        var d = PlanetaryRecenterController.Decide(
            com, FrameW, FrameH, MidRoi, SensorW, SensorH, canJogRoi: true,
            new RecenterOptions(Gain: 0.5, MaxRoiStepPixels: 80));

        d.RoiDx.ShouldBe(80);
    }

    [Fact]
    public void Roi_at_right_edge_with_mount_enabled_hands_off_to_the_mount()
    {
        // Window at the right edge of its pan range: an X correction can't jog further, so (mount jog on +
        // a known pixel scale) it nudges the mount instead. 1 arcsec/px -> 60 px offset -> 60 arcsec East.
        var maxStartX = SensorW - FrameW;
        var edgeRoi = new RoiRect(maxStartX, (SensorH - FrameH) / 2, FrameW, FrameH);
        var com = (Centre.X + 60.0, Centre.Y);

        var d = PlanetaryRecenterController.Decide(
            com, FrameW, FrameH, edgeRoi, SensorW, SensorH, canJogRoi: true,
            new RecenterOptions(Gain: 0.5, MountJogEnabled: true, PixelScaleArcsec: 1.0, MaxMountArcsec: 200));

        d.Actuator.ShouldBe(RecenterActuator.Mount);
        d.RoiDx.ShouldBe(0);                 // ROI is saturated on this axis
        d.MountRaArcsec.ShouldBe(60.0, 1e-9);// + = East
        d.MountDecArcsec.ShouldBe(0.0);
    }

    [Fact]
    public void Roi_at_edge_with_mount_disabled_does_nothing()
    {
        // Same saturated geometry, but mount jog is off -> nothing actionable (the panel prompts the user to
        // enable mount jog). The offset is still reported.
        var maxStartX = SensorW - FrameW;
        var edgeRoi = new RoiRect(maxStartX, (SensorH - FrameH) / 2, FrameW, FrameH);
        var com = (Centre.X + 60.0, Centre.Y);

        var d = PlanetaryRecenterController.Decide(
            com, FrameW, FrameH, edgeRoi, SensorW, SensorH, canJogRoi: true,
            new RecenterOptions(Gain: 0.5, MountJogEnabled: false));

        d.Actuator.ShouldBe(RecenterActuator.None);
        d.RoiDx.ShouldBe(0);
        d.MountRaArcsec.ShouldBe(0.0);
        d.OffsetX.ShouldBe(60.0, 1e-9);
    }

    [Fact]
    public void No_roi_jog_capability_uses_the_mount_for_both_axes()
    {
        // Rapid-exposure fallback (no IVideoCameraDriver): CanJogRoi is false, so both axes go to the mount.
        var com = (Centre.X + 60.0, Centre.Y + 40.0);
        var d = PlanetaryRecenterController.Decide(
            com, FrameW, FrameH, MidRoi, SensorW, SensorH, canJogRoi: false,
            new RecenterOptions(MountJogEnabled: true, PixelScaleArcsec: 2.0, MaxMountArcsec: 500));

        d.Actuator.ShouldBe(RecenterActuator.Mount);
        d.RoiDx.ShouldBe(0);
        d.RoiDy.ShouldBe(0);
        d.MountRaArcsec.ShouldBe(120.0, 1e-9);  // 60 px * 2 arcsec/px, + = East
        d.MountDecArcsec.ShouldBe(80.0, 1e-9);  // 40 px * 2 arcsec/px, + = North
    }

    [Fact]
    public void Flip_flags_invert_the_mount_direction()
    {
        var com = (Centre.X + 60.0, Centre.Y + 40.0);
        var d = PlanetaryRecenterController.Decide(
            com, FrameW, FrameH, MidRoi, SensorW, SensorH, canJogRoi: false,
            new RecenterOptions(MountJogEnabled: true, PixelScaleArcsec: 2.0, MaxMountArcsec: 500,
                FlipRa: true, FlipDec: true));

        d.MountRaArcsec.ShouldBe(-120.0, 1e-9); // - = West
        d.MountDecArcsec.ShouldBe(-80.0, 1e-9); // - = South
    }

    [Fact]
    public void Mount_nudge_is_capped()
    {
        // A full half-frame offset would ask for a large move; the per-axis cap bounds it (a wrong uncalibrated
        // sign then only mis-moves the field by the cap, never a runaway).
        var com = (Centre.X + 1000.0, Centre.Y);
        var d = PlanetaryRecenterController.Decide(
            com, FrameW, FrameH, MidRoi, SensorW, SensorH, canJogRoi: false,
            new RecenterOptions(MountJogEnabled: true, PixelScaleArcsec: 1.0, MaxMountArcsec: 60));

        d.MountRaArcsec.ShouldBe(60.0, 1e-9);
    }

    [Fact]
    public void Mount_path_is_disabled_without_a_pixel_scale()
    {
        // Mount jog on but no usable pixel scale (NaN) -> can't size a pulse -> mount stays silent.
        var com = (Centre.X + 60.0, Centre.Y);
        var d = PlanetaryRecenterController.Decide(
            com, FrameW, FrameH, MidRoi, SensorW, SensorH, canJogRoi: false,
            new RecenterOptions(MountJogEnabled: true, PixelScaleArcsec: double.NaN));

        d.MountRaArcsec.ShouldBe(0.0);
        d.Actuator.ShouldBe(RecenterActuator.None);
    }
}
