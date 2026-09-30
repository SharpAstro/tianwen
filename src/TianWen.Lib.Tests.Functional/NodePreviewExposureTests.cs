using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using TianWen.UI.Abstractions;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A preview frame outside a session, taken by the node (P5 part 2 of docs/plans/hardware-in-the-server.md, #934): a job
/// that holds its camera while it exposes, whose frame is then the OTA's on the linear route and the push, and which a
/// snapshot saves. With no session at all, which is exactly where the node used to have no frame to show.
/// </summary>
[Collection("Hosting")]
public class NodePreviewExposureTests(ITestOutputHelper outputHelper)
{
    private static readonly FakeDevice Camera = new FakeDevice(DeviceType.Camera, 1);

    private static TianWenNodeClient ClientOf(NodeHarness node) => new TianWenNodeClient(node.Client);

    /// <summary>An active profile whose one OTA has the fake camera, connected.</summary>
    private static Task ActiveRigAsync(NodeHarness node, CancellationToken ct) => node.ActivateRigAsync(Camera, mount: null, ct);

    private static Task<JobDto> UntilEndedAsync(TianWenNodeClient client, string id, CancellationToken ct) =>
        UntilAsync<JobDto>($"job {id} to end", async token =>
        {
            var job = (await client.GetJobAsync(id, token)).Value;
            return (job is { State: not JobState.Running } ? job : null, job is null ? "not found" : $"{job.State}: {job.Step}");
        }, ct);

    [Fact(Timeout = 60_000)]
    public async Task AnOtaWithNoCameraAssignedIsRefusedInWordsThatSaySoAndNeverNameThePlaceholder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        // An unassigned slot is the none:// placeholder, whose name is "None": the answer used to be "None is not connected".
        var ota = new OTAData("Test OTA", 400, NoneDevice.Instance.DeviceUri, null, null, null, null, null);
        var profile = new Profile(Guid.NewGuid(), "No camera",
            new ProfileData(NoneDevice.Instance.DeviceUri, NoneDevice.Instance.DeviceUri, [ota], SiteLatitude: 48.2, SiteLongitude: 16.3));
        await profile.SaveAsync(node.External, ct);
        await node.Node.SetActiveProfileAsync(profile.ProfileId, ct);

        var preview = await ClientOf(node).StartPreviewExposureAsync(0, new PreviewExposureRequestDto { ExposureSeconds = 1 }, ct);

        preview.StatusCode.ShouldBe(409);
        var error = preview.Error.ShouldNotBeNull();
        error.ShouldContain("OTA 1 has no camera assigned");
        error.ShouldNotContain("None");
    }

    [Fact(Timeout = 60_000)]
    public async Task AStopEndsAPreviewThatWouldNotEndAndFreesItsCamera()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        ((FakeExternal)node.External).MaxFitsWrites = 10;
        await ActiveRigAsync(node, ct);
        var client = ClientOf(node);
        var exposing = (await client.StartPreviewExposureAsync(0, new PreviewExposureRequestDto { ExposureSeconds = 600 }, ct)).Value.ShouldNotBeNull();

        var stopped = await PictureJobs.StopPreviewsAsync(client, new SystemTimeProvider(), progress: null, ct);

        stopped.Select(j => j.Id).ShouldBe([exposing.Id]);
        (await client.GetJobAsync(exposing.Id, ct)).Value.ShouldNotBeNull().State.ShouldBe(JobState.Cancelled);
        // Its camera is free again: the next preview is taken, not refused for a job that is gone.
        var next = await client.StartPreviewExposureAsync(0, new PreviewExposureRequestDto { ExposureSeconds = 0.1 }, ct);
        next.IsSuccess.ShouldBeTrue(next.Error);
        var after = await UntilEndedAsync(client, next.Value.ShouldNotBeNull().Id, ct);
        after.State.ShouldBe(JobState.Succeeded, after.Error);
    }

    [Fact(Timeout = 60_000)]
    public async Task StoppingWithNoPreviewRunningStopsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        await ActiveRigAsync(node, ct);

        (await PictureJobs.StopPreviewsAsync(ClientOf(node), new SystemTimeProvider(), progress: null, ct)).ShouldBeEmpty();
    }

    [Fact(Timeout = 60_000)]
    public async Task APreviewIsAJobHoldingItsCameraAndItsFrameIsTheOtasUntilTheNextReplacesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        ((FakeExternal)node.External).MaxFitsWrites = 10;
        await ActiveRigAsync(node, ct);
        var client = ClientOf(node);
        var pushed = new ConcurrentQueue<FrameAvailableDto>();
        await using var stream = node.Transport.CreateEventStream(new SystemTimeProvider(), FakeExternal.CreateLogger(outputHelper));
        stream.EventReceived += (_, e) =>
        {
            if (FrameAvailableDto.TryFromEvent(e, out var frame))
            {
                pushed.Enqueue(frame);
            }
        };
        stream.Start(ct);
        await UntilAsync("the event stream to connect", _ => ValueTask.FromResult((stream.IsConnected, "not yet")), ct);

        var exposing = (await client.StartPreviewExposureAsync(0, new PreviewExposureRequestDto { ExposureSeconds = 2 }, ct)).Value.ShouldNotBeNull();
        exposing.Kind.ShouldBe(NodePreviews.ExposureJob);

        // While it exposes the camera is the preview's: a command to it is refused naming it, and so is a second preview.
        var settings = await client.SetCameraSettingsAsync(new CameraSettingsRequestDto { DeviceUri = Camera.DeviceUri.ToString(), Bin = 1 }, ct);
        settings.StatusCode.ShouldBe(409);
        settings.Error.ShouldNotBeNull().ShouldContain(NodePreviews.LeaseOwner);
        (await client.StartPreviewExposureAsync(0, new PreviewExposureRequestDto { ExposureSeconds = 0.1 }, ct)).StatusCode.ShouldBe(409);

        (await UntilEndedAsync(client, exposing.Id, ct)).State.ShouldBe(JobState.Succeeded);

        var first = await client.GetLatestFrameAsync(FrameSources.Ota(0), after: null, new FrameReader(), ct);
        var number = first.FrameNumber.ShouldNotBeNull();
        first.Image.ShouldNotBeNull(first.Error).Width.ShouldBeGreaterThan(0);
        first.Image.Release();
        await UntilAsync("FRAME-AVAILABLE for the preview", _ => ValueTask.FromResult((
            pushed.Any(f => f.Source == FrameSources.Ota(0) && f.Number == number), $"{pushed.Count} pushed")), ct);

        (await client.SetCameraSettingsAsync(new CameraSettingsRequestDto { DeviceUri = Camera.DeviceUri.ToString(), Bin = 1 }, ct))
            .IsSuccess.ShouldBeTrue("the preview gives its camera back as it ends");

        var snapshot = await client.SaveSnapshotAsync(0, ct);
        File.Exists(snapshot.Value.ShouldNotBeNull(snapshot.Error)).ShouldBeTrue();
        Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(snapshot.Value))).ShouldBe("Snapshot");

        var again = (await client.StartPreviewExposureAsync(0, new PreviewExposureRequestDto { ExposureSeconds = 0.1 }, ct)).Value.ShouldNotBeNull();
        (await UntilEndedAsync(client, again.Id, ct)).State.ShouldBe(JobState.Succeeded);
        var second = await client.GetLatestFrameAsync(FrameSources.Ota(0), after: number, new FrameReader(), ct);
        second.IsUnchanged.ShouldBeFalse("a new preview is a new frame for a client holding the last");
        second.Image.ShouldNotBeNull(second.Error).Release();
    }

    [Fact(Timeout = 60_000)]
    public async Task APreviewIsRefusedWithoutAnOtaToTakeItWithAndWhileASessionRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        var client = ClientOf(node);
        var request = new PreviewExposureRequestDto { ExposureSeconds = 0.1 };

        (await client.StartPreviewExposureAsync(0, request, ct)).StatusCode.ShouldBe(409, "no active profile");
        (await client.SaveSnapshotAsync(0, ct)).StatusCode.ShouldBe(404, "no frame to save");

        await ActiveRigAsync(node, ct);
        (await client.StartPreviewExposureAsync(1, request, ct)).StatusCode.ShouldBe(404, "the profile has one OTA");

        node.Factory.Initialised.TrySetResult();
        await node.StartSessionAsync(ct);
        var refused = await client.StartPreviewExposureAsync(0, request, ct);
        refused.StatusCode.ShouldBe(409);
        refused.Error.ShouldNotBeNull().ShouldContain("session");
    }
}
