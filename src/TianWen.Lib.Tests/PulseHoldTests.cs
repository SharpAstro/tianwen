using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The hold of a host-timed guide pulse (#2330): the pulse's timer is armed before the caller is told the pulse is
/// commanded. Told first, the caller could wait on the same clock before the driver's thread reached its sleep, and on a
/// clock a test advances the pulse was then held from wherever the clock had got to: a 750 ms Dec pulse ran for several
/// times that, and a calibration lost its star (<c>GuiderCalibrationOnFakeGuideCameraTests</c>, once on CI).
/// </summary>
public class PulseHoldTests
{
    private static readonly TimeSpan Pulse = TimeSpan.FromMilliseconds(750);

    /// <summary>How a clock refuses a sleep on a cancelled token.</summary>
    public enum Refusal
    {
        /// <summary>It does not: the sleep is over at once.</summary>
        None,

        /// <summary>By throwing out of the call itself.</summary>
        Throws,

        /// <summary>By handing back a cancelled task, which is how the clocks that ship refuse
        /// (<c>FakeTimeProviderWrapper</c>, <c>SystemTimeProvider</c>).</summary>
        ReturnsCancelled,
    }

    /// <summary>A clock whose sleep notes whether the caller had been told already, and is over at once (or refuses).</summary>
    private sealed class ArmingClock(TaskCompletionSource commanded, Refusal refusal = Refusal.None) : ITimeProvider
    {
        private readonly FakeTimeProviderWrapper _inner = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 11, 22, 3, 0, 0, TimeSpan.Zero));

        public List<(TimeSpan Duration, bool CallerToldAlready)> Armed { get; } = [];

        public DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public long GetTimestamp() => _inner.GetTimestamp();
        public long TimestampFrequency => _inner.TimestampFrequency;
        public ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => _inner.CreateTimer(callback, state, dueTime, period);
        public TimeProvider System => _inner.System;

        public ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        {
            Armed.Add((duration, commanded.Task.IsCompleted));
            return refusal switch
            {
                Refusal.Throws => throw new OperationCanceledException(cancellationToken),
                Refusal.ReturnsCancelled => ValueTask.FromCanceled(cancellationToken),
                _ => ValueTask.CompletedTask,
            };
        }
    }

    [Fact]
    public async Task ThePulsesTimerIsArmedBeforeTheCallerIsToldThePulseIsCommanded()
    {
        var commanded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ArmingClock(commanded);

        await PulseHold.RunAsync(clock, Pulse, commanded, TestContext.Current.CancellationToken);

        clock.Armed.ShouldBe([(Pulse, false)], "one sleep of the pulse's length, armed while the caller still waited to be told");
        commanded.Task.IsCompletedSuccessfully.ShouldBeTrue("and then the caller is told");
    }

    /// <summary>
    /// A cancelled token refuses the sleep. The board has been commanded all the same, so the caller is released (it
    /// would otherwise wait for ever on a pulse whose driver has already gone to stop the axis), and the refusal comes
    /// out of the hold for the driver's own handling. Both ways a clock can refuse (#2437): out of the call, which the
    /// hold's <c>finally</c> answers, and as a cancelled task, which is what the clocks that ship hand back and which
    /// only the <c>await</c> after the caller was told throws.
    /// </summary>
    [Theory]
    [InlineData(Refusal.Throws)]
    [InlineData(Refusal.ReturnsCancelled)]
    public async Task AHoldWhoseTimerIsRefusedStillTellsTheCaller(Refusal refusal)
    {
        var commanded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ArmingClock(commanded, refusal);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // Caught by hand: the refusal ends the hold's task cancelled, and what comes out of it is what is asserted.
        OperationCanceledException? refused = null;
        try
        {
            await PulseHold.RunAsync(clock, Pulse, commanded, cancelled.Token);
        }
        catch (OperationCanceledException ex)
        {
            refused = ex;
        }

        refused.ShouldNotBeNull("the refusal comes out of the hold");
        commanded.Task.IsCompletedSuccessfully.ShouldBeTrue();
        clock.Armed.ShouldBe([(Pulse, false)]);
    }

    /// <summary>
    /// The three holds the SkyWatcher driver times itself go through <see cref="PulseHold"/>, and none sleeps or tells
    /// its caller by hand (#2437). Read off the source, because the driver cannot be made to show it: with the old order
    /// the caller's own task completes on another thread a moment later, so "had the caller been told when the sleep was
    /// armed" reads no in both orders nearly every time, and a site put back would pass every other test here and come
    /// back only as the flake #2330 was.
    /// </summary>
    [Fact]
    public void EveryHoldTheSkywatcherDriverTimesGoesThroughPulseHold()
    {
        if (ChromeMeasuresThroughTheEngineTests.FindSourceRoot() is not { } src)
        {
            Assert.Skip("the sources are not beside the binaries here");
            return;
        }

        var driver = File.ReadAllText(Path.Combine(src, "TianWen.Lib", "Devices", "Skywatcher", "SkywatcherMountDriverBase.cs"));
        // The pulse's core, up to the micro-goto that follows it (which holds nothing: its length is a step count).
        var start = driver.IndexOf("private async ValueTask PulseGuideCoreAsync(", StringComparison.Ordinal);
        var end = driver.IndexOf("private async ValueTask DecPulseAsMicroGotoAsync(", StringComparison.Ordinal);
        (start >= 0 && end > start).ShouldBeTrue("premise: the pulse's core and the micro-goto after it are where they were");
        var core = driver[start..end];

        Occurrences(core, "await PulseHold.RunAsync(TimeProvider, duration, commanded, cancellationToken);")
            .ShouldBe(3, "RA while tracking, RA on a stopped axis, and Dec");
        Occurrences(core, "SleepAsync(").ShouldBe(0, "a hold that sleeps by hand has left PulseHold");
        Occurrences(core, "commanded.TrySetResult()").ShouldBe(0, "and so has one that tells its caller by hand");
    }

    private static int Occurrences(string text, string what)
    {
        var count = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
