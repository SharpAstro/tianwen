using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What the short waits in TianWen's own async code cost a frame on Windows, measured THROUGH the code that holds each wait and on
/// the REAL clock, now that #2020 routes every <see cref="SystemTimeProvider"/> sleep up to 2.5 s through
/// <see cref="HighResolutionWait"/> (#2018): none of them may end on a 15.625 ms system timer tick.
/// </summary>
/// <remarks>
/// <para>Each test drives a substitute camera whose frame becomes ready at an instant the camera itself records (the exposure's
/// start plus its length, on <see cref="Stopwatch"/>), so a wait's pickup lag is the time from that instant to the moment the code
/// under test had the frame, which no other wait or driver cost can hide in. The same loop is run again on a clock whose sleep is a
/// plain <see cref="Task.Delay(TimeSpan, CancellationToken)"/>, written beside it as <see cref="SystemTimeProviderPrecisionTests"/>
/// does: that is what these waits cost before #2020, and the asserted numbers are well short of it.</para>
/// <para>The three waits #2018 names, and what each really is:</para>
/// <list type="bullet">
/// <item><description><see cref="CameraDriverExtensions.WaitForImageReadyAsync"/>: one long sleep through the exposure, then a
/// 10 ms and finally a 1 ms poll, every one a <see cref="ITimeProvider.SleepAsync"/> of 10 ms or less once the long sleep is
/// done; a long sleep past <see cref="HighResolutionWait.PreciseUpTo"/> is a <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
/// but ends the lead margin before the frame, so a tick there costs nothing.</description></item>
/// <item><description>The built-in guider's frame capture: <see cref="IExternal.ImageReadyPollInterval"/> (50 ms) is the
/// LEAD MARGIN it hands to that same wait, not a poll period, so no poll of it waits 50 ms and the "3.2 ticks, really 62.5 ms" of
/// the issue never happens.</description></item>
/// <item><description><see cref="PlanetaryCapture"/>'s single-exposure loop (a camera that cannot stream): a 5 ms poll between a
/// start and the frame, the one wait here whose period is a real poll.</description></item>
/// </list>
/// <para>A slow frame through the driver (the QHY PoleMaster's 10 ms exposure took about 2 s from start to image on the bench of
/// 2026-11-05) is therefore not this wait: against a camera that answers at once the wait itself adds a millisecond or two.</para>
/// <para>The measurements run on the REAL clock, so off CI (<see cref="StressTestGate"/>): a shared runner measures the runner. The
/// asserts are on the MEDIAN, as the precision test's are, because on a loaded box a wait now and then ends tens of milliseconds
/// late for want of a thread, which measures the scheduler; a tick-bound wait has a median a whole tick out, so the median still
/// tells the two apart. Mean and worst are written out beside it.</para>
/// <para>A box shared with other builds (a leg's check runs beside the other flights') can delay whole runs of frames by more than
/// the bound allows (a median 3 to 8 ms late and a worst of 40 to 150 ms, 2026-11-07, which no wait can avoid), so a measurement
/// that misses its bound is taken again, up to <see cref="WallClockMeasurement.Attempts"/> times, each set beside the plain
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> measured right after it under the same load. An attempt counts when it is
/// within its bound or at most half as late as that control; a wait bound to the tick is as late as its control on every attempt
/// (its median is 5 to 11 ms out on a quiet box), so it counts on none. Every attempt and its control are written out.</para>
/// </remarks>
[Collection("Devices")]
public class ShortWaitTurnaroundTests(ITestOutputHelper output)
{
    /// <summary>Frames timed per measurement, after <see cref="WarmUp"/>.</summary>
    private const int Frames = 50;

    /// <summary>Frames taken and not timed first: the loop's first pass pays for the JIT and the substitute's set-up.</summary>
    private const int WarmUp = 3;

    /// <summary>How far past the instant a frame is ready the MEDIAN pickup may land when the wait polls every millisecond or finer.</summary>
    private const double PickupSlackMs = 3.0;

    /// <summary>The planetary single-exposure loop's poll period (<c>PlanetaryCapture._readyPollInterval</c>), which bounds its pickup lag.</summary>
    private const double PlanetaryPollMs = 5.0;

    /// <summary>Slack on top of a poll period, as the precision test's (the issue's "about 2 ms", on a loaded box).</summary>
    private const double SlackMs = 2.5;

    /// <summary>How far past the instant a frame is ready the planetary loop's MEDIAN pickup may land: a poll and the slack.</summary>
    private const double PlanetaryLagBoundMs = PlanetaryPollMs + SlackMs;

    /// <summary>The planetary loop's frame interval over its exposure adds the loop's own work (the read, the push, the next start) to the pickup.</summary>
    private const double PlanetaryIntervalBoundMs = PlanetaryLagBoundMs + 2.0;

    [Theory(Timeout = 480_000)]
    [InlineData(10)]
    [InlineData(100)]
    public async Task AFrameIsPickedUpWithinAMillisecondOrTwoOfBeingReady(int exposureMs)
    {
        SkipUnlessMeasurable();
        var ct = TestContext.Current.CancellationToken;
        var exposure = TimeSpan.FromMilliseconds(exposureMs);
        IExternal external = new FakeExternal(output);
        var leadMargin = external.ImageReadyPollInterval;

        async Task<Timings> LagsAsync(ITimeProvider clock)
        {
            var (camera, timeline) = TimedCamera();
            var lags = new double[Frames];
            for (var i = -WarmUp; i < Frames; i++)
            {
                await camera.StartExposureAsync(exposure, FrameType.Light, ct);
                var ready = await camera.WaitForImageReadyAsync(exposure, clock, leadMargin, timeout: null, ct);
                var taken = Stopwatch.GetTimestamp();
                ready.ShouldBeTrue();
                if (i >= 0)
                {
                    lags[i] = Stopwatch.GetElapsedTime(timeline.LastReadyAt, taken).TotalMilliseconds;
                }
            }

            return Timings.Of(lags);
        }

        var (precise, delay) = await PickedUpAsync(
            () => LagsAsync(SystemTimeProvider.Instance),
            () => LagsAsync(new TaskDelayTimeProvider()),
            t => t.Median - PickupSlackMs,
            t => t.Median,
            t => t.ToString(),
            "the frame is picked up a poll after it is ready, not at a timer tick");
        output.WriteLine($"{Frames} frames of {exposureMs} ms, lead margin {leadMargin.TotalMilliseconds:F0} ms: pickup lag SystemTimeProvider {precise}; Task.Delay {delay}");

        precise.Median.ShouldBeGreaterThanOrEqualTo(0, "the wait never returns before the frame is ready");
    }

    [Theory(Timeout = 480_000)]
    [InlineData(20)]
    [InlineData(150)]
    public async Task TheBuiltInGuidersFrameCaptureReturnsWithinAMillisecondOrTwoOfTheFrameBeingReady(int exposureMs)
    {
        SkipUnlessMeasurable();
        var ct = TestContext.Current.CancellationToken;
        var exposure = TimeSpan.FromMilliseconds(exposureMs);
        IExternal external = new FakeExternal(output);
        var leadMargin = external.ImageReadyPollInterval;

        async Task<(Timings Lag, double PollsPerFrame)> LagsAsync(ITimeProvider clock)
        {
            var (camera, timeline) = TimedCamera();
            var lags = new double[Frames];
            for (var i = -WarmUp; i < Frames; i++)
            {
                // The guide loop's own call: it starts the exposure, waits and reads the frame.
                var frame = await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, exposure, clock, leadMargin, ct);
                frame.Release();
                if (i >= 0)
                {
                    lags[i] = Stopwatch.GetElapsedTime(timeline.LastReadyAt, timeline.LastTakenAt).TotalMilliseconds;
                }
            }

            return (Timings.Of(lags), timeline.Polls / (double)(Frames + WarmUp));
        }

        var ((precise, polls), (delay, delayPolls)) = await PickedUpAsync(
            () => LagsAsync(SystemTimeProvider.Instance),
            () => LagsAsync(new TaskDelayTimeProvider()),
            t => t.Lag.Median - PickupSlackMs,
            t => t.Lag.Median,
            t => $"{t.Lag} ({t.PollsPerFrame:F1} polls a frame)",
            "the guide frame is read a poll after it is ready, not a 50 ms interval or a timer tick later");
        output.WriteLine($"{Frames} guide frames of {exposureMs} ms, ImageReadyPollInterval {leadMargin.TotalMilliseconds:F0} ms: "
            + $"capture pickup lag SystemTimeProvider {precise} ({polls:F1} polls a frame); Task.Delay {delay} ({delayPolls:F1} polls a frame)");

        precise.Median.ShouldBeGreaterThanOrEqualTo(0, "the frame is never read before it is ready");
    }

    [Theory(Timeout = 480_000)]
    [InlineData(5)]
    [InlineData(20)]
    public async Task ThePlanetarySingleExposureLoopTakesEachFrameWithinAPollOfItsExposure(int exposureMs)
    {
        SkipUnlessMeasurable();
        var ct = TestContext.Current.CancellationToken;
        var exposure = TimeSpan.FromMilliseconds(exposureMs);

        var (precise, delay) = await PickedUpAsync(
            () => CaptureAsync(SystemTimeProvider.Instance),
            () => CaptureAsync(new TaskDelayTimeProvider()),
            t => Math.Max(t.Lag.Median - PlanetaryLagBoundMs, t.Overhead.Median - PlanetaryIntervalBoundMs),
            t => Math.Max(t.Lag.Median, t.Overhead.Median),
            t => $"pickup lag {t.Lag}, frame interval over the exposure {t.Overhead}",
            "a frame is taken within a poll of being ready and follows its exposure by about a poll, not a timer tick later");
        output.WriteLine($"{Frames} frames of {exposureMs} ms through RapidExposureFramesAsync (5 ms poll): "
            + $"SystemTimeProvider pickup lag {precise.Lag}, frame interval over the exposure {precise.Overhead}; "
            + $"Task.Delay pickup lag {delay.Lag}, frame interval over the exposure {delay.Overhead}");

        precise.Lag.Median.ShouldBeGreaterThanOrEqualTo(0, "the frame is never taken before it is ready");

        async Task<(Timings Lag, Timings Overhead)> CaptureAsync(ITimeProvider clock)
        {
            var (camera, timeline) = TimedCamera();
            var target = WarmUp + Frames + 1;
            var reached = timeline.WhenStarted(target);
            await using var capture = new PlanetaryCapture(clock, NullLogger.Instance);
            capture.Start(camera, new VideoCaptureOptions(exposure), ct).ShouldBeTrue();
            await reached.WaitAsync(ct);
            await capture.StopAsync(ct);

            var (started, ready, taken) = timeline.Snapshot();
            var read = Math.Min(taken.Length, ready.Length);
            var lags = Enumerable.Range(WarmUp, Math.Min(Frames, read - WarmUp))
                .Select(i => Stopwatch.GetElapsedTime(ready[i], taken[i]).TotalMilliseconds).ToArray();
            var overheads = Enumerable.Range(WarmUp, Math.Min(Frames, started.Length - 1 - WarmUp))
                .Select(i => Stopwatch.GetElapsedTime(started[i], started[i + 1]).TotalMilliseconds - exposure.TotalMilliseconds).ToArray();
            lags.Length.ShouldBeGreaterThan(Frames / 2, "the capture took frames");
            overheads.Length.ShouldBeGreaterThan(Frames / 2, "the capture started exposures");
            return (Timings.Of(lags), Timings.Of(overheads));
        }
    }

    /// <summary>
    /// Takes <paramref name="measureAsync"/> until it is within its bound or clear of the same measurement through a plain
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> taken right after it, and returns both: <see cref="WallClockMeasurement"/>.
    /// </summary>
    private Task<(T Result, T Control)> PickedUpAsync<T>(
        Func<Task<T>> measureAsync,
        Func<Task<T>> tickBoundAsync,
        Func<T, double> excess,
        Func<T, double> lateness,
        Func<T, string> describe,
        string because)
        => WallClockMeasurement.MeasureAsync(output, measureAsync, tickBoundAsync, excess, lateness, describe, because);

    private static void SkipUnlessMeasurable()
    {
        StressTestGate.SkipOnCi();
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the 15.6 ms tick is Windows'; other systems already wait fine-grained");
    }

    /// <summary>
    /// A camera whose frame is ready exactly an exposure after it was started, on <see cref="Stopwatch"/>, and which records
    /// every start, readiness instant and read.
    /// </summary>
    private static (ICameraDriver Camera, ExposureTimeline Timeline) TimedCamera()
    {
        var timeline = new ExposureTimeline();
        var camera = Substitute.For<ICameraDriver>();
        camera.StartExposureAsync(Arg.Any<TimeSpan>(), Arg.Any<FrameType>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                timeline.Started(call.Arg<TimeSpan>());
                return ValueTask.FromResult(DateTimeOffset.UtcNow);
            });
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(timeline.IsReady()));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            timeline.Taken();
            return ValueTask.FromResult<Image?>(Image.FromChannel(new float[32, 32], 1f, 0f));
        });
        return (camera, timeline);
    }

    /// <summary>The instants one camera's exposures started, became ready and were read, as <see cref="Stopwatch"/> timestamps.</summary>
    private sealed class ExposureTimeline
    {
        private readonly ConcurrentQueue<long> _started = new();
        private readonly ConcurrentQueue<long> _ready = new();
        private readonly ConcurrentQueue<long> _taken = new();
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _readyAt;
        private long _lastTakenAt;
        private int _polls;
        private int _target = int.MaxValue;

        public long LastReadyAt => Volatile.Read(ref _readyAt);

        public long LastTakenAt => Volatile.Read(ref _lastTakenAt);

        public int Polls => Volatile.Read(ref _polls);

        public void Started(TimeSpan exposure)
        {
            var now = Stopwatch.GetTimestamp();
            var readyAt = now + (long)(exposure.TotalSeconds * Stopwatch.Frequency);
            Volatile.Write(ref _readyAt, readyAt);
            _started.Enqueue(now);
            _ready.Enqueue(readyAt);
            if (_started.Count >= Volatile.Read(ref _target))
            {
                _reached.TrySetResult();
            }
        }

        public bool IsReady()
        {
            Interlocked.Increment(ref _polls);
            return Stopwatch.GetTimestamp() >= Volatile.Read(ref _readyAt);
        }

        public void Taken()
        {
            var now = Stopwatch.GetTimestamp();
            Volatile.Write(ref _lastTakenAt, now);
            _taken.Enqueue(now);
        }

        /// <summary>Completes once <paramref name="exposures"/> have been started.</summary>
        public Task WhenStarted(int exposures)
        {
            Volatile.Write(ref _target, exposures);
            if (_started.Count >= exposures)
            {
                _reached.TrySetResult();
            }

            return _reached.Task;
        }

        public (long[] Started, long[] Ready, long[] Taken) Snapshot() => (_started.ToArray(), _ready.ToArray(), _taken.ToArray());
    }

    /// <summary>
    /// <see cref="SystemTimeProvider"/> but for its sleep, which is a plain <see cref="Task.Delay(TimeSpan, CancellationToken)"/>: what
    /// every short wait was before #2020, so each measurement has its before beside it.
    /// </summary>
    private sealed class TaskDelayTimeProvider : ITimeProvider
    {
        private static SystemTimeProvider Real => SystemTimeProvider.Instance;

        public DateTimeOffset GetUtcNow() => Real.GetUtcNow();

        public long GetTimestamp() => Real.GetTimestamp();

        public long TimestampFrequency => Real.TimestampFrequency;

        public ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => Real.CreateTimer(callback, state, dueTime, period);

        public ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default)
            => new(Task.Delay(duration, cancellationToken));

        public TimeProvider System => Real.System;
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
