using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Skywatcher;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A SkyWatcher that lost its power, through the node (#1954): the device state says so in words, a goto fails naming it,
/// Home and a move are refused before any job, and the restore route puts the mount back where it last knew itself.
/// </summary>
[Collection("Hosting")]
public class NodeMountPositionLossTests(ITestOutputHelper outputHelper) : IAsyncLifetime
{
    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(outputHelper, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private IDeviceHub Hub => _node.App.Services.GetRequiredService<IDeviceHub>();

    private TianWenNodeClient Client => NodeTestClients.ClientOf(_node.Client);

    // Melbourne, where the AZ-GTi this was found on stands; the profile names the same site.
    private static readonly FakeDevice Mount = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
    {
        { "port", "SkyWatcher" },
        { "latitude", "-37.8769444" },
        { "longitude", "145.1775" },
    });

    private async Task ActiveRigAsync(CancellationToken ct)
    {
        var ota = new OTAData("Loss OTA", 400, new FakeDevice(DeviceType.Camera, 1).DeviceUri, null, null, null, null, null);
        var profile = new Profile(Guid.NewGuid(), "Loss rig",
            new ProfileData(Mount.DeviceUri, NoneDevice.Instance.DeviceUri, [ota], SiteLatitude: -37.8769444, SiteLongitude: 145.1775));
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
    public async Task AMountThatLostPowerIsRefusedEveryMoveUntilItsPositionIsRestored()
    {
        var ct = TestContext.Current.CancellationToken;
        await ActiveRigAsync(ct);
        var mount = (IMountDriver)await Hub.ConnectAsync(Mount, ct);
        var lst = await mount.GetSiderealTimeAsync(ct);
        // Away from home, which the driver writes down as the last known position.
        await mount.SyncRaDecAsync(((lst + 1.0) % 24.0 + 24.0) % 24.0, -60.0, ct);
        var board = ((SkywatcherMountDriverBase<FakeDevice>)mount).SerialConnection.ShouldBeOfType<FakeSkywatcherSerialDevice>();
        var before = board.Steps;

        board.PowerCycle();

        // The node's poller reads the mount, and the reading says what happened, in words.
        var state = await UntilAsync<MountDeviceStateDto>("the mount's state to say it lost its position", async token =>
        {
            var mountState = (await Client.GetDeviceStatesAsync(token)).Value?.FirstOrDefault(d => d.DeviceUri == Mount.DeviceUri.ToString())?.Mount;
            return (mountState is { PositionLost: not null } ? mountState : null, mountState?.PositionLost ?? "not yet");
        }, ct);
        state.PositionLost.ShouldNotBeNull().ShouldContain("lost power");

        var gotoJob = (await Client.GotoAsync(new MountGotoRequestDto { DeviceUri = Mount.DeviceUri.ToString(), RaJ2000 = lst, DecJ2000 = -60, Name = "somewhere" }, ct)).Value.ShouldNotBeNull();
        var failed = await UntilEndedAsync(gotoJob.Id, ct);
        failed.State.ShouldBe(JobState.Failed);
        failed.Error.ShouldNotBeNull().ShouldContain("lost power");

        var home = await Client.HomeMountAsync(Mount.DeviceUri, ct);
        home.StatusCode.ShouldBe(409);
        home.Error.ShouldNotBeNull().ShouldContain("lost power");
        (await Client.MoveAxisAsync(Mount.DeviceUri, TelescopeAxis.Primary, 3.0, ct)).StatusCode.ShouldBe(409);
        board.Steps.ShouldBe((0, 0), "nothing moved");

        var restore = (await Client.RestoreMountPositionAsync(Mount.DeviceUri, MountPositionRestore.LastKnown, ct)).Value.ShouldNotBeNull();
        var restored = await UntilEndedAsync(restore.Id, ct);
        restored.State.ShouldBe(JobState.Succeeded, restored.Error);
        mount.PositionLoss.ShouldBeNull();
        board.Steps.ShouldBe(before);

        (await Client.RestoreMountPositionAsync(Mount.DeviceUri, MountPositionRestore.LastKnown, ct)).StatusCode.ShouldBe(409, "nothing left to restore");
    }
}
