using Shouldly;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The proof of the device plane (P2 part 6 of docs/plans/hardware-in-the-server.md, #929): the Equipment tab's flows,
/// in the order the tab runs them, driven end to end over the SOCKET through <see cref="TianWenNodeClient"/> against a
/// spawned node (<see cref="KeptNode"/>: <c>--fake-devices --local-only</c>, a temp socket and data root). This is the
/// client P6 cuts the GUI over to, so what passes here is what the GUI will be able to do without a hub of its own.
/// </summary>
[Collection("NodeProcesses")]
public class EquipmentFlowProcessTests(ITestOutputHelper output)
{
    private static readonly Guid ProfileId = Guid.Parse("7e57ab1e-0b0e-4e5d-9a5e-000000000929");
    private static readonly Uri Mount = new Uri("Mount://FakeDevice/FakeMount1?latitude=48.2&longitude=16.3");
    private static readonly Uri Camera = new Uri("Camera://FakeDevice/FakeCamera1");
    private static readonly Uri Focuser = new Uri("Focuser://FakeDevice/FakeFocuser1");
    private static readonly Uri Wheel = new Uri("FilterWheel://FakeDevice/FakeFilterWheel1");

    /// <summary>The rig as the GUI would have saved it, at the fake mount's site.</summary>
    private static Task SeedProfileAsync(string dataRoot, CancellationToken ct)
    {
        var profiles = Directory.CreateDirectory(Path.Combine(dataRoot, "Profiles")).FullName;
        var data = new ProfileData(
            Mount: Mount,
            Guider: new Uri("Guider://NoneDevice/none"),
            OTAs: [new OTAData("OTA 1", 1000, Camera: Camera, Cover: null, Focuser: Focuser, FilterWheel: Wheel,
                PreferOutwardFocus: null, OutwardIsPositive: null)],
            SiteLatitude: 48.2,
            SiteLongitude: 16.3);
        return File.WriteAllTextAsync(Path.Combine(profiles, Profile.DeviceIdFromUUID(ProfileId) + ".json"),
            JsonSerializer.Serialize(new ProfileDto(ProfileId, "Equipment flow", data), Profile.ProfileJsonSerializerContextIndented.ProfileDto), ct);
    }

    private static Task<JobDto> EndedAsync(TianWenNodeClient client, JobDto job, CancellationToken ct) =>
        UntilAsync<JobDto>($"{job.Kind} job {job.Id} to end", async token =>
        {
            var now = (await client.GetJobAsync(job.Id, token)).Value;
            return (now is { State: not JobState.Running } ? now : null, now is null ? "not found" : $"{now.State}: {now.Step}");
        }, ct);

    private static async Task<JobDto> SucceedsAsync(TianWenNodeClient client, NodeResult<JobDto> started, CancellationToken ct)
    {
        var ended = await EndedAsync(client, started.Value.ShouldNotBeNull(started.Error), ct);
        ended.State.ShouldBe(JobState.Succeeded, $"{ended.Kind}: {ended.Error}");
        return ended;
    }

    private static Task<DeviceStateDto> StateAsync(TianWenNodeClient client, Uri device, Func<DeviceStateDto, bool> holds, CancellationToken ct) =>
        UntilAsync<DeviceStateDto>($"the node's reading of {device}", async token =>
        {
            var state = (await client.GetDeviceStatesAsync(token)).Value?.FirstOrDefault(s => new Uri(s.DeviceUri).DeviceKey == device.DeviceKey);
            return (state is { ReadUtc: not null } && holds(state) ? state : null, state is null ? "not listed" : $"read {state.ReadUtc:HH:mm:ss.fff}");
        }, ct);

    [Fact(Timeout = 240_000)]
    public async Task TheEquipmentTabsFlowsRunOverTheSocketAgainstASpawnedNode()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kept = await KeptNode.StartAsync(ct, dataRoot => SeedProfileAsync(dataRoot, ct));
        var client = kept.Client;
        (await client.SetActiveProfileAsync(ProfileId, ct)).IsSuccess.ShouldBeTrue();

        // The event stream, as the GUI will hold it: every DEVICE-STATE the node pushes.
        var pushed = new ConcurrentQueue<DeviceStateDto>();
        await using var stream = NodeTransport.OverSocket(kept.SocketPath).CreateEventStream(new SystemTimeProvider(), FakeExternal.CreateLogger(output));
        stream.EventReceived += (_, e) =>
        {
            if (DeviceStateDto.TryFromEvent(e, out var state))
            {
                pushed.Enqueue(state);
            }
        };
        stream.Start(ct);
        await UntilAsync("the event stream to connect", _ => ValueTask.FromResult((stream.IsConnected, "not yet")), ct);

        // Discover, then connect every device the profile names: each a job the node finishes.
        await SucceedsAsync(client, await client.StartDiscoveryAsync(ct), ct);
        foreach (var device in new[] { Mount, Camera, Focuser, Wheel })
        {
            await SucceedsAsync(client, await client.ConnectDeviceAsync(device, ct), ct);
            await StateAsync(client, device, static s => s.Connected, ct);
        }
        await UntilAsync("a push for every device", _ => ValueTask.FromResult((
            new[] { Mount, Camera, Focuser, Wheel }.All(d => pushed.Any(s => new Uri(s.DeviceUri).DeviceKey == d.DeviceKey)), $"{pushed.Count} pushed")), ct);

        // The camera: a cool-down is the session's ramp, running in the node; switched off at once, it says so.
        var cooling = (await client.CoolCameraAsync(Camera, -10, rampMinutes: null, ct)).Value.ShouldNotBeNull();
        await StateAsync(client, Camera, static s => s.Camera is { CoolerOn: true, CoolerIntent: CoolerIntentKind.Cool }, ct);
        (await client.CancelJobAsync(cooling.Id, ct)).IsSuccess.ShouldBeTrue();
        (await EndedAsync(client, cooling, ct)).State.ShouldBe(JobState.Cancelled);
        (await client.CameraCoolerOffAsync(Camera, ct)).IsSuccess.ShouldBeTrue();
        await StateAsync(client, Camera, static s => s.Camera is { CoolerOn: false, CoolerIntent: CoolerIntentKind.Off }, ct);
        var settings = (await client.SetCameraSettingsAsync(new CameraSettingsRequestDto { DeviceUri = Camera.ToString(), Bin = 2 }, ct)).Value.ShouldNotBeNull();
        (settings.BinX, settings.BinY).ShouldBe((2, 2));

        // The focuser and the filter wheel.
        var focuserStart = (await StateAsync(client, Focuser, static s => s.Focuser is { IsMoving: false }, ct)).Focuser.ShouldNotBeNull().Position;
        await SucceedsAsync(client, await client.MoveFocuserAsync(new FocuserMoveRequestDto { DeviceUri = Focuser.ToString(), Steps = 100 }, ct), ct);
        await StateAsync(client, Focuser, s => s.Focuser is { IsMoving: false } f && f.Position == focuserStart + 100, ct);
        await SucceedsAsync(client, await client.ChangeFilterAsync(Wheel, 1, ct), ct);
        await StateAsync(client, Wheel, static s => s.FilterWheel is { Position: 1 }, ct);

        // The mount: a goto near the pole through the GUI's own goto, a park and unpark, tracking, and a held move-axis
        // let go.
        var ra = (await StateAsync(client, Mount, static s => s.Mount is { RaJ2000: not null }, ct)).Mount.ShouldNotBeNull().RaJ2000.ShouldNotBeNull();
        await SucceedsAsync(client, await client.GotoAsync(new MountGotoRequestDto { DeviceUri = Mount.ToString(), RaJ2000 = ra, DecJ2000 = 85 }, ct), ct);
        await SucceedsAsync(client, await client.ParkMountAsync(Mount, ct), ct);
        await SucceedsAsync(client, await client.UnparkMountAsync(Mount, ct), ct);
        (await client.SetMountTrackingAsync(Mount, true, ct)).IsSuccess.ShouldBeTrue();
        var moving = (await client.MoveAxisAsync(Mount, TelescopeAxis.Primary, 1.0, ct)).Value.ShouldNotBeNull();
        (await client.MoveAxisAsync(Mount, TelescopeAxis.Primary, 1.0, ct)).Value.ShouldNotBeNull().Id.ShouldBe(moving.Id);
        (await client.MoveAxisAsync(Mount, TelescopeAxis.Primary, 0, ct)).IsSuccess.ShouldBeTrue();
        (await EndedAsync(client, moving, ct)).State.ShouldBe(JobState.Succeeded);

        // Disconnect: the camera's cooler is off, so it is safe to; it goes through the warm-up job, the rest at once.
        (await client.GetDisconnectSafetyAsync(Camera, ct)).Value.ShouldNotBeNull().Safety.ShouldBe(DisconnectSafety.Safe);
        await SucceedsAsync(client, await client.WarmAndDisconnectDeviceAsync(Camera, ct), ct);
        foreach (var device in new[] { Mount, Focuser, Wheel })
        {
            await SucceedsAsync(client, await client.DisconnectDeviceAsync(device, skipWarmUp: false, ct), ct);
        }
        await UntilAsync("every device gone from the node's list", async token =>
        {
            var left = (await client.GetDeviceStatesAsync(token)).Value;
            return (left is { Length: 0 }, $"{left?.Length} listed");
        }, ct);
        await UntilAsync("a goodbye pushed for every device", _ => ValueTask.FromResult((
            new[] { Mount, Camera, Focuser, Wheel }.All(d => pushed.Any(s => !s.Connected && new Uri(s.DeviceUri).DeviceKey == d.DeviceKey)), $"{pushed.Count} pushed")), ct);
    }
}
