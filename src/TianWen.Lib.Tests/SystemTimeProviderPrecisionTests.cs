using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="SystemTimeProvider"/>'s short waits and short one-shot timers end on time on Windows, where a
/// <see cref="Task.Delay(TimeSpan)"/> waits out a whole 15.625 ms system timer tick; a guide pulse timed on our side lasts as long
/// as its wait, so the tick was guiding error (#2020).
/// </summary>
/// <remarks>
/// The measurements run on the REAL clock, so off CI (<see cref="StressTestGate"/>): a shared runner measures the runner. The
/// collection keeps them from running beside the device tests, which would load the pool they are measuring.
/// </remarks>
[Collection("Devices")]
public class SystemTimeProviderPrecisionTests(ITestOutputHelper output)
{
    private const int Waits = 50;

    /// <summary>How far past the asked time the MEDIAN precise wait may end: the issue's "about 2 ms", on a loaded box.</summary>
    /// <remarks>
    /// The median, not the mean: on a box whose CPU is saturated by other work a wait now and then ends tens of milliseconds
    /// late because no thread ran its continuation (2026-11-05: a 77 ms worst among 50 waits of 20 ms), which measures the
    /// scheduler, not the timer. A tick-bound wait has a median a whole tick out, so the median still tells the two apart.
    /// Mean and worst are written out beside it.
    /// </remarks>
    private const double SlackMs = 2.5;

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public async Task AShortWaitEndsOnTimeWhereADelayWaitsATick(int milliseconds)
    {
        SkipUnlessMeasurable();
        var ct = TestContext.Current.CancellationToken;
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var provider = SystemTimeProvider.Instance;

        var precise = await BestOfAsync(milliseconds, () => MeasureAsync(() => provider.SleepAsync(duration, ct).AsTask()));
        var delay = await MeasureAsync(() => Task.Delay(duration, ct));
        output.WriteLine($"{Waits} waits of {milliseconds} ms: SleepAsync {precise}; Task.Delay {delay}");

        precise.Median.ShouldBeGreaterThanOrEqualTo(milliseconds - 0.05, "it waits at least what was asked");
        precise.Median.ShouldBeLessThan(milliseconds + SlackMs, "it ends near the asked time, not at a timer tick");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public async Task AShortOneShotTimerFiresOnTime(int milliseconds)
    {
        SkipUnlessMeasurable();
        var ct = TestContext.Current.CancellationToken;
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var provider = SystemTimeProvider.Instance;

        // Created disarmed and armed by Change, as the DAL camera's pulse timer is.
        var precise = await BestOfAsync(milliseconds, () => MeasureAsync(async () =>
        {
            var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var timer = provider.CreateTimer(static state => ((TaskCompletionSource?)state)?.TrySetResult(), fired,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            timer.Change(duration, Timeout.InfiniteTimeSpan);
            await fired.Task.WaitAsync(ct);
        }));
        output.WriteLine($"{Waits} one-shot timers of {milliseconds} ms: {precise}");

        precise.Median.ShouldBeGreaterThanOrEqualTo(milliseconds - 0.05, "it fires no earlier than asked");
        precise.Median.ShouldBeLessThan(milliseconds + SlackMs, "it fires near the due time, not at a timer tick");
    }

    [Fact]
    public async Task ACancelledShortWaitThrowsAndReleasesItsTimer()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = SystemTimeProvider.Instance;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var handlesBefore = process.HandleCount;

        const int cancelled = 2000;
        for (var i = 0; i < cancelled; i++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var wait = provider.SleepAsync(TimeSpan.FromMilliseconds(500), cts.Token).AsTask();
            cts.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(wait);
        }

        process.Refresh();
        var grown = process.HandleCount - handlesBefore;
        output.WriteLine($"{cancelled} cancelled waits: handle count {handlesBefore} -> {process.HandleCount}");
        grown.ShouldBeLessThan(cancelled / 10, "a cancelled wait closes its timer");
    }

    [Fact]
    public async Task ASupersededOrDisposedOneShotFiresNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = SystemTimeProvider.Instance;
        var fires = 0;

        var rearmed = provider.CreateTimer(_ => Interlocked.Increment(ref fires), null, TimeSpan.FromMilliseconds(30), Timeout.InfiniteTimeSpan);
        rearmed.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var disposed = provider.CreateTimer(_ => Interlocked.Increment(ref fires), null, TimeSpan.FromMilliseconds(30), Timeout.InfiniteTimeSpan);
        await disposed.DisposeAsync();

        await provider.SleepAsync(TimeSpan.FromMilliseconds(200), ct);
        Volatile.Read(ref fires).ShouldBe(0);
        rearmed.Change(TimeSpan.FromMilliseconds(30), Timeout.InfiniteTimeSpan).ShouldBeTrue("a disarmed timer can be armed again");
        await provider.SleepAsync(TimeSpan.FromMilliseconds(200), ct);
        Volatile.Read(ref fires).ShouldBe(1);
        await rearmed.DisposeAsync();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    public async Task ADriverTimedDalPulseIsSwitchedOffOnTime(int milliseconds)
    {
        SkipUnlessMeasurable();
        var ct = TestContext.Current.CancellationToken;
        var duration = TimeSpan.FromMilliseconds(milliseconds);

        // The real clock, which a DAL camera on a guide port uses at night; the scripted body times nothing itself
        // (CanPulseGuideForDuration is false), so the driver switches the line off.
        var external = new FakeExternal(output, new FakeTimeProviderWrapper());
        var services = new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(SystemTimeProvider.Instance)
            .AddSingleton<IDeviceHub, DeviceHub>()
            .AddLogging()
            .BuildServiceProvider();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var state = new FakeCmosState(device.DeviceId) { HasST4Port = true };
        var camera = new TestDalCameraDriver(device, services, state);
        await camera.ConnectAsync(ct);
        camera.CanPulseGuide.ShouldBeTrue();

        var on = await BestOfAsync(milliseconds, async () =>
        {
            var lengths = new double[Waits];
            for (var i = 0; i < Waits; i++)
            {
                var pulse = state.NextPulse();
                await camera.StartPulseGuideAsync(GuideDirection.West, duration, ct);
                lengths[i] = (await pulse.WaitAsync(ct)).TotalMilliseconds;

                // The bookkeeping clears right after the line is switched off, on the timer's own thread.
                while (await camera.GetIsPulseGuidingAsync(ct))
                {
                    await Task.Yield();
                }
            }

            return Timings.Of(lengths);
        });
        output.WriteLine($"{Waits} driver-timed pulses of {milliseconds} ms: on for {on}");
        on.Median.ShouldBeGreaterThanOrEqualTo(milliseconds - 0.05, "the line is on for at least the pulse");
        on.Median.ShouldBeLessThan(milliseconds + SlackMs, "the line is switched off near the pulse's end, not at a timer tick");

        await camera.DisconnectAsync(ct);
    }

    private static void SkipUnlessMeasurable()
    {
        StressTestGate.SkipOnCi();
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the 15.6 ms tick is Windows'; other systems already wait fine-grained");
    }

    /// <summary>
    /// Takes the measurement again while its median is past <see cref="SlackMs"/> over <paramref name="milliseconds"/> (a box shared
    /// with other builds delays whole runs of waits), and returns the best: <see cref="WallClockMeasurement"/>.
    /// </summary>
    private Task<Timings> BestOfAsync(int milliseconds, Func<Task<Timings>> measureAsync)
        => WallClockMeasurement.BestOfAsync(output, measureAsync, t => t.Median - (milliseconds + SlackMs), t => t.ToString());

    /// <summary>Runs <paramref name="wait"/> <see cref="Waits"/> times one after the other, timing each.</summary>
    private static async Task<Timings> MeasureAsync(Func<Task> wait)
    {
        var lengths = new double[Waits];
        for (var i = 0; i < Waits; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await wait();
            lengths[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        return Timings.Of(lengths);
    }

    /// <summary>A set of measured lengths, in ms.</summary>
    private readonly record struct Timings(double Median, double Mean, double Worst)
    {
        public static Timings Of(double[] lengths)
        {
            Array.Sort(lengths);
            var mid = lengths.Length / 2;
            var median = lengths.Length % 2 == 1 ? lengths[mid] : (lengths[mid - 1] + lengths[mid]) / 2;
            return new Timings(median, lengths.Average(), lengths[^1]);
        }

        public override string ToString() => $"{Median:F2} ms median, {Mean:F2} mean, {Worst:F2} worst";
    }
}
