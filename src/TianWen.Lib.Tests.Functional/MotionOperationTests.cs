using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Net.Http;
using System.Text.Json;
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
/// Moving a focuser, a filter wheel or a mount through the node, with no session running (P2 part 4 of
/// docs/plans/hardware-in-the-server.md, #929): a move is a job that ends when the device has settled, a second move
/// is refused rather than joined, a stop ends the job it stops, a goto is the GUI's own (<c>MountGoto</c>), and the
/// session-scoped routes from before the device plane now reach a device with no session at all.
/// </summary>
[Collection("Hosting")]
public class MotionOperationTests(ITestOutputHelper outputHelper) : IAsyncLifetime
{
    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(outputHelper, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private IDeviceHub Hub => _node.App.Services.GetRequiredService<IDeviceHub>();

    private TianWenNodeClient Client => new TianWenNodeClient(_node.Client);

    // The fake mount's own site, so the profile's transform and the mount's sidereal time agree.
    private static readonly FakeDevice Mount = new FakeDevice(DeviceType.Mount, 1);
    private static readonly FakeDevice Focuser = new FakeDevice(DeviceType.Focuser, 1);
    private static readonly FakeDevice Wheel = new FakeDevice(DeviceType.FilterWheel, 1);

    /// <summary>An active profile naming the fake mount, focuser and wheel, at the fake mount's site.</summary>
    private async Task ActiveRigAsync(CancellationToken ct)
    {
        var ota = new OTAData("Motion OTA", 400, new FakeDevice(DeviceType.Camera, 1).DeviceUri, null, Focuser.DeviceUri, Wheel.DeviceUri, null, null);
        var profile = new Profile(Guid.NewGuid(), "Motion rig",
            new ProfileData(Mount.DeviceUri, NoneDevice.Instance.DeviceUri, [ota], SiteLatitude: 48.2, SiteLongitude: 16.3));
        await profile.SaveAsync(_node.App.Services.GetRequiredService<IExternal>(), ct);
        await _node.App.Services.GetRequiredService<IHostedSession>().SetActiveProfileAsync(profile.ProfileId, ct);
    }

    private Task<JobDto> UntilEndedAsync(string id, CancellationToken ct) =>
        UntilAsync<JobDto>($"job {id} to end", async token =>
        {
            var job = (await Client.GetJobAsync(id, token)).Value;
            return (job is { State: not JobState.Running } ? job : null, job is null ? "not found" : $"{job.State}: {job.Step}");
        }, ct);

    [Fact(Timeout = 60_000)]
    public async Task AFocuserMovesToAPositionAndByStepsAsJobsThatEndWhenItHasStopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var focuser = (IFocuserDriver)await Hub.ConnectAsync(Focuser, ct);
        var start = await focuser.GetPositionAsync(ct);

        var to = (await Client.MoveFocuserAsync(new FocuserMoveRequestDto { DeviceUri = Focuser.DeviceUri.ToString(), Position = start + 200 }, ct)).Value.ShouldNotBeNull();
        (await UntilEndedAsync(to.Id, ct)).State.ShouldBe(JobState.Succeeded);
        (await focuser.GetPositionAsync(ct)).ShouldBe(start + 200);
        (await focuser.GetIsMovingAsync(ct)).ShouldBeFalse("the job ends when the focuser has stopped");

        var by = (await Client.MoveFocuserAsync(new FocuserMoveRequestDto { DeviceUri = Focuser.DeviceUri.ToString(), Steps = -100 }, ct)).Value.ShouldNotBeNull();
        (await UntilEndedAsync(by.Id, ct)).State.ShouldBe(JobState.Succeeded);
        (await focuser.GetPositionAsync(ct)).ShouldBe(start + 100);

        (await Client.MoveFocuserAsync(new FocuserMoveRequestDto { DeviceUri = Focuser.DeviceUri.ToString() }, ct)).StatusCode.ShouldBe(400);
        (await Client.MoveFocuserAsync(new FocuserMoveRequestDto { DeviceUri = Focuser.DeviceUri.ToString(), Position = 1, Steps = 1 }, ct)).StatusCode.ShouldBe(400);
        (await Client.MoveFocuserAsync(new FocuserMoveRequestDto { DeviceUri = Focuser.DeviceUri.ToString(), Position = focuser.MaxStep + 1 }, ct)).StatusCode.ShouldBe(400);
    }

    // The fake walks 200 steps a second, so a move to the far end of its travel holds the focuser for seconds.
    [Fact(Timeout = 60_000)]
    public async Task ASecondMoveIsRefusedAndAStopEndsTheFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var focuser = (IFocuserDriver)await Hub.ConnectAsync(Focuser, ct);
        var start = await focuser.GetPositionAsync(ct);
        var target = start > focuser.MaxStep / 2 ? 0 : focuser.MaxStep;
        Math.Abs(target - start).ShouldBeGreaterThanOrEqualTo(600, $"a move long enough to catch in flight, from {start} in 0..{focuser.MaxStep}");

        var started = await Client.MoveFocuserAsync(new FocuserMoveRequestDto { DeviceUri = Focuser.DeviceUri.ToString(), Position = target }, ct);
        var moving = started.Value.ShouldNotBeNull(started.Error);
        await UntilAsync("the focuser to move", async token => (await focuser.GetIsMovingAsync(token), "not moving yet"), ct);

        var second = await Client.MoveFocuserAsync(new FocuserMoveRequestDto { DeviceUri = Focuser.DeviceUri.ToString(), Steps = 10 }, ct);
        second.StatusCode.ShouldBe(409, "a move to another position is not the same move");
        second.Error.ShouldNotBeNull().ShouldContain(moving.Id);

        (await Client.StopFocuserAsync(Focuser.DeviceUri, ct)).IsSuccess.ShouldBeTrue("a stop is the one command the running job does not refuse");
        (await UntilEndedAsync(moving.Id, ct)).State.ShouldBe(JobState.Cancelled);
        (await focuser.GetIsMovingAsync(ct)).ShouldBeFalse();
        (await focuser.GetPositionAsync(ct)).ShouldNotBe(target, "it stopped on the way");
    }

    [Fact(Timeout = 60_000)]
    public async Task AFilterWheelTurnsToAPositionAsAJob()
    {
        var ct = TestContext.Current.CancellationToken;
        var wheel = (IFilterWheelDriver)await Hub.ConnectAsync(Wheel, ct);
        var position = wheel.Filters.Count - 1;

        var job = (await Client.ChangeFilterAsync(Wheel.DeviceUri, position, ct)).Value.ShouldNotBeNull();

        job.Kind.ShouldBe("filter");
        (await UntilEndedAsync(job.Id, ct)).State.ShouldBe(JobState.Succeeded);
        (await wheel.GetPositionAsync(ct)).ShouldBe(position);
        (await Client.ChangeFilterAsync(Wheel.DeviceUri, wheel.Filters.Count, ct)).StatusCode.ShouldBe(400);
    }

    [Fact(Timeout = 60_000)]
    public async Task AGotoIsTheGuisOwnAndEndsWhenTheMountHasLanded()
    {
        var ct = TestContext.Current.CancellationToken;
        var mount = (IMountDriver)await Hub.ConnectAsync(Mount, ct);
        var goto_ = new MountGotoRequestDto { DeviceUri = Mount.DeviceUri.ToString(), RaJ2000 = await mount.GetRightAscensionAsync(ct), DecJ2000 = 85, Name = "near the pole" };

        var refused = await Client.GotoAsync(goto_, ct);
        refused.StatusCode.ShouldBe(409, "a goto takes its site from the active profile, and there is none yet");

        await ActiveRigAsync(ct);
        var job = (await Client.GotoAsync(goto_, ct)).Value.ShouldNotBeNull();

        job.Kind.ShouldBe("slew");
        var landed = await UntilEndedAsync(job.Id, ct);
        landed.State.ShouldBe(JobState.Succeeded, landed.Error);
        landed.Step.ShouldNotBeNull().ShouldContain("near the pole");
        (await mount.IsSlewingAsync(ct)).ShouldBeFalse();
        (await mount.GetDeclinationAsync(ct)).ShouldBe(85, 0.01);
    }

    // What only the mount's geometry can answer fails the job with its reason, rather than being a request refusal.
    [Fact(Timeout = 60_000)]
    public async Task AGotoBelowTheHorizonFailsSayingSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var mount = (IMountDriver)await Hub.ConnectAsync(Mount, ct);
        await ActiveRigAsync(ct);

        var job = (await Client.GotoAsync(new MountGotoRequestDto { DeviceUri = Mount.DeviceUri.ToString(), RaJ2000 = 0, DecJ2000 = -80 }, ct)).Value.ShouldNotBeNull();

        var failed = await UntilEndedAsync(job.Id, ct);
        failed.State.ShouldBe(JobState.Failed);
        failed.Error.ShouldNotBeNull().ShouldContain("below the horizon");
        (await mount.IsSlewingAsync(ct)).ShouldBeFalse("nothing was commanded");
    }

    [Fact(Timeout = 60_000)]
    public async Task AMountParksUnparksAndTracksWithNoSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var mount = (IMountDriver)await Hub.ConnectAsync(Mount, ct);

        var park = (await Client.ParkMountAsync(Mount.DeviceUri, ct)).Value.ShouldNotBeNull();
        (await UntilEndedAsync(park.Id, ct)).State.ShouldBe(JobState.Succeeded);
        (await mount.AtParkAsync(ct)).ShouldBeTrue();

        var unpark = (await Client.UnparkMountAsync(Mount.DeviceUri, ct)).Value.ShouldNotBeNull();
        (await UntilEndedAsync(unpark.Id, ct)).State.ShouldBe(JobState.Succeeded);
        (await mount.AtParkAsync(ct)).ShouldBeFalse();

        (await Client.SetMountTrackingAsync(Mount.DeviceUri, true, ct)).IsSuccess.ShouldBeTrue();
        (await mount.IsTrackingAsync(ct)).ShouldBeTrue();
        (await Client.SetMountTrackingAsync(Mount.DeviceUri, false, ct)).IsSuccess.ShouldBeTrue();
        (await mount.IsTrackingAsync(ct)).ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task ADeviceARunHoldsIsNotMovedAndTheRunIsNamed()
    {
        var ct = TestContext.Current.CancellationToken;
        await Hub.ConnectAsync(Mount, ct);
        await Hub.ConnectAsync(Focuser, ct);
        await ActiveRigAsync(ct);
        Hub.TryAcquireLease(Mount.DeviceUri, "Session run", out var mountLease).ShouldBeTrue();
        Hub.TryAcquireLease(Focuser.DeviceUri, "Session run", out var focuserLease).ShouldBeTrue();
        using (mountLease)
        using (focuserLease)
        {
            foreach (var refused in new[]
            {
                (await Client.GotoAsync(new MountGotoRequestDto { DeviceUri = Mount.DeviceUri.ToString(), RaJ2000 = 1, DecJ2000 = 85 }, ct)).Error,
                (await Client.ParkMountAsync(Mount.DeviceUri, ct)).Error,
                (await Client.SetMountTrackingAsync(Mount.DeviceUri, false, ct)).Error,
                (await Client.StopMountAsync(Mount.DeviceUri, ct)).Error,
                (await Client.MoveFocuserAsync(new FocuserMoveRequestDto { DeviceUri = Focuser.DeviceUri.ToString(), Steps = 10 }, ct)).Error,
                (await Client.StopFocuserAsync(Focuser.DeviceUri, ct)).Error,
            })
            {
                refused.ShouldNotBeNull().ShouldContain("Session run");
            }
        }
    }

    // The session-scoped routes from before the device plane answered 404 with no session; re-pointed at it, they reach
    // the active profile's devices.
    [Fact(Timeout = 60_000)]
    public async Task TheOldMountAndOtaRoutesReachTheActiveProfilesDevicesWithNoSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var mount = (IMountDriver)await Hub.ConnectAsync(Mount, ct);
        var focuser = (IFocuserDriver)await Hub.ConnectAsync(Focuser, ct);

        (await StatusAsync("/api/v1/mount/tracking?on=true", ct)).ShouldBe(404, "no session, and no active profile yet");

        await ActiveRigAsync(ct);
        (await StatusAsync("/api/v1/mount/tracking?on=true", ct)).ShouldBe(200);
        (await mount.IsTrackingAsync(ct)).ShouldBeTrue();

        var target = await focuser.GetPositionAsync(ct) + 50;
        (await StatusAsync($"/api/v1/ota/0/focuser/move?position={target}", ct)).ShouldBe(202, "a move is a job now");
        await UntilAsync("the focuser to arrive", async token => (await focuser.GetPositionAsync(token) == target && !await focuser.GetIsMovingAsync(token), "on its way"), ct);
    }

    private async Task<int> StatusAsync(string path, CancellationToken ct)
    {
        using var response = await _node.Client.PostAsync(path, content: null, ct);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return body.RootElement.GetProperty("statusCode").GetInt32();
    }
}
