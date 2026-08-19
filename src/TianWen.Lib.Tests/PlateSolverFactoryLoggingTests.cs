using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A plate solve that fails must leave a record, at a level the default filter admits.
/// </summary>
/// <remarks>
/// <para>It did not. <c>PlateSolverFactory</c> logged the ATTEMPT at Information and every per-solver
/// outcome -- "returned no solution", "failed" -- at Debug, then threw; the viewer caught that into a
/// transient status string and the GUI into an in-memory notification feed. So a SUCCESSFUL solve was
/// on the record and a failed one left nothing, which is backwards: failure is when the reason is
/// wanted, and nobody enables Debug before the thing they are about to debug fails.</para>
/// <para>Fakes rather than the real solver chain, deliberately. The contract under test is "when they
/// all fail, say so once and name them", and a real chain on a real frame cannot express that
/// reliably -- on the frame that motivated this, ASTAP now succeeds where the catalog solver does
/// not, so the all-failed path never runs.</para>
/// </remarks>
[Collection("Imaging")]
public class PlateSolverFactoryLoggingTests
{
    private sealed class StubSolver(string name, float priority, Exception? throws) : IPlateSolver
    {
        public string Name => name;

        public float Priority => priority;

        public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);

        public Task<PlateSolveResult> SolveFileAsync(string fitsFile, ImageDim? imageDim = null,
            float range = IPlateSolver.DefaultRange, WCS? searchOrigin = null, double? searchRadius = null,
            CancellationToken cancellationToken = default)
            => throws is not null
                ? Task.FromException<PlateSolveResult>(throws)
                : Task.FromResult(new PlateSolveResult(null, TimeSpan.Zero));
    }

    private sealed class Capturing(List<(LogLevel Level, string Message)> sink) : ILogger<PlateSolverFactory>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                sink.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    [Fact]
    public async Task WhenEverySolverFailsOneWarningNamesThemAll()
    {
        var log = new List<(LogLevel Level, string Message)>();
        var factory = new PlateSolverFactory(
            [
                new StubSolver("Catalog plate solver", 0.99f, throws: null),
                new StubSolver("ASTAP Plate Solver", 0.95f, new PlateSolverException("cannot read .fz")),
            ],
            new Capturing(log));

        await Should.ThrowAsync<PlateSolverException>(
            () => factory.SolveFileAsync("frame.fz", cancellationToken: TestContext.Current.CancellationToken));

        // Level matters as much as presence: this is what the viewer ran at, so a Debug-only record
        // would be exactly as absent as before.
        var warnings = log.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Message).ToArray();
        warnings.Length.ShouldBe(1, "one summary, not one line per solver");

        var summary = warnings[0];
        summary.ShouldContain("No plate solver could solve");
        // Naming each solver AND what it said is what makes the line actionable rather than merely
        // present -- "could not solve" alone does not distinguish a starless frame from a file the
        // tool could not open, which were the two real causes on the frame that prompted this.
        summary.ShouldContain("Catalog plate solver");
        summary.ShouldContain("no solution");
        summary.ShouldContain("ASTAP Plate Solver");
        summary.ShouldContain("cannot read .fz");
    }

    [Fact]
    public async Task ASuccessfulSolveLogsNoWarning()
    {
        var log = new List<(LogLevel Level, string Message)>();
        var factory = new PlateSolverFactory(
            [new StubSolver("Failing solver", 0.99f, throws: null), new SolvingStub()],
            new Capturing(log));

        var result = await factory.SolveFileAsync("frame.fits",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Solution.ShouldNotBeNull();
        // The summary is for failure only; a solver chain that recovers on its second attempt is a
        // normal outcome and must not cry wolf in the log.
        log.ShouldNotContain(e => e.Level >= LogLevel.Warning);
    }

    private sealed class SolvingStub : IPlateSolver
    {
        public string Name => "Working solver";

        public float Priority => 0.5f;

        public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);

        public Task<PlateSolveResult> SolveFileAsync(string fitsFile, ImageDim? imageDim = null,
            float range = IPlateSolver.DefaultRange, WCS? searchOrigin = null, double? searchRadius = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new PlateSolveResult(new WCS(1.0, 2.0), TimeSpan.FromMilliseconds(1)));
    }
}
