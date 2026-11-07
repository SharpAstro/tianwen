using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Judges a measurement taken on the REAL clock, for the tests that sit behind <see cref="StressTestGate"/>: against a bound on a
/// quiet box, and against a tick-bound wait measured beside it on a loaded one.
/// </summary>
/// <remarks>
/// <para>A box shared with other builds (a leg's check runs beside the other flights') can hold a whole run of waits back by more
/// than any bound a quiet box allows: a below-normal process on a saturated machine gets a median 3 to 8 ms late and a worst of 40 to
/// 150 ms, tick or no tick. So a measurement is taken up to <see cref="Attempts"/> times, and each attempt is set beside the same
/// measurement through a plain <see cref="Task.Delay(TimeSpan, CancellationToken)"/> taken right after it, under the same load.
/// An attempt is accepted when it is within its bound (a quiet box), or when it is at most <see cref="ClearOfTheControl"/> as late
/// as that tick-bound control (a loaded box, where the control is late by the load and the tick together).</para>
/// <para>A wait that rides the 15.625 ms timer tick is as late as its control on EVERY attempt, loaded or quiet (its median is 5 to
/// 11 ms out on a quiet box), so neither test lets it through. Every attempt and its control are written out, so a pass that took
/// a second try, or passed on the control, says so.</para>
/// </remarks>
internal static class WallClockMeasurement
{
    /// <summary>Times a measurement is taken, at most, when a loaded box makes it miss its bound.</summary>
    public const int Attempts = 5;

    /// <summary>The share of the tick-bound control's lateness an attempt may have, past its bound, and still count as clear of the tick.</summary>
    public const double ClearOfTheControl = 0.5;

    /// <summary>
    /// Takes <paramref name="measureAsync"/> until an attempt is accepted or <see cref="Attempts"/> are used, then fails naming the
    /// closest one. Returns the accepted attempt with the control taken right after it.
    /// </summary>
    /// <param name="output">Where every attempt and control is written.</param>
    /// <param name="measureAsync">One measurement.</param>
    /// <param name="tickBoundAsync">The same measurement through a plain <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="excess">How far a result is past its bound; below zero is within it.</param>
    /// <param name="lateness">How late a result is past its asked time, in ms, to compare with the control's.</param>
    /// <param name="describe">A result in words, for the output.</param>
    /// <param name="because">What the bound stands for, for the failure.</param>
    public static async Task<(T Result, T Control)> MeasureAsync<T>(
        ITestOutputHelper output,
        Func<Task<T>> measureAsync,
        Func<Task<T>> tickBoundAsync,
        Func<T, double> excess,
        Func<T, double> lateness,
        Func<T, string> describe,
        string because)
    {
        (T Result, T Control, double Excess)? closest = null;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var result = await measureAsync();
            output.WriteLine($"attempt {attempt} of {Attempts}: {describe(result)}");
            var control = await tickBoundAsync();
            output.WriteLine($"    tick-bound control right after it: {describe(control)}");
            var taken = (result, control, Excess: excess(result));

            if (taken.Excess < 0)
            {
                return (result, control);
            }

            if (lateness(result) <= ClearOfTheControl * lateness(control))
            {
                output.WriteLine($"    over its bound by {taken.Excess:F2} ms on a loaded box, but {lateness(result):F2} ms late against "
                    + $"{lateness(control):F2} ms for the tick-bound wait beside it: clear of the tick");
                return (result, control);
            }

            if (closest is not { } best || taken.Excess < best.Excess)
            {
                closest = taken;
            }
        }

        if (closest is { } nearest)
        {
            Assert.Fail($"{because}: no attempt of {Attempts} was within its bound or clear of the tick-bound control. "
                + $"Closest: {describe(nearest.Result)}, {nearest.Excess:F2} ms over its bound; its control {describe(nearest.Control)}.");
        }

        throw new UnreachableException();
    }
}
