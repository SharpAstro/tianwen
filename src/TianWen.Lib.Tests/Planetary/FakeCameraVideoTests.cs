using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Phase B of the live planetary plan: the fake camera's <see cref="IVideoCameraDriver"/> path. A
/// drifting synthetic planet disk streams through video mode and JogRoiAsync pans the readout window --
/// the COM-recenter actuator the live tab + Phase C recenter loop drive. These pin the synthetic
/// renderer (deterministic, bright disk) and the fake video loop (frame shape, ROI jog pans the planet,
/// end-to-end into the rolling-window stacker) without any GUI / Vulkan.
/// </summary>
public class FakeCameraVideoTests(ITestOutputHelper output)
{
    private const float MaxAdu = 65535f;

    // ── SyntheticPlanetRenderer ──────────────────────────────────────────────

    [Fact]
    public void Renderer_is_deterministic_for_the_same_seed()
    {
        var a = SyntheticPlanetRenderer.Render(128, 128, 64, 64, radius: 40, blurSigma: 1.2, noiseSeed: 7);
        var b = SyntheticPlanetRenderer.Render(128, 128, 64, 64, radius: 40, blurSigma: 1.2, noiseSeed: 7);

        for (var y = 0; y < 128; y++)
        {
            for (var x = 0; x < 128; x++)
            {
                a[y, x].ShouldBe(b[y, x]);
            }
        }
    }

    [Fact]
    public void Renderer_makes_a_bright_disk_on_a_dark_sky()
    {
        var arr = SyntheticPlanetRenderer.Render(200, 200, 100, 100, radius: 60, blurSigma: 0.4, noiseSeed: 1);

        // Disk centre is far brighter than a sky corner.
        arr[100, 100].ShouldBeGreaterThan(arr[4, 4] * 10f);

        // The bright pixels localise to a disk whose centre of mass is the supplied centre.
        var img = Image.FromChannel(arr, MaxAdu, 0f);
        var (cx, cy) = PlanetaryDisk.CenterOfMass(img, PlanetaryDisk.BoundingBox(img));
        cx.ShouldBe(100.0, 4.0);
        cy.ShouldBe(100.0, 4.0);
    }

    [Fact]
    public void Sharper_frame_grades_higher_than_a_soft_frame()
    {
        // The lucky-imaging premise: a low-blur ("lucky") frame carries more high-frequency energy than a
        // soft one, so the quality estimator ranks it higher. Same geometry + seed isolates blur.
        var sharp = SyntheticPlanetRenderer.Render(200, 200, 100, 100, radius: 60, blurSigma: 0.4, noiseSeed: 3);
        var soft = SyntheticPlanetRenderer.Render(200, 200, 100, 100, radius: 60, blurSigma: 4.0, noiseSeed: 3);

        LaplacianVariance(sharp).ShouldBeGreaterThan(LaplacianVariance(soft));
    }

    // ── FakeCameraDriver video path ──────────────────────────────────────────

    [Fact]
    public async Task Capabilities_report_video_and_gate_roi_jog_on_an_active_stream()
    {
        var (camera, _) = await CreateCameraAsync();
        camera.CanVideoCapture.ShouldBeTrue();
        camera.DroppedFrames.ShouldBe(0);

        // No stream running yet -> ROI jog is unavailable and throws.
        camera.CanJogRoi.ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(async () => await camera.JogRoiAsync(8, 8));
    }

    [Fact]
    public async Task CaptureVideo_yields_mono_frames_of_the_configured_roi_size()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, _) = await CreateCameraAsync();
        camera.NumX = 320;
        camera.NumY = 256;

        var count = 0;
        await foreach (var frame in camera.CaptureVideoAsync(new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct))
        {
            frame.ChannelCount.ShouldBe(1);
            frame.Width.ShouldBe(320);
            frame.Height.ShouldBe(256);
            camera.CanJogRoi.ShouldBeTrue();   // ROI jog is available while streaming
            frame.Release();
            if (++count >= 5)
            {
                break;
            }
        }

        count.ShouldBe(5);
        camera.CanJogRoi.ShouldBeFalse();       // stream finished -> jog gated off again
    }

    [Fact]
    public async Task JogRoi_pans_the_readout_window_so_the_planet_shifts_the_other_way()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, _) = await CreateCameraAsync();
        camera.NumX = 400;
        camera.NumY = 400;
        camera.PlanetRadiusPixels = 60;

        // 2 ms exposure means the planet drifts a negligible fraction of a pixel between two adjacent
        // frames, so the only thing that moves the disk is the ROI jog.
        var opts = new VideoCaptureOptions(TimeSpan.FromMilliseconds(2));
        await using var e = camera.CaptureVideoAsync(opts, ct).GetAsyncEnumerator(ct);

        (await e.MoveNextAsync()).ShouldBeTrue();
        var (cx0, _) = PlanetaryDisk.CenterOfMass(e.Current, PlanetaryDisk.BoundingBox(e.Current));
        cx0.ShouldBe(200.0, 8.0);                // disk starts centred in the 400 px window

        // Pan the readout +80 px in X: the disk now sits 80 px to the LEFT in frame coordinates.
        await camera.JogRoiAsync(80, 0, ct);

        (await e.MoveNextAsync()).ShouldBeTrue();
        var (cx1, _) = PlanetaryDisk.CenterOfMass(e.Current, PlanetaryDisk.BoundingBox(e.Current));
        output.WriteLine($"COM x: before={cx0:F1} after jog={cx1:F1}");
        cx1.ShouldBeLessThan(cx0 - 60.0);        // moved left by ~80 px (allow COM tolerance)
    }

    [Fact]
    public async Task Live_video_feeds_the_rolling_window_stacker_end_to_end()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, _) = await CreateCameraAsync();
        const int roi = 360;
        camera.NumX = roi;
        camera.NumY = roi;
        camera.PlanetRadiusPixels = 55;

        using var stream = new LiveCameraFrameStream(roi, roi, PlanetaryFrameLayout.Mono);
        var pushed = 0;
        await foreach (var frame in camera.CaptureVideoAsync(new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct))
        {
            stream.Push(frame);
            frame.Release();
            if (++pushed >= 12)
            {
                break;
            }
        }

        stream.FrameCount.ShouldBe(12);

        var stacker = new RollingWindowStacker(stream, new RollingWindowOptions { FallbackWindowFrames = 8 });
        var master = await stacker.StackToAsync(stream.LatestIndex, ct);

        // The master is the coverage-normalised MEAN in the input ADU scale (PlanetaryMaster.NormalizeInPlace),
        // not [0,1]. The disk peaks near 0.55 * MaxADU; the sky sits near the ~300 ADU background.
        master.ChannelCount.ShouldBe(1);
        master.Width.ShouldBe(roi);
        var centre = master[0, roi / 2, roi / 2];
        var corner = master[0, 6, 6];
        output.WriteLine($"master centre={centre:F0} ADU, corner={corner:F0} ADU");
        centre.ShouldBeGreaterThan(5000f);     // stacked disk centre is bright
        corner.ShouldBeLessThan(2000f);        // sky corner stays dark
        centre.ShouldBeGreaterThan(corner * 10f);
    }

    private async Task<(FakeCameraDriver Camera, FakeExternal External)> CreateCameraAsync()
    {
        // IMX464M (id 8): compact mono planetary sensor, 2712 x 1538, MaxADU 65535.
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 24, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var device = new FakeDevice(DeviceType.Camera, 8);
        var camera = new FakeCameraDriver(device, external.BuildServiceProvider());
        await camera.ConnectAsync(TestContext.Current.CancellationToken);
        return (camera, external);
    }

    private static double LaplacianVariance(float[,] a)
    {
        int h = a.GetLength(0), w = a.GetLength(1);
        double sum = 0, sum2 = 0;
        long n = 0;
        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                var lap = (4f * a[y, x]) - a[y - 1, x] - a[y + 1, x] - a[y, x - 1] - a[y, x + 1];
                sum += lap;
                sum2 += lap * lap;
                n++;
            }
        }

        var mean = sum / n;
        return (sum2 / n) - (mean * mean);
    }
}
