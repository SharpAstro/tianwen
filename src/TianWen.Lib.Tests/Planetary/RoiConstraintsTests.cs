using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Phase 4c of the live planetary plan: the <see cref="RoiConstraints"/> snap/clamp math. The planetary
/// ROI picker offers a free rect; these pin that any chosen rect snaps to the camera's step/alignment and
/// stays fully on the sensor, so the UI never encodes a per-vendor rule. The fake reports ZWO-style 8 / 2.
/// </summary>
public class RoiConstraintsTests(ITestOutputHelper output)
{
    // ZWO-style: width % 8 == 0, height % 2 == 0, origin on the same steps, on a 1936x1096 sensor.
    private static readonly RoiConstraints Zwo = new(
        MaxWidth: 1936, MaxHeight: 1096, MinWidth: 16, MinHeight: 16,
        WidthStep: 8, HeightStep: 2, OriginStepX: 8, OriginStepY: 2);

    [Fact]
    public void ForSensor_is_a_free_step1_rect_over_the_full_sensor()
    {
        var c = RoiConstraints.ForSensor(640, 480);

        c.MaxWidth.ShouldBe(640);
        c.MaxHeight.ShouldBe(480);
        c.MinWidth.ShouldBe(1);
        c.WidthStep.ShouldBe(1);
        c.OriginStepX.ShouldBe(1);
        // Any rect is already legal -> snap is identity.
        c.Snap(new RoiRect(123, 45, 321, 99)).ShouldBe(new RoiRect(123, 45, 321, 99));
    }

    [Theory]
    [InlineData(645, 640)]   // rounds down to the nearest multiple of 8
    [InlineData(640, 640)]   // already aligned
    [InlineData(647, 640)]
    [InlineData(648, 648)]
    [InlineData(10, 16)]     // below min -> rounds the min UP to a multiple of 8 (16)
    [InlineData(5000, 1936)] // above max -> the largest in-range multiple (1936 % 8 == 0)
    public void SnapWidth_rounds_to_the_step_and_clamps(int input, int expected)
        => Zwo.SnapWidth(input).ShouldBe(expected);

    [Theory]
    [InlineData(321, 320)]   // odd -> nearest lower even
    [InlineData(320, 320)]
    [InlineData(10, 16)]     // below min (16)
    [InlineData(5000, 1096)] // above max (even)
    public void SnapHeight_rounds_to_the_step_and_clamps(int input, int expected)
        => Zwo.SnapHeight(input).ShouldBe(expected);

    [Fact]
    public void Snap_aligns_origin_to_its_step()
    {
        // Origin 101,51 with a legal 640x320 window snaps each coord DOWN to a multiple of 8 / 2.
        var r = Zwo.Snap(new RoiRect(101, 51, 640, 320));

        r.Width.ShouldBe(640);
        r.Height.ShouldBe(320);
        (r.X % 8).ShouldBe(0);
        (r.Y % 2).ShouldBe(0);
        r.X.ShouldBe(96);
        r.Y.ShouldBe(50);
    }

    [Fact]
    public void Snap_clamps_origin_so_the_window_stays_on_the_sensor()
    {
        // A 640x320 window pushed past the right/bottom edge clamps so Right/Bottom stay within the sensor.
        var r = Zwo.Snap(new RoiRect(X: 1900, Y: 1080, Width: 640, Height: 320));

        r.Width.ShouldBe(640);
        r.Height.ShouldBe(320);
        r.Right.ShouldBeLessThanOrEqualTo(Zwo.MaxWidth);
        r.Bottom.ShouldBeLessThanOrEqualTo(Zwo.MaxHeight);
        (r.X % 8).ShouldBe(0);
        (r.Y % 2).ShouldBe(0);
    }

    [Fact]
    public void Snap_negative_origin_clamps_to_zero()
    {
        var r = Zwo.Snap(new RoiRect(X: -50, Y: -10, Width: 320, Height: 240));

        r.X.ShouldBe(0);
        r.Y.ShouldBe(0);
    }

    [Fact]
    public void RoiRect_Centered_centres_the_window()
    {
        var r = RoiRect.Centered(1936, 1096, 640, 320);

        r.X.ShouldBe((1936 - 640) / 2);
        r.Y.ShouldBe((1096 - 320) / 2);
        r.Width.ShouldBe(640);
        r.Height.ShouldBe(320);
    }

    [Fact]
    public void Fake_camera_reports_zwo_style_constraints()
    {
        var external = new FakeExternal(output);
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 8), external.BuildServiceProvider());

        var c = camera.RoiConstraints;

        c.WidthStep.ShouldBe(8);
        c.HeightStep.ShouldBe(2);
        c.MaxWidth.ShouldBe(camera.CameraXSize);
        c.MaxHeight.ShouldBe(camera.CameraYSize);
    }
}
