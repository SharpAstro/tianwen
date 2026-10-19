using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The Planetary mode's readouts through the GUI's real signal handler over a real node (#1619): a capture started at 10 ms and
/// set to 5 ms says 5 ms on its status line, worded from what the node applied, where it kept the start's settings for ever,
/// and its state carries the applied exposure and a current rate.
/// </summary>
[Collection("NodeProcesses")]
public class PlanetaryReadoutsThroughTheNodeTests(ITestOutputHelper output)
{
    [Fact(Timeout = 120_000)]
    public async Task AnExposureSetWhileCapturingIsWhatTheStatusLineAndTheStateSay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        h.Post(new StartVideoCaptureSignal(ExposureMs: 10, RoiWidth: 320, RoiHeight: 200));
        await h.UntilSettledAsync(ct);
        var planetary = h.Planetary;
        await h.UntilAsync(() => planetary.Capture is { AppliedExposureMs: 10, CurrentFps: > 0 }, ct);

        planetary.Capture.SetExposure(TimeSpan.FromMilliseconds(5));
        await h.UntilAsync(() => planetary.Capture.AppliedExposureMs is 5, ct);
        await h.UntilAsync(() => h.AppState.Notifications.Any(static n => n.Message.StartsWith("Planetary capture at", StringComparison.Ordinal)), ct);

        var said = h.AppState.Notifications.Last(static n => n.Message.StartsWith("Planetary capture at", StringComparison.Ordinal)).Message;
        output.WriteLine(said);
        said.ShouldBe($"Planetary capture at {planetary.Capture.Roi.Width}x{planetary.Capture.Roi.Height}, 5 ms");
        var state = (await h.Local.Client.GetPlanetaryAsync(ct)).Value.ShouldNotBeNull();
        (state.ExposureMs, state.Gain).ShouldBe((5.0, (short?)null), "the node's own state: the exposure as applied, the camera's own gain");
        state.CurrentFramesPerSecond.ShouldNotBeNull().ShouldBeGreaterThan(0);
        output.WriteLine($"current {state.CurrentFramesPerSecond:F1} fps, the run's average {state.FramesPerSecond:F1}");

        h.Post(new StopVideoCaptureSignal());
        await h.UntilAsync(() => !planetary.IsCapturing, ct);
    }
}
