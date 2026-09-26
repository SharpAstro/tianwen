using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A device takes one job at a time (P2 part 2 of docs/plans/hardware-in-the-server.md, #929): another start of the same
/// kind joins the running job, one of another kind is refused naming it, and two devices run their jobs side by side.
/// <c>DeviceOperationTests</c> drives the same rule through a real node.
/// </summary>
public class NodeJobsPerDeviceTests
{
    private static readonly Uri Camera = new Uri("Camera://FakeDevice/1#Fake Camera 1");
    private static readonly Uri Focuser = new Uri("Focuser://FakeDevice/1#Fake Focuser 1");

    /// <summary>A job's body that runs until the test lets it end, counting how often it was run.</summary>
    private sealed class HeldWork
    {
        private int _runs;

        public TaskCompletionSource Began { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Runs => Volatile.Read(ref _runs);

        public async Task<string?> RunAsync(NodeJobs.JobStep step, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            Began.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return "done";
        }
    }

    private static NodeJobs Jobs() => new NodeJobs(Substitute.For<IHostApplicationLifetime>(), new SystemTimeProvider(), NullLogger<NodeJobs>.Instance);

    private static async Task UntilEndedAsync(NodeJobs jobs, string id, CancellationToken ct)
    {
        while (!jobs.TryGet(id, out var job) || job.State is JobState.Running)
        {
            await Task.Delay(10, ct);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task ASecondStartOfTheSameKindOnADeviceJoinsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobs = Jobs();
        var work = new HeldWork();

        jobs.TryStartOrJoin("connect", Camera, work.RunAsync, out var first).ShouldBeTrue();
        await work.Began.Task.WaitAsync(ct);
        // The same device however its URI is spelt: the slot is the device's key, not its query or its name.
        jobs.TryStartOrJoin("connect", new Uri("Camera://FakeDevice/1?port=COM3"), work.RunAsync, out var second).ShouldBeTrue();

        second.Id.ShouldBe(first.Id, "a second connect of a camera is the same connect");
        first.DeviceUri.ShouldBe(Camera.ToString());
        work.Runs.ShouldBe(1);
        work.Release.SetResult();
    }

    [Fact(Timeout = 10_000)]
    public async Task AJobOfAnotherKindOnTheSameDeviceIsRefusedNamingTheOneThatHoldsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobs = Jobs();
        var connect = new HeldWork();
        var disconnect = new HeldWork();

        jobs.TryStartOrJoin("connect", Camera, connect.RunAsync, out var running).ShouldBeTrue();
        await connect.Began.Task.WaitAsync(ct);

        jobs.TryStartOrJoin("disconnect", Camera, disconnect.RunAsync, out var holder).ShouldBeFalse("a disconnect half-way through a connect races it for the driver");
        holder.Id.ShouldBe(running.Id);
        holder.Kind.ShouldBe("connect");
        disconnect.Runs.ShouldBe(0);
        connect.Release.SetResult();
    }

    [Fact(Timeout = 10_000)]
    public async Task TwoDevicesRunTheirJobsSideBySide()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobs = Jobs();
        var camera = new HeldWork();
        var focuser = new HeldWork();

        jobs.TryStartOrJoin("connect", Camera, camera.RunAsync, out var cameraJob).ShouldBeTrue();
        jobs.TryStartOrJoin("connect", Focuser, focuser.RunAsync, out var focuserJob).ShouldBeTrue();

        await Task.WhenAll(camera.Began.Task, focuser.Began.Task).WaitAsync(ct);
        focuserJob.Id.ShouldNotBe(cameraJob.Id);
        camera.Release.SetResult();
        focuser.Release.SetResult();
    }

    [Fact(Timeout = 10_000)]
    public async Task OnceItsJobHasEndedTheDeviceTakesAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobs = Jobs();
        var connect = new HeldWork();
        jobs.TryStartOrJoin("connect", Camera, connect.RunAsync, out var connectJob).ShouldBeTrue();
        connect.Release.SetResult();
        await UntilEndedAsync(jobs, connectJob.Id, ct);

        var disconnect = new HeldWork();
        jobs.TryStartOrJoin("disconnect", Camera, disconnect.RunAsync, out var disconnectJob).ShouldBeTrue();

        disconnectJob.Id.ShouldNotBe(connectJob.Id);
        disconnect.Release.SetResult();
        await UntilEndedAsync(jobs, disconnectJob.Id, ct);
        jobs.TryGet(disconnectJob.Id, out var ended).ShouldBeTrue();
        ended.DeviceUri.ShouldBe(Camera.ToString(), "an ended job still names its device");
    }

    // A discovery holds its KIND, a device job its DEVICE: neither stands in the other's way.
    [Fact(Timeout = 10_000)]
    public async Task ADiscoveryDoesNotHoldADevicesJobUp()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobs = Jobs();
        var discovery = new HeldWork();
        var discoveryJob = jobs.StartOrJoin("discover", discovery.RunAsync);
        await discovery.Began.Task.WaitAsync(ct);

        var connect = new HeldWork();
        jobs.TryStartOrJoin("connect", Camera, connect.RunAsync, out var connectJob).ShouldBeTrue();

        connectJob.Id.ShouldNotBe(discoveryJob.Id);
        discoveryJob.DeviceUri.ShouldBeNull();
        discovery.Release.SetResult();
        connect.Release.SetResult();
    }
}
