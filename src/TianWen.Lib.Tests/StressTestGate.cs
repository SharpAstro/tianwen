using System;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Where a thread stress test runs: on a developer's machine, never on CI.
/// </summary>
/// <remarks>
/// A test that hammers an object from several threads for a stretch of wall-clock time measures the box it runs on as much
/// as the code. On a shared 4-core runner its loops held the thread pool while the multicore planetary tests needed it, and
/// the whole test process stalled until the five-minute hang dump (#1385, five legs in one night). It proves what it proves
/// on a machine whose load is known, so CI (GitHub Actions sets <c>CI=true</c>) skips it. A deterministic test of the same
/// code, one that reads a known interleaving rather than racing for one, belongs on CI as any other.
/// </remarks>
internal static class StressTestGate
{
    /// <summary>Skips the calling test on CI.</summary>
    public static void SkipOnCi()
        => Assert.SkipWhen(
            string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
            "A thread stress test runs off CI: on a shared runner it measures the runner and starves the other tests (#1385).");
}
