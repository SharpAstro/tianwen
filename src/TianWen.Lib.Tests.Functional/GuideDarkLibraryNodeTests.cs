using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The guide camera's dark library as the node takes it (#1915): the node's dark library asked for the profile's guide camera
/// takes a row per exposure into the guide camera's own folder, the guide-camera loop that follows says it applies them, and
/// nothing is taken while the camera loops (the run going on is named).
/// </summary>
[Collection("Hosting")]
public class GuideDarkLibraryNodeTests(ITestOutputHelper outputHelper)
{
    private static readonly FakeDevice Camera = new FakeDevice(DeviceType.Camera, 1);
    private static readonly FakeDevice GuideCamera = new FakeDevice(DeviceType.Camera, 2);

    // Binned 4 as the loop of NodeGuideLoopTests is: the fake's whole sensor is slow to render a frame of in a Debug build.
    private static readonly DarkLibraryRequestDto Darks = new DarkLibraryRequestDto
    {
        DeviceUri = "",
        CameraRole = LiveViewCamera.Guider,
        ExposureSeconds = 0.01,
        ExposuresSeconds = [0.01, 0.02],
        Count = 2,
        Bin = 4,
    };

    private static readonly LiveViewRequestDto Loop = new LiveViewRequestDto { CameraRole = LiveViewCamera.Guider, ExposureMs = 10, Binning = 4 };

    private static TianWenNodeClient ClientOf(NodeHarness node) => new TianWenNodeClient(node.Client);

    private static Task<DarkLibraryStateDto> UntilEndedAsync(TianWenNodeClient client, CancellationToken ct) =>
        UntilAsync<DarkLibraryStateDto>("the guide camera's darks to end", async token =>
        {
            var state = (await client.GetDarkLibraryAsync(token)).Value;
            return (state is { Running: false } ? state : null, state is null ? "none" : $"{state.Frames.Length} frames");
        }, ct);

    [Fact(Timeout = 90_000)]
    public async Task GuideDarkLibraryTakesAFrameSetPerExposureInTheGuideCamerasFolderAndTheLoopAppliesThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        ((FakeExternal)node.External).MaxFitsWrites = 8;
        await node.ActivateRigAsync(Camera, mount: null, ct, guideCamera: GuideCamera);
        var client = ClientOf(node);

        var started = await client.StartDarkLibraryAsync(Darks, ct);
        started.IsSuccess.ShouldBeTrue(started.Error);
        started.Value.ShouldNotBeNull().CameraRole.ShouldBe(LiveViewCamera.Guider);

        var ended = await UntilEndedAsync(client, ct);
        ended.FailureReason.ShouldBeNull();
        ended.Frames.Length.ShouldBe(4, "two frames at each of the two exposures");
        ended.ExposuresSeconds.ShouldBe([0.01, 0.02]);
        foreach (var frame in ended.Frames)
        {
            frame.Path.Replace('\\', '/').ShouldContain("/Darks/Guide/");
            File.Exists(frame.Path).ShouldBeTrue(frame.Path);
        }

        var before = await client.StartLiveViewAsync(Loop, ct);
        before.IsSuccess.ShouldBeTrue(before.Error);
        var state = await UntilAsync<LiveViewStateDto>("the loop to say it applies the darks", async token =>
        {
            var s = (await client.GetLiveViewAsync(token)).Value;
            return (s is { FramesReceived: > 0, DarksApplied: true } ? s : null, s is null ? "none" : $"{s.FramesReceived} frames, darks applied {s.DarksApplied}");
        }, ct);
        state.DarksApplied.ShouldBeTrue("the library taken a moment ago is the loop's");
        (await client.StopLiveViewAsync(ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact(Timeout = 90_000)]
    public async Task GuideDarkLibraryIsRefusedWhileTheGuideCameraLoopsAndNamesTheRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        await node.ActivateRigAsync(Camera, mount: null, ct, guideCamera: GuideCamera);
        var client = ClientOf(node);

        (await client.StartLiveViewAsync(Loop, ct)).IsSuccess.ShouldBeTrue();

        var refused = await client.StartDarkLibraryAsync(Darks, ct);
        refused.StatusCode.ShouldBe(409);
        refused.Error.ShouldNotBeNull().ShouldContain("live view");

        (await client.StopLiveViewAsync(ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task GuideDarkLibraryRefusesAProfileWithNoGuideCameraInWords()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        await node.ActivateRigAsync(Camera, mount: null, ct);

        var refused = await ClientOf(node).StartDarkLibraryAsync(Darks, ct);
        (refused.StatusCode, refused.Error).ShouldBe((409, "The active profile has no guide camera to take darks of"));
    }

    [Fact(Timeout = 60_000)]
    public async Task GuideDarkLibraryHoldsTheGuideCameraForTheRunAndGivesItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        await node.ActivateRigAsync(Camera, mount: null, ct, guideCamera: GuideCamera);
        var client = ClientOf(node);

        (await client.StartDarkLibraryAsync(Darks, ct)).IsSuccess.ShouldBeTrue();
        var second = await client.StartDarkLibraryAsync(Darks, ct);
        second.StatusCode.ShouldBe(409);

        await UntilEndedAsync(client, ct);
        (await client.StartLiveViewAsync(Loop, ct)).IsSuccess.ShouldBeTrue("the camera is the loop's once the darks end");
        (await client.StopLiveViewAsync(ct)).IsSuccess.ShouldBeTrue();
    }
}
