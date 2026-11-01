using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FC.SDK;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Canon;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Every wait on a Canon body ends at a deadline on the injected clock: the identity read discovery makes of a body over WPD, and
/// the drain of the announced-object queue at a disconnect. Each deadline is set to an hour (the pump drain's, 10 s, is a
/// parameter of the routine that waits), the body answers nothing, and the fake clock is advanced by that hour ONCE, once the
/// deadline's timer exists: a deadline on the wall clock would hold for the hour, and the test's own timeout would end it instead.
/// </summary>
public class CanonDeadlineTests
{
    private static readonly TimeSpan Long = TimeSpan.FromHours(1);
    private const string Path = @"\\?\usb#vid_04a9&pid_3250#6&fd1ea69&1&2#{6ac27878-a6fa-4155-ba85-f98f491d4f33}";

    [Fact(Timeout = 30_000)]
    public async Task ABodyThatNeverAnswersItsIdentityIsKeyedByItsPathAtTheReadDeadline()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProviderWrapper();
        var bodies = new CanonBodyRegistry();
        var source = new CanonDeviceSource(NullLogger<CanonDeviceSource>.Instance, new SilentWpd(), bodies, LocalOnlyMode.Off, clock)
        {
            IdentityReadTimeout = Long
        };

        var discovery = source.DiscoverWpdAsync(ct);
        await clock.WaitForTimerDueAfterAsync(TimeSpan.FromMinutes(30), ct);
        discovery.IsCompleted.ShouldBeFalse("the body has not answered and the clock has not moved");
        clock.Advance(Long);

        var camera = (await discovery).ShouldHaveSingleItem();
        camera.WpdDeviceId.ShouldBe(Path, "a read that is slow leaves the camera keyed by its path");
        bodies.TryGetSerial(Path, out _).ShouldBeFalse("nothing was learned, so the next discovery asks again");
    }

    [Fact(Timeout = 30_000)]
    public async Task AnObjectQueueThatNeverFinishesIsStoppedAtTheDrainBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProviderWrapper();
        var stop = new CancellationTokenSource();
        // A reader stuck on a body that never answers a release: it ends only when it is stopped.
        var pump = Task.Run(async () => await new TaskCompletionSource().Task.WaitAsync(stop.Token), CancellationToken.None);

        var drain = CanonCameraDriver.DrainPumpAsync(pump, stop, clock, Long, NullLogger.Instance);
        await clock.WaitForTimerDueAfterAsync(TimeSpan.FromMinutes(30), ct);
        drain.IsCompleted.ShouldBeFalse("the reader is still waiting and the clock has not moved");
        clock.Advance(Long);

        await drain;
        pump.IsCanceled.ShouldBeTrue("the budget stopped the reader through its own token");
    }

    [Fact(Timeout = 30_000)]
    public async Task AnObjectQueueThatFinishesInTimeIsNotStopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProviderWrapper();
        var stop = new CancellationTokenSource();
        var finished = new TaskCompletionSource();

        var drain = CanonCameraDriver.DrainPumpAsync(finished.Task, stop, clock, Long, NullLogger.Instance);
        await clock.WaitForTimerDueAfterAsync(TimeSpan.FromMinutes(30), ct);
        finished.SetResult();

        await drain;
        stop.IsCancellationRequested.ShouldBeFalse("the reader ended on its own before the budget");
    }

    /// <summary>One camera that lists and never answers what it is: a read that ends only when its token is cancelled.</summary>
    private sealed class SilentWpd : ICanonWpd
    {
        public IEnumerable<(string Id, string Name)> Enumerate() => [(Path, "Canon EOS 6D")];

        public Task<CanonBodyIdentity?> ReadIdentityAsync(string wpdId, CancellationToken cancellationToken)
            => new TaskCompletionSource<CanonBodyIdentity?>().Task.WaitAsync(cancellationToken);
    }
}
