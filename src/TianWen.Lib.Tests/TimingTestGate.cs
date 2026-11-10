using System;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Where a wall-clock latency bar runs: only when asked, on a quiet machine, never on CI.
/// </summary>
/// <remarks>
/// <para>A bar on how LATE a real timer ends ("the median wait is under 2.5 ms past the asked time") measures the scheduler as
/// much as the code: on a 16-core desktop with a dozen builds running, four such tests failed whole-suite runs and passed alone
/// straight after (#2031), and they went on failing other flights' checks. The bar is not what is wrong, so none is widened.
/// What the code DOES (which wait primitive it arms, the order of its events) is pinned deterministically in the normal suite
/// (<c>HighResolutionWaitTests</c>, the DAL camera's pulse test); a latency bar stays, as the measurement of how well that
/// primitive performs, and runs when <c>TIANWEN_TIMING_TESTS=1</c> says the machine is quiet enough to believe it.</para>
/// <para>Such a test is <c>[Trait("Category", "Timing")]</c> and its class is in <see cref="TimingCollection"/>, which runs with
/// nothing else in parallel, so an opted-in run measures alone.</para>
/// </remarks>
internal static class TimingTestGate
{
    /// <summary>The environment variable that opts a run into the wall-clock latency bars.</summary>
    public const string Variable = "TIANWEN_TIMING_TESTS";

    /// <summary>Skips the calling test unless the run opted in, and always on CI.</summary>
    public static void SkipUnlessOptedIn()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(Variable), "1", StringComparison.Ordinal),
            $"A wall-clock latency bar is opt-in: set {Variable}=1 and run it on a quiet machine, since a loaded one moves the clock whatever the code does (#2031).");
        Assert.SkipWhen(
            string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
            $"A wall-clock latency bar never runs on CI, whatever {Variable} says: a shared runner measures the runner (#2031).");
    }
}
