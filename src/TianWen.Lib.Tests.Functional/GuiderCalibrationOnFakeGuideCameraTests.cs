using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The rig <c>NodeGuidingTests</c> calibrates, in process (#2050): the fake SkyWatcher mount at 6h +45 with the coupled fake
/// guide camera on a 130 mm scope, binned 2 at 100 ms, the middle candidate of the frame chosen as the Guider tab picks it,
/// and a calibration through the built-in guider on the fake clock. The node's tests hit this about one run in 80; here the
/// same calibration runs over several fresh rigs, and a failure names its cause (<see cref="GuiderCalibrationFailure"/>).
/// </summary>
public class GuiderCalibrationOnFakeGuideCameraTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Exposure = TimeSpan.FromMilliseconds(100);

    private async Task<(BuiltInGuiderDriver Guider, ICameraDriver Camera)> RigAsync(
        FakeTimeProviderWrapper clock, CancellationToken ct)
    {
        var external = new FakeExternal(output, clock);
        var sp = new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(external.TimeProvider)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(new XUnitLoggerProvider(output, false)))
            .AddSingleton<IDeviceHub, DeviceHub>()
            .BuildServiceProvider();
        var hub = sp.GetRequiredService<IDeviceHub>();

        var mountDevice = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            { "port", "SkyWatcher" },
            { "latitude", "48.2" },
            { "longitude", "16.3" },
        });
        var mount = (IMountDriver)await hub.ConnectAsync(mountDevice, ct);
        await mount.SetSiteLatitudeAsync(48.2, ct);
        await mount.SetSiteLongitudeAsync(16.3, ct);
        await mount.SetTrackingAsync(true, ct);
        await mount.SyncRaDecAsync(6.0, 45.0, ct);

        var camera = (ICameraDriver)await hub.ConnectAsync(new FakeDevice(new Uri("camera://FakeDevice/FakeGuideCam1#Fake Guide Cam")), ct);
        camera.FocalLength = 130;
        PlanetaryCapture.ConfigureRoi(camera, 0, 0, 2);

        var guider = new BuiltInGuiderDriver(new BuiltInGuiderDevice(new Uri("guider://BuiltInGuiderDevice/builtin?pulseGuideSource=Mount#Built-in Guider")), sp);
        guider.LinkDevices(mount, camera);

        return (guider, camera);
    }

    /// <summary>The pick the Guider tab's click and the node's tests make, as a list: every candidate the frame offers, in its own ranking.</summary>
    private async Task<((double X, double Y)[] Stars, int Width, int Height)> CandidatesAsync(CancellationToken ct)
    {
        var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero)) { ExternalTimePump = true };
        var (_, camera) = await RigAsync(clock, ct);
        var capture = Task.Run(async () => await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, Exposure, clock, TimeSpan.FromMilliseconds(10), ct), ct);
        // No progress probe here, deliberately: this is ONE exposure of 100 ms through the static capture, which publishes
        // no frame and has no counter of its own, so the minute of fake time bounds the run itself (600 times its length).
        await clock.PumpUntilCompletedAsync(capture, TimeSpan.FromMinutes(1), cancellationToken: ct);
        var frame = await capture;
        try
        {
            var plane = frame.GetChannel(0).Data;
            return ([.. new GuiderCentroidTracker(maxStars: 8).FindStars(plane)
                .Select(c => GuideStarReadout.Measure(plane, 1f, (c.X, c.Y)))
                .OfType<GuideStarMeasurement>()
                .Select(m => (m.X, m.Y))], plane.GetLength(1), plane.GetLength(0));
        }
        finally
        {
            frame.Release();
        }
    }

    /// <summary>
    /// The cause of the node tests' one failure in about 80 (#2050): the middle candidate is sometimes a star about 31 px
    /// from the top edge, and the 12 pulse Dec sweep (plus the backlash clearing before it) walked it where its aperture no
    /// longer fits the frame, so the tracker lost it ("the star was lost during the Dec (North) measurement sweep after 8 of
    /// 12 pulses"), and the retry then looked for it at the place it was last seen. The sweep now stops while the star can
    /// still be measured, and the rate is the displacement over the pulses it sent.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACalibrationOnTheStarNearestAnEdgeStopsItsSweepBeforeTheStarLeavesTheFrame()
    {
        var ct = TestContext.Current.CancellationToken;
        var (candidates, width, height) = await CandidatesAsync(ct);
        candidates.ShouldNotBeEmpty();
        var (x, y) = candidates.OrderBy(c => Math.Min(Math.Min(c.X, c.Y), Math.Min(width - c.X, height - c.Y))).First();
        output.WriteLine($"calibrating on the candidate nearest an edge: ({x:F1}, {y:F1}) in a {width} x {height} frame");

        var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero)) { ExternalTimePump = true };
        var (guider, _) = await RigAsync(clock, ct);
        // The cooperative pump (never a wall-clock loop of its own): it advances only once the calibration is parked at a
        // sleep, so the exposures and pulses see the same fake time the node's tests do.
        var calibration = Task.Run(async () => await guider.CalibrateAsync(new GuideStartOptions((x, y), Exposure), ct), ct);
        // The probe is the calibration's own progress: every frame it measures is published through LastGuideFrameNumber, so
        // the budget bounds a STALL of the calibration, not the whole run, and a slow runner only takes longer.
        await clock.PumpUntilCompletedAsync(calibration, TimeSpan.FromMinutes(30),
            progress: () => guider.LastGuideFrameNumber, cancellationToken: ct);
        var summary = await calibration;
        summary.ShouldNotBeNull($"calibration on ({x:F1}, {y:F1}) failed: {guider.LastCalibrationFailure}");
    }
}
