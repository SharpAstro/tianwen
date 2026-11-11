using System;
using System.Threading;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The one way a line reaches a test's output from code that can outlive the test: a reader thread, a logger, a watch, a wait.
/// xunit abandons a test that outran its <c>Timeout</c> with its body still running and closes its output under it, and a write
/// after that throws "There is no currently active test" on the WRITER's thread, which on a thread of its own ends the whole
/// test host (StallWatch lost 5 of 13 tests to it). xunit offers no way to ask whether the output is open.
/// </summary>
internal static class TestOutputLine
{
    /// <summary>
    /// Writes <paramref name="line"/> to <paramref name="output"/> while the test runs. A line the test can no longer take (its
    /// token cancelled, which is how xunit ends a test that timed out, or the output refusing it) goes to the runner's diagnostic
    /// messages instead, never thrown. True when the test's output took it.
    /// A diagnostic message reaches no console under Microsoft Testing Platform: it is in the run's <c>.diag</c> log, which only a
    /// run with <c>--diagnostic --xunit-diagnostics on</c> writes (measured 2026-11-11, a line written from a thread a second after
    /// its test ended included). CI passes neither today, so there the line is kept from throwing and goes no further.
    /// </summary>
    public static bool Write(ITestOutputHelper? output, string line, CancellationToken ct = default)
    {
        if (output is not null && !ct.IsCancellationRequested)
        {
            try
            {
                output.WriteLine(line);
                return true;
            }
            catch (InvalidOperationException)
            {
                // The test ended between the check and the write (or on a thread with no token to check): the line goes below.
            }
        }

        TestContext.Current.SendDiagnosticMessage("after its test ended: " + line);
        return false;
    }
}
