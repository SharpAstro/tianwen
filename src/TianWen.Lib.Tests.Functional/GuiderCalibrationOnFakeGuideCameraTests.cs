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

    private async Task<(BuiltInGuiderDriver Guider, ICameraDriver Camera, Func<CancellationToken, Task<(double X, double Y)>> PickAStar)> RigAsync(
        FakeTimeProviderWrapper clock, CancellationToken ct)
    {
        var external = new FakeExternal(output, clock);
        var sp = new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(external.TimeProvider)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Information))
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

        async Task<(double X, double Y)> PickAStar(CancellationToken token)
        {
            var frame = await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, Exposure, clock, sp.GetRequiredService<IExternal>().ImageReadyPollInterval, token);
            try
            {
                var plane = frame.GetChannel(0).Data;
                var measured = new GuiderCentroidTracker(maxStars: 8).FindStars(plane)
                    .Select(c => GuideStarReadout.Measure(plane, 1f, (c.X, c.Y)))
                    .OfType<GuideStarMeasurement>()
                    .ToList();
                measured.ShouldNotBeEmpty("the guide frame holds a star to choose");
                output.WriteLine($"{measured.Count} candidates: " + string.Join(", ", measured.Select(m => $"({m.X:F1}, {m.Y:F1})")));
                var star = measured[measured.Count / 2];
                return (star.X, star.Y);
            }
            finally
            {
                frame.Release();
            }
        }

        return (guider, camera, PickAStar);
    }

    /// <summary>The pick the Guider tab's click and the node's tests make, as a list: every candidate the frame offers, in its own ranking.</summary>
    private async Task<(double X, double Y)[]> CandidatesAsync(CancellationToken ct)
    {
        var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero)) { ExternalTimePump = true };
        await using var pumped = new Pump(clock);
        var (_, camera, _) = await RigAsync(clock, ct);
        var frame = await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, Exposure, clock, TimeSpan.FromMilliseconds(10), ct);
        try
        {
            var plane = frame.GetChannel(0).Data;
            return [.. new GuiderCentroidTracker(maxStars: 8).FindStars(plane)
                .Select(c => GuideStarReadout.Measure(plane, 1f, (c.X, c.Y)))
                .OfType<GuideStarMeasurement>()
                .Select(m => (m.X, m.Y))];
        }
        finally
        {
            frame.Release();
        }
    }

    private sealed class Pump : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Task _task;

        public Pump(FakeTimeProviderWrapper clock)
            => _task = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        clock.Advance(TimeSpan.FromMilliseconds(100));
                        await Task.Delay(1, _stop.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _task;
            _stop.Dispose();
        }
    }

    [Fact(Timeout = 600_000)]
    public async Task CalibratingOnEveryCandidateOfTheFakeGuideCameraFrameSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var candidates = await CandidatesAsync(ct);
        candidates.ShouldNotBeEmpty();
        var failures = new System.Collections.Generic.List<string>();
        foreach (var star in candidates)
        {
            var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero)) { ExternalTimePump = true };
            await using var pumped = new Pump(clock);
            var (guider, _, _) = await RigAsync(clock, ct);
            var summary = await guider.CalibrateAsync(new GuideStartOptions(star, Exposure), ct);
            var line = $"({star.X:F1}, {star.Y:F1}): " + (summary is null ? $"FAILED {guider.LastCalibrationFailure}" : "ok");
            output.WriteLine(line);
            if (summary is null)
            {
                failures.Add(line);
            }
        }
        failures.ShouldBeEmpty($"{failures.Count} of {candidates.Length} candidates failed to calibrate");
    }
}
