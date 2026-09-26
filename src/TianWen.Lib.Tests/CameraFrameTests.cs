using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A camera's binning and frame set together (<see cref="CameraFrameExtensions.SetFrame"/>, P2 part 3 of
/// docs/plans/hardware-in-the-server.md, #929), against the fake camera, which reports ZWO's steps (a width in eights, a
/// height in twos, the origin on the same) and, as ZWO's own constraints do, the UNBINNED sensor as its largest frame.
/// </summary>
public class CameraFrameTests(ITestOutputHelper output)
{
    private async Task<ICameraDriver> ConnectAsync()
    {
        var hub = new FakeExternal(output).BuildServiceProvider().GetRequiredService<IDeviceHub>();
        return (ICameraDriver)await hub.ConnectAsync(new FakeDevice(DeviceType.Camera, 1), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NoFrameReadsOutTheWholeBinnedSensor(int bin)
    {
        var camera = await ConnectAsync();

        var set = camera.SetFrame(bin, frame: null);

        // The constraints name the unbinned sensor as the largest frame; binned, the sensor is smaller, and the frame
        // has to be too, or the setter refuses it.
        set.ShouldBe(new RoiRect(0, 0, camera.CameraXSize / bin / 8 * 8, camera.CameraYSize / bin / 2 * 2));
        (camera.BinX, camera.BinY).ShouldBe((bin, bin));
        (camera.StartX, camera.StartY, camera.NumX, camera.NumY).ShouldBe((set.X, set.Y, set.Width, set.Height));
    }

    // The fake refused both of these until this change: an origin was checked against the pixel pitch in microns, and the
    // whole sensor against a strict bound. DAL's setters had been fixed; the fake's had not.
    [Fact]
    public async Task TheWholeSensorAndAnOriginPastTheCornerAreBothFrames()
    {
        var camera = await ConnectAsync();
        camera.BinX = 1;

        Should.NotThrow(() => camera.NumX = camera.CameraXSize);
        Should.NotThrow(() => camera.NumY = camera.CameraYSize);
        Should.NotThrow(() => camera.StartX = 64);
        Should.Throw<ArgumentOutOfRangeException>(() => camera.StartX = camera.CameraXSize);
    }

    [Fact]
    public async Task AFrameIsSnappedToTheCamerasStepsAndKeptOnTheSensor()
    {
        var camera = await ConnectAsync();

        camera.SetFrame(1, new RoiRect(13, 7, 101, 51)).ShouldBe(new RoiRect(8, 6, 96, 50));

        var overTheEdge = camera.SetFrame(1, new RoiRect(camera.CameraXSize - 10, 0, 200, 100));
        overTheEdge.Right.ShouldBeLessThanOrEqualTo(camera.CameraXSize, "the window stays on the sensor");
        overTheEdge.Width.ShouldBe(200);
    }

    // The camera's constraints name the UNBINNED sensor at any binning, so only the binned sensor's own extent keeps a
    // frame asked for in unbinned terms, or one near the binned edge, on the sensor at bin 2.
    [Fact]
    public async Task ABinnedFrameIsKeptOnTheBinnedSensor()
    {
        var camera = await ConnectAsync();
        var (binnedWidth, binnedHeight) = (camera.CameraXSize / 2, camera.CameraYSize / 2);

        var whole = camera.SetFrame(2, new RoiRect(0, 0, camera.CameraXSize, camera.CameraYSize));
        whole.ShouldBe(new RoiRect(0, 0, binnedWidth / 8 * 8, binnedHeight / 2 * 2), "the whole unbinned sensor, binned, is the whole binned sensor");

        var nearTheEdge = camera.SetFrame(2, new RoiRect(binnedWidth - 10, binnedHeight - 10, 96, 50));
        nearTheEdge.Right.ShouldBeLessThanOrEqualTo(binnedWidth);
        nearTheEdge.Bottom.ShouldBeLessThanOrEqualTo(binnedHeight);
        nearTheEdge.Width.ShouldBe(96);
    }

    [Fact]
    public async Task ACameraIsNotBinnedPastItsMost()
    {
        var camera = await ConnectAsync();

        Should.Throw<ArgumentOutOfRangeException>(() => camera.SetFrame(camera.MaxBinX + 1, frame: null));
        Should.Throw<ArgumentOutOfRangeException>(() => camera.SetFrame(0, frame: null));
    }
}
