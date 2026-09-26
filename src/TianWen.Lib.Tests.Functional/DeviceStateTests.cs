using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
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
/// The device plane's read side over a real node (P2 part 1 of docs/plans/hardware-in-the-server.md, #929): every
/// connected device read and served with its lease and, for a mount, its safety-limit verdict; a device a run holds left
/// to the run; a change pushed as <c>DEVICE-STATE</c>. The cadences and the push's wire form: <c>DeviceStatePollerTests</c>.
/// </summary>
[Collection("Hosting")]
public class DeviceStateTests(ITestOutputHelper outputHelper) : IAsyncLifetime
{
    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(outputHelper, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private IDeviceHub Hub => _node.App.Services.GetRequiredService<IDeviceHub>();

    private TianWenNodeClient Client => new TianWenNodeClient(_node.Client);

    /// <summary>Waits until the node has read the device at <paramref name="uri"/> and <paramref name="holds"/> of it.</summary>
    private Task<DeviceStateDto> UntilReadAsync(Uri uri, Func<DeviceStateDto, bool> holds, CancellationToken ct) =>
        UntilAsync<DeviceStateDto>($"the node's reading of {uri}", async token =>
        {
            var states = (await Client.GetDeviceStatesAsync(token)).Value;
            var state = states?.FirstOrDefault(s => new Uri(s.DeviceUri).DeviceKey == uri.DeviceKey);
            return (state is { ReadUtc: not null } && holds(state) ? state : null,
                state is null ? "not listed" : $"read {state.ReadUtc:HH:mm:ss.fff}, held by {state.LeaseOwner ?? "nothing"}");
        }, ct);

    [Fact(Timeout = 60_000)]
    public async Task EveryConnectedDeviceIsReadAndServedWithItsLeaseAndItsLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var camera = new FakeDevice(DeviceType.Camera, 1);
        var focuser = new FakeDevice(DeviceType.Focuser, 1);
        var filterWheel = new FakeDevice(DeviceType.FilterWheel, 1);
        var mount = new FakeDevice(DeviceType.Mount, 1);
        var cover = new FakeDevice(DeviceType.CoverCalibrator, 1);
        foreach (var device in new DeviceBase[] { camera, focuser, filterWheel, mount, cover })
        {
            await Hub.ConnectAsync(device, ct);
        }

        var cameraState = await UntilReadAsync(camera.DeviceUri, static s => s.Camera is not null, ct);
        cameraState.DeviceType.ShouldBe(DeviceType.Camera);
        cameraState.Connected.ShouldBeTrue();
        cameraState.LeaseOwner.ShouldBeNull("nothing holds it");
        cameraState.Camera.ShouldNotBeNull().State.ShouldBe(CameraState.Idle);

        (await UntilReadAsync(focuser.DeviceUri, static s => s.Focuser is not null, ct)).Focuser.ShouldNotBeNull();
        (await UntilReadAsync(filterWheel.DeviceUri, static s => s.FilterWheel is not null, ct)).FilterWheel.ShouldNotBeNull();

        var mountState = (await UntilReadAsync(mount.DeviceUri, static s => s.Mount is not null, ct)).Mount.ShouldNotBeNull();
        mountState.RightAscension.ShouldNotBeNull();
        mountState.RaJ2000.ShouldBe(mountState.RightAscension, "a J2000 mount's pointing is its J2000");
        mountState.Limit.ShouldNotBeNull("the node's safety-limit verdict, which the GUI reads every frame today");

        var coverState = (await UntilReadAsync(cover.DeviceUri, static s => s.Cover is not null, ct)).Cover.ShouldNotBeNull();
        coverState.CoverState.ShouldBe(CoverStatus.Closed);
        coverState.CalibratorState.ShouldBe(CalibratorStatus.Off);
    }

    // A run reads the devices it holds itself, and two readers on one serial port race: the node leaves a held device
    // alone, keeps its last reading, and names the run holding it.
    [Fact(Timeout = 60_000)]
    public async Task ADeviceARunHoldsIsLeftToTheRunAndNamesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Focuser, 1);
        var focuser = (IFocuserDriver)await Hub.ConnectAsync(device, ct);
        var before = await UntilReadAsync(device.DeviceUri, static s => s.Focuser is { IsMoving: false }, ct);
        var position = before.Focuser.ShouldNotBeNull().Position;

        Hub.TryAcquireLease(device.DeviceUri, "Session run", out var lease).ShouldBeTrue();
        using (lease)
        {
            await focuser.BeginMoveAsync(position + 500, ct);
            var held = await UntilReadAsync(device.DeviceUri, static s => s.LeaseOwner is not null, ct);
            held.LeaseOwner.ShouldBe("Session run");

            // Longer than the watched cadence (2 s), so the node would have read an unheld focuser again by now.
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            var stillHeld = (await Client.GetDeviceStatesAsync(ct)).Value.ShouldNotBeNull()
                .Single(s => new Uri(s.DeviceUri).DeviceKey == device.DeviceUri.DeviceKey);
            stillHeld.ReadUtc.ShouldBe(before.ReadUtc, "a held device is the run's to read");
            stillHeld.Focuser.ShouldNotBeNull().Position.ShouldBe(position);
        }

        var released = await UntilReadAsync(device.DeviceUri, s => s.LeaseOwner is null && s.ReadUtc != before.ReadUtc, ct);
        released.Focuser.ShouldNotBeNull().Position.ShouldNotBe(position, "let go, it is read again and has moved");
    }

    [Fact(Timeout = 60_000)]
    public async Task AChangeIsPushedAsDeviceStateAndSoIsAGoodbye()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Focuser, 1);
        var pushed = new System.Collections.Concurrent.ConcurrentQueue<DeviceStateDto>();
        await using var stream = new TianWenEventStream(
            _node.Client.BaseAddress ?? throw new InvalidOperationException("the harness client has no base address"),
            new SystemTimeProvider(), FakeExternal.CreateLogger(outputHelper));
        stream.EventReceived += (_, e) =>
        {
            if (DeviceStateDto.TryFromEvent(e, out var state) && new Uri(state.DeviceUri).DeviceKey == device.DeviceUri.DeviceKey)
            {
                pushed.Enqueue(state);
            }
        };
        stream.Start(ct);
        await UntilAsync("the event stream to connect", _ => ValueTask.FromResult((stream.IsConnected, stream.IsConnected ? "connected" : "not yet")), ct);

        var focuser = (IFocuserDriver)await Hub.ConnectAsync(device, ct);
        await UntilAsync("the focuser's first push", _ => ValueTask.FromResult((pushed.Any(static s => s.Focuser is not null), $"{pushed.Count} pushed")), ct);
        var position = pushed.Last(static s => s.Focuser is not null).Focuser.ShouldNotBeNull().Position;

        // A read that changes nothing pushes nothing, though the reading time moves on every read: longer than the watched
        // cadence (2 s), an idle focuser is read again and must not be pushed again, or every client is flooded.
        var pushedWhileIdle = pushed.Count;
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        pushed.Count.ShouldBe(pushedWhileIdle, "an idle focuser read again is no change");

        await focuser.BeginMoveAsync(position + 500, ct);
        await UntilAsync("a push of the move", _ => ValueTask.FromResult((pushed.Any(s => s.Focuser is { } f && f.Position != position), $"{pushed.Count} pushed")), ct);

        await Hub.DisconnectAsync(device.DeviceUri, cancellationToken: ct);
        await UntilAsync("a push that it has gone", _ => ValueTask.FromResult((pushed.Any(static s => !s.Connected), $"{pushed.Count} pushed")), ct);
    }
}
