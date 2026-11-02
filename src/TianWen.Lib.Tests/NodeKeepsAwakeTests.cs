using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The node keeps the computer awake while it holds hardware (#1957): a connected device, a lease, a run or a running job
/// each take the request, the last one let go releases it once, two holders take it once, and stopping releases it. Driven one
/// look at a time over a fake power request, so each take and release is counted exactly.
/// </summary>
public class NodeKeepsAwakeTests
{
    private sealed class FakePower : IPowerRequest
    {
        public int Takes { get; private set; }
        public int Releases { get; private set; }
        public int Held => Takes - Releases;

        public IDisposable Acquire(string reason)
        {
            Takes++;
            return new Handle(this);
        }

        private sealed class Handle(FakePower owner) : IDisposable
        {
            private bool _released;

            public void Dispose()
            {
                if (!_released)
                {
                    _released = true;
                    owner.Releases++;
                }
            }
        }
    }

    private sealed class Rig
    {
        public readonly FakeTimeProviderWrapper Clock = new FakeTimeProviderWrapper();
        public readonly IHostedSession Node = Substitute.For<IHostedSession>();
        public readonly IDeviceHub Hub = Substitute.For<IDeviceHub>();
        public readonly IHostApplicationLifetime Lifetime = Substitute.For<IHostApplicationLifetime>();
        public readonly NodeJobs Jobs;
        public readonly FakePower Power = new FakePower();
        public readonly NodeKeepsAwake Service;

        public Rig()
        {
            Hub.ConnectedDevices.Returns(new List<(Uri, IDeviceDriver)>());
            Hub.Leases.Returns(new List<DeviceLease>());
            Jobs = new NodeJobs(Lifetime, Clock, NullLogger<NodeJobs>.Instance);
            Service = new NodeKeepsAwake(Hub, Node, Jobs, Power, Clock, NullLogger<NodeKeepsAwake>.Instance);
        }

        public void Connected(bool on) => Hub.ConnectedDevices.Returns(on
            ? new List<(Uri, IDeviceDriver)> { (new Uri("Camera://CanonDevice/2977d924"), Substitute.For<IDeviceDriver>()) }
            : new List<(Uri, IDeviceDriver)>());

        public void Leased(bool on) => Hub.Leases.Returns(on
            ? new List<DeviceLease> { new DeviceLease(new Uri("Mount://SkyWatcherDevice/abc"), "the imaging session") }
            : new List<DeviceLease>());
    }

    [Fact]
    public void Nothing_held_takes_nothing()
    {
        var rig = new Rig();

        rig.Service.Check();
        rig.Service.Check();

        rig.Power.Takes.ShouldBe(0);
    }

    [Fact]
    public void A_connected_device_takes_it_and_disconnecting_releases_it_once()
    {
        var rig = new Rig();
        rig.Connected(true);

        rig.Service.Check();
        rig.Power.Held.ShouldBe(1);

        rig.Connected(false);
        rig.Service.Check();
        rig.Service.Check();

        rig.Power.Takes.ShouldBe(1);
        rig.Power.Releases.ShouldBe(1);
    }

    [Fact]
    public void A_lease_alone_takes_it()
    {
        var rig = new Rig();
        rig.Leased(true);

        rig.Service.Check();

        rig.Power.Held.ShouldBe(1);
    }

    [Fact]
    public void A_run_takes_it()
    {
        var rig = new Rig();
        rig.Node.RunningKind.Returns(NodeRunKind.Darks);

        rig.Service.Check();

        rig.Power.Held.ShouldBe(1);
    }

    [Fact]
    public async Task A_running_job_takes_it_and_its_end_releases_it()
    {
        var rig = new Rig();
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Jobs.StartOrJoin("warm-and-disconnect", (_, _) => finish.Task);

        rig.Service.Check();
        rig.Power.Held.ShouldBe(1);

        finish.SetResult("Warmed");
        await Task.Yield();
        for (var i = 0; i < 100 && rig.Power.Held > 0; i++)
        {
            await Task.Yield();
            rig.Service.Check();
            if (rig.Power.Held > 0)
            {
                await Task.Delay(1, TestContext.Current.CancellationToken);
            }
        }

        rig.Power.Held.ShouldBe(0);
        rig.Power.Takes.ShouldBe(1);
    }

    [Fact]
    public void Two_holders_take_it_once_and_it_is_released_after_the_last()
    {
        var rig = new Rig();
        rig.Connected(true);
        rig.Leased(true);

        rig.Service.Check();
        rig.Service.Check();
        rig.Power.Takes.ShouldBe(1, "two holders are one request");

        rig.Connected(false);
        rig.Service.Check();
        rig.Power.Releases.ShouldBe(0, "a lease still holds");

        rig.Leased(false);
        rig.Service.Check();
        rig.Power.Releases.ShouldBe(1);
        rig.Power.Takes.ShouldBe(1);
    }

    [Fact]
    public async Task Stopping_the_service_releases_it()
    {
        var rig = new Rig();
        rig.Connected(true);
        rig.Service.Check();
        rig.Power.Held.ShouldBe(1);

        await rig.Service.StopAsync(TestContext.Current.CancellationToken);

        rig.Power.Releases.ShouldBe(1);
        rig.Power.Held.ShouldBe(0);
    }
}
