using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The proof of P3 (#930, part 5 of docs/plans/hardware-in-the-server.md): a profile's life through a spawned node over its
/// socket, as the Equipment tab will live it once the node is the one profile writer. Created, assigned, reconciled by a
/// discovery, written into by connecting its mount and camera, a device setting placed, a stale edit refused, and every
/// write pushed as <c>PROFILE-CHANGED</c>.
/// </summary>
[Collection("NodeProcesses")]
public class ProfileFlowProcessTests(ITestOutputHelper output)
{
    private static readonly Uri Mount = new FakeDevice(DeviceType.Mount, 1).DeviceUri;
    private static readonly Uri Camera = new FakeDevice(DeviceType.Camera, 1).DeviceUri;
    private static readonly Uri Focuser = new FakeDevice(DeviceType.Focuser, 1).DeviceUri;

    private static async Task<JobDto> SucceedsAsync(TianWenNodeClient client, NodeResult<JobDto> started, CancellationToken ct)
    {
        var job = started.Value.ShouldNotBeNull(started.Error);
        var ended = await UntilAsync<JobDto>($"{job.Kind} job {job.Id} to end", async token =>
        {
            var now = (await client.GetJobAsync(job.Id, token)).Value;
            return (now is { State: not JobState.Running } ? now : null, now is null ? "not found" : $"{now.State}: {now.Step}");
        }, ct);
        ended.State.ShouldBe(JobState.Succeeded, $"{ended.Kind}: {ended.Error}");
        return ended;
    }

    [Fact(Timeout = 240_000)]
    public async Task AProfilesLifeRunsThroughTheNodeOverItsSocket()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kept = await KeptNode.StartAsync(ct);
        var client = kept.Client;

        var pushed = new ConcurrentQueue<ProfileChangedDto>();
        await using var stream = NodeTransport.OverSocket(kept.SocketPath).CreateEventStream(new SystemTimeProvider(), FakeExternal.CreateLogger(output));
        stream.EventReceived += (_, e) =>
        {
            if (ProfileChangedDto.TryFromEvent(e, out var change))
            {
                pushed.Enqueue(change);
            }
        };
        stream.Start(ct);
        await UntilAsync("the event stream to connect", _ => ValueTask.FromResult((stream.IsConnected, "not yet")), ct);

        // Created, then its devices assigned as the Equipment tab assigns them, and made the node's active profile.
        var created = (await client.CreateProfileAsync("Flow", ct)).Value.ShouldNotBeNull();
        var id = created.ProfileId;
        var assigned = EquipmentActions.ApplyAssignment(created.Data.ShouldNotBeNull(), new AssignTarget.ProfileLevel("Mount"), DeviceType.Mount, Mount);
        assigned = EquipmentActions.AddOTA(assigned, new OTAData("Main", 800, Camera, Cover: null, Focuser: Focuser, FilterWheel: null,
            PreferOutwardFocus: null, OutwardIsPositive: null));
        var edited = (await client.UpdateProfileAsync(id, assigned, created.Revision.ShouldNotBeNull(), name: null, ct)).Value.ShouldNotBeNull();
        (await client.SetActiveProfileAsync(id, ct)).IsSuccess.ShouldBeTrue();

        // A discovery reconciles it with what it found, and connecting its mount and camera writes into it.
        await SucceedsAsync(client, await client.StartDiscoveryAsync(ct), ct);
        (await SucceedsAsync(client, await client.ConnectDeviceAsync(Mount, ct), ct)).Step.ShouldNotBeNull().ShouldContain("the profile took the mount's site");
        (await SucceedsAsync(client, await client.ConnectDeviceAsync(Camera, ct), ct)).Step.ShouldNotBeNull().ShouldContain("its sensor recorded in the profile");
        var connected = (await client.GetProfileAsync(id, ct)).Value.ShouldNotBeNull();
        var data = connected.Data.ShouldNotBeNull();
        data.Site.ShouldNotBeNull("the profile took no site from its mount");
        data.OTAs[0].CameraSensorWidthPx.ShouldNotBeNull("the profile recorded no sensor");

        // A device setting placed onto the profile's focuser.
        var setting = (await client.SetDeviceSettingAsync(Focuser, DeviceQueryKey.FocuserBacklashIn.Key, "25", id, ct)).Value.ShouldNotBeNull();
        setting.Secret.ShouldBeFalse();
        var afterSetting = (await client.GetProfileAsync(id, ct)).Value.ShouldNotBeNull();
        afterSetting.Revision.ShouldBe(setting.Revision);
        HttpUtility.ParseQueryString(afterSetting.Data.ShouldNotBeNull().OTAs[0].Focuser.ShouldNotBeNull().Query)[DeviceQueryKey.FocuserBacklashIn.Key].ShouldBe("25");

        // An edit made against the revision before the node's own writes is refused, not applied over them.
        (await client.UpdateProfileAsync(id, assigned with { GuiderFocalLength = 180 }, edited.Revision.ShouldNotBeNull(), name: null, ct))
            .StatusCode.ShouldBe(412);

        // The active profile is not deleted from under the node; every write was pushed, the last at the revision stored.
        (await client.DeleteProfileAsync(id, ct)).StatusCode.ShouldBe(409);
        await UntilAsync("the last write pushed", _ => ValueTask.FromResult((
            pushed.Any(c => c.ProfileId == id && c.Revision == afterSetting.Revision), $"{pushed.Count(c => c.ProfileId == id)} pushed for the profile")), ct);
        pushed.Count(c => c.ProfileId == id).ShouldBeGreaterThanOrEqualTo(5, "create, assign, the mount's site, the sensor and the setting are each a write");
    }
}
