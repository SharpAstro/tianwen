using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Why this type exists rather than <see cref="Progress{T}"/>, pinned deterministically.
    /// </summary>
    /// <remarks>
    /// The bug it fixes is an ORDERING one, and ordering bugs cannot be pinned by the code that suffers
    /// them: the full suite caught the viewer's enhance leaving "Enhancing: gradient-correction (0%)" on
    /// the status line of a finished run exactly once in 5,782 tests, because a queued report landed
    /// after the terminal message. So the assertion is on the mechanism -- a report has already run by
    /// the time Report returns -- which is true or false on every run, on any machine.
    /// </remarks>
    public class SynchronousProgressTests
    {
        [Fact]
        public void AReportHasAlreadyRunByTheTimeReportReturns()
        {
            var seen = new List<int>();
            IProgress<int> progress = new SynchronousProgress<int>(seen.Add);

            progress.Report(1);
            seen.ShouldBe([1], "inline, on the calling thread -- no queue to drain");

            progress.Report(2);
            seen.ShouldBe([1, 2]);
        }

        /// <summary>
        /// The property the viewer depends on: a report issued during a run cannot overtake the state
        /// written after it. With <see cref="Progress{T}"/> this is a race; here it is arithmetic.
        /// </summary>
        [Fact]
        public async Task TheCallersFinalWriteWins()
        {
            var status = "";
            IProgress<string> progress = new SynchronousProgress<string>(s => status = $"running: {s}");

            await Task.Run(() =>
            {
                progress.Report("step 1");
                progress.Report("step 2");
            });
            status = "done";

            status.ShouldBe("done");
        }

        [Fact]
        public void ASinkIsRequired()
            => Should.Throw<ArgumentNullException>(() => new SynchronousProgress<int>(null!));
    }
}
