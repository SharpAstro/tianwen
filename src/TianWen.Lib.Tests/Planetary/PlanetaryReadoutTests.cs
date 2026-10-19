using System;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What a planetary capture's readouts say (#1619): the CURRENT frame rate, never the run's average, which blended every exposure
/// the run had; the settings the stream runs at as applied, never as staged; both on the wire.
/// </summary>
[Collection("Session")]
public class PlanetaryReadoutTests(ITestOutputHelper output)
{
    private static long Ms(double ms) => (long)(ms * TimeSpan.TicksPerMillisecond);

    [Fact]
    public void ARateWindowReadsTheLastSecondNotTheWholeRun()
    {
        var window = new FrameRateWindow();
        var t = 0L;
        double rate = 0;
        // Five seconds at 100 frames a second (10 ms), then two seconds at 200 (5 ms).
        for (var i = 0; i < 500; i++, t += Ms(10))
        {
            rate = window.Note(t);
        }
        rate.ShouldBe(100, 0.5);
        for (var i = 0; i < 400; i++, t += Ms(5))
        {
            rate = window.Note(t);
        }

        rate.ShouldBe(200, 1, "a second after the change it reads the new exposure's rate, where the run's average reads about 129");
    }

    [Fact]
    public void ASlowExposuresRateIsReadOverItsLastFiveFrames()
    {
        var window = new FrameRateWindow();
        double rate = 0;
        for (var i = 0; i < 20; i++)
        {
            rate = window.Note(Ms(2000) * i);
        }

        rate.ShouldBe(0.5, 1e-9, "a frame every two seconds: a second-long window would hold one frame and read nothing");
    }

    [Fact]
    public void BeforeTwoFramesThereIsNoRate()
    {
        var window = new FrameRateWindow();
        window.Note(Ms(3)).ShouldBe(0);
        window.Note(Ms(13)).ShouldBe(100, 1e-9);
        window.Reset();
        window.Note(Ms(50)).ShouldBe(0, "a new capture forgets the last one's frames");
    }

    [Fact(Timeout = 30_000)]
    public async System.Threading.Tasks.Task TheAppliedSettingsFollowALiveChangeAndAnInterludeNotTheStart()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 10, 7, 11, 30, 0, TimeSpan.Zero));
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 8), new FakeExternal(output, timeProvider).BuildServiceProvider());
        await camera.ConnectAsync(ct);
        (camera.NumX, camera.NumY, camera.PlanetRadiusPixels) = (128, 128, 40);
        await using var capture = new PlanetaryCapture(new FakeTimeProviderWrapper(), NullLogger.Instance,
            new RollingWindowOptions { FallbackWindowFrames = 10, MaxWindowFrames = 16 });
        capture.ArmFrameGate();
        capture.AppliedOptions.ShouldBeNull("nothing before a start");
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(10), Gain: 50), ct).ShouldBeTrue();

        async System.Threading.Tasks.Task StepAsync(int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                var next = capture.WaitForNextFrameAsync(ct);
                capture.StepFrame();
                await next;
            }
        }

        await StepAsync(2);
        capture.AppliedOptions.ShouldNotBeNull().Exposure.ShouldBe(TimeSpan.FromMilliseconds(10));

        capture.SetExposure(TimeSpan.FromMilliseconds(5));
        capture.SetGain(110);
        capture.AppliedOptions.ShouldNotBeNull().Exposure.ShouldBe(TimeSpan.FromMilliseconds(10), "a staged change is not applied yet");
        await StepAsync(1);
        var applied = capture.AppliedOptions.ShouldNotBeNull();
        (applied.Exposure, applied.Gain).ShouldBe((TimeSpan.FromMilliseconds(5), (short?)110));

        var interlude = capture.InterludeAsync(new PlanetaryInterlude("a test", _ => System.Threading.Tasks.ValueTask.CompletedTask)
        {
            RestartAt = had => had with { Options = had.Options with { Exposure = TimeSpan.FromMilliseconds(4) } },
        }, ct);
        for (var i = 0; i < 20 && !interlude.IsCompleted; i++)
        {
            await StepAsync(1);
        }
        (await interlude).ShouldBeNull();
        // The rate itself is FrameRateWindow's tests' and the node's (a real clock): this capture's fake clock stands still between frames.
        capture.AppliedOptions.ShouldNotBeNull().Exposure.ShouldBe(TimeSpan.FromMilliseconds(4), "a step's interlude restarts the stream at its own");
    }

    [Fact]
    public void TheSettingsAreWordedFromWhatTheNodeApplied()
    {
        var state = new PlanetaryStateDto { Camera = "G3M678M", RoiWidth = 640, RoiHeight = 360, ExposureMs = 5, Gain = 810 };

        PlanetaryCaptureController.SettingsInWords(state).ShouldBe("640x360, 5 ms, gain 810");
        PlanetaryCaptureController.SettingsInWords(new PlanetaryStateDto { Camera = "G3M678M", RoiWidth = 640, RoiHeight = 360 })
            .ShouldBeNull("nothing applied yet, nothing said");
    }

    [Fact]
    public void TheCurrentRateAndTheAppliedSettingsCrossTheWire()
    {
        var state = new PlanetaryStateDto { Camera = "G3M678M", FramesPerSecond = 129.4, CurrentFramesPerSecond = 199.6, ExposureMs = 5, Gain = 810 };

        var read = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, HostingJsonContext.Default.PlanetaryStateDto),
            HostingJsonContext.Default.PlanetaryStateDto).ShouldNotBeNull();

        (read.FramesPerSecond, read.CurrentFramesPerSecond, read.ExposureMs, read.Gain).ShouldBe((129.4, 199.6, 5.0, (short?)810));
    }
}
