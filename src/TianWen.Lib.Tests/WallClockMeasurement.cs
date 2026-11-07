using System;
using System.Threading.Tasks;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Retries a measurement taken on the REAL clock, for the tests that sit behind <see cref="StressTestGate"/>.
/// </summary>
/// <remarks>
/// A box shared with other builds (a leg's check runs beside the other flights') can delay a whole run of frames, so a
/// measurement that misses its bound is taken again, up to <see cref="Attempts"/> times, and the best attempt is the one asserted.
/// Load only ever ADDS to a lag, so the lowest result is the nearest to the code's own, while a wait bound to the 15.625 ms system
/// timer tick misses its bound on every attempt (its median is 5 to 11 ms out on a quiet box). Every attempt is written out, so a
/// pass that took a second try says so.
/// </remarks>
internal static class WallClockMeasurement
{
    /// <summary>Times a measurement is taken, at most, when a loaded box makes it miss its bound.</summary>
    public const int Attempts = 5;

    /// <summary>
    /// Takes <paramref name="measureAsync"/> until one result is within its bound or <see cref="Attempts"/> are used, and returns
    /// the first within it, else the one nearest to it.
    /// </summary>
    /// <param name="output">Where every attempt is written.</param>
    /// <param name="measureAsync">One measurement.</param>
    /// <param name="excess">How far a result is past its bound; below zero is within it.</param>
    /// <param name="describe">A result in words, for the output.</param>
    public static async Task<T> BestOfAsync<T>(
        ITestOutputHelper output,
        Func<Task<T>> measureAsync,
        Func<T, double> excess,
        Func<T, string> describe)
    {
        var best = await measureAsync();
        var bestExcess = excess(best);
        output.WriteLine($"attempt 1 of {Attempts}: {describe(best)}");
        for (var attempt = 2; attempt <= Attempts && bestExcess >= 0; attempt++)
        {
            var next = await measureAsync();
            var nextExcess = excess(next);
            output.WriteLine($"attempt {attempt} of {Attempts}: {describe(next)}");
            if (nextExcess < bestExcess)
            {
                best = next;
                bestExcess = nextExcess;
            }
        }

        return best;
    }
}
