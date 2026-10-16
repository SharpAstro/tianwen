using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A filter change through the node's device plane while a planetary capture streams through the wheel (#1618, Phase G of
/// docs/plans/live-planetary-capture.md): the OTA route stops the capture's stream, turns the wheel through the session's one
/// filter change (<see cref="FilterChange"/>), moves the OTA's focuser by the focus offset of the filter arrived at less the
/// filter left, and starts the stream again; the next recording is named for the new filter. During a recording the change
/// is refused in words and the wheel stays where it was. Over the fake camera, wheel (L 0, R +20, G 0, B -15) and focuser.
/// </summary>
[Collection("Hosting")]
public class PlanetaryFilterChangeTests(ITestOutputHelper outputHelper)
{
    private static readonly FakeDevice Camera = new FakeDevice(DeviceType.Camera, 1);
    private static readonly FakeDevice Wheel = new FakeDevice(DeviceType.FilterWheel, 1);
    private static readonly FakeDevice Focuser = new FakeDevice(DeviceType.Focuser, 1);

    private const int L = 0, R = 1, G = 2, B = 3;

    private static Task<JobDto> UntilEndedAsync(TianWenNodeClient client, string id, CancellationToken ct) =>
        UntilAsync<JobDto>($"job {id} to end", async token =>
        {
            var job = (await client.GetJobAsync(id, token)).Value;
            return (job is { State: not JobState.Running } ? job : null, job is null ? "not found" : $"{job.State}: {job.Step}");
        }, ct);

    private static Task<PlanetaryStateDto> UntilStateAsync(TianWenNodeClient client, string what, Func<PlanetaryStateDto, bool> done, CancellationToken ct) =>
        UntilAsync<PlanetaryStateDto>(what, async token =>
        {
            var state = (await client.GetPlanetaryAsync(token)).Value;
            return (state is not null && done(state) ? state : null, state is null ? "none" : $"running {state.Running}, {state.FramesReceived} frames");
        }, ct);

    // Changes OTA 0's filter through the route a client uses, and waits for the job to succeed.
    private async Task<JobDto> ChangeAsync(TianWenNodeClient client, int position, CancellationToken ct)
    {
        var started = await client.ChangeOtaFilterAsync(0, position, ct);
        var job = started.Value.ShouldNotBeNull($"{started.StatusCode}: {started.Error}");
        job.Kind.ShouldBe(DeviceOperations.FilterJob);
        var ended = await UntilEndedAsync(client, job.Id, ct);
        outputHelper.WriteLine($"to {position}: {ended.State}, {ended.Step} {ended.Error}");
        ended.State.ShouldBe(JobState.Succeeded, ended.Error);
        return ended;
    }

    [Fact(Timeout = 120_000)]
    public async Task AFilterChangeDuringACaptureRestartsItsStreamRefocusesByTheOffsetAndNamesTheNextRecording()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        var ota = new OTAData("Test OTA", 1200, Camera.DeviceUri, null, Focuser.DeviceUri, Wheel.DeviceUri, null, null, Aperture: 254,
            OpticalDesign: OpticalDesign.Newtonian);
        var profile = new Profile(Guid.NewGuid(), "Mono rig", new ProfileData(NoneDevice.Instance.DeviceUri, NoneDevice.Instance.DeviceUri, [ota]));
        await profile.SaveAsync(node.External, ct);
        await node.Node.SetActiveProfileAsync(profile.ProfileId, ct);
        var hub = node.App.Services.GetRequiredService<IDeviceHub>();
        var camera = (FakeCameraDriver)await hub.ConnectAsync(Camera, ct);
        var wheel = (IFilterWheelDriver)await hub.ConnectAsync(Wheel, ct);
        var focuser = (IFocuserDriver)await hub.ConnectAsync(Focuser, ct);
        (await wheel.GetPositionAsync(ct)).ShouldBe(L, "the fake wheel starts at luminance");
        var luminanceFocus = await focuser.GetPositionAsync(ct);

        var client = new TianWenNodeClient(node.Client);
        (await client.StartPlanetaryAsync(new PlanetaryRequestDto(), ct)).IsSuccess.ShouldBeTrue();
        await UntilStateAsync(client, "the capture to stream", s => s.FramesReceived > 0, ct);
        camera.VideoStarts.Length.ShouldBe(1);

        // Luminance to red, then red to green: the second moves the focuser back by red's offset, where the rule before the
        // hoist left it at red's (green's offset is 0, and it applied only a non-zero target offset).
        var toRed = await ChangeAsync(client, R, ct);
        (await wheel.GetPositionAsync(ct)).ShouldBe(R);
        (await focuser.GetPositionAsync(ct) - luminanceFocus).ShouldBe(20);
        toRed.Step.ShouldNotBeNull().ShouldContain("the planetary capture streams again");
        camera.VideoStarts.Length.ShouldBe(2, "the change stopped the stream and started it again");

        await ChangeAsync(client, G, ct);
        (await wheel.GetPositionAsync(ct)).ShouldBe(G);
        (await focuser.GetPositionAsync(ct) - luminanceFocus).ShouldBe(0, "green focuses where luminance does");
        camera.VideoStarts.Length.ShouldBe(3);
        camera.VideoStarts[2].Options.Exposure.ShouldBe(camera.VideoStarts[0].Options.Exposure, "at the settings it had");
        hub.TryGetLease(Wheel.DeviceUri, out _).ShouldBeFalse("the change claims the wheel only while it moves it");
        hub.TryGetLease(Focuser.DeviceUri, out _).ShouldBeFalse("and the focuser");
        hub.TryGetLease(Camera.DeviceUri, out var held).ShouldBeTrue();
        held.OwnerLabel.ShouldBe("planetary capture", "the capture keeps the camera throughout");

        var streaming = (await client.GetPlanetaryAsync(ct)).Value.ShouldNotBeNull();
        await UntilStateAsync(client, "frames after the change", s => s.FramesReceived > streaming.FramesReceived, ct);

        // The next recording is named for the filter the wheel now holds.
        var recording = await client.StartPlanetaryRecordingAsync(new PlanetaryRecordRequestDto { DurationSeconds = 60 }, ct);
        recording.IsSuccess.ShouldBeTrue(recording.Error);
        var path = recording.Value.ShouldNotBeNull().Recording.ShouldNotBeNull().Path;
        outputHelper.WriteLine(path);
        Path.GetFileName(path).ShouldStartWith("Green_");

        // During it a change is refused in words before anything moves: one file must not hold two filters.
        var refused = await client.ChangeOtaFilterAsync(0, B, ct);
        outputHelper.WriteLine($"{refused.StatusCode}: {refused.Error}");
        refused.StatusCode.ShouldBe(409);
        refused.Error.ShouldBe("A recording is being made: a filter change would stop the stream it is made from, and put frames from "
            + "before and after it in one file. Stop the recording, or let it end, first");
        (await wheel.GetPositionAsync(ct)).ShouldBe(G, "the wheel did not move");
        (await focuser.GetPositionAsync(ct) - luminanceFocus).ShouldBe(0, "nor the focuser");
        camera.VideoStarts.Length.ShouldBe(3, "nor was the stream stopped");

        (await client.StopPlanetaryRecordingAsync(ct)).IsSuccess.ShouldBeTrue();
        await UntilStateAsync(client, "the recording to be written", s => s.Recording is { Written: true }, ct);
        (await client.StopPlanetaryAsync(ct)).IsSuccess.ShouldBeTrue();
        hub.TryGetLease(Camera.DeviceUri, out _).ShouldBeFalse("the capture ends normally and gives the camera back");
    }
}
