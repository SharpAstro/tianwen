using Shouldly;
using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing.PolarAlignment;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A rung of the polar-alignment exposure ramp ends at its budget on the injected clock. The source never delivers a frame, the
/// rung's budget (5 x the exposure + 5 s) is waited on in fake time, and the fake clock is advanced past it ONCE, as soon as the
/// budget's timer exists: on the wall clock the rung would hold until the budget ran out in real seconds, and the
/// test would instead wait for a fake timer that is never made, and end at its own timeout.
/// </summary>
public class AdaptiveExposureRampDeadlineTests
{
    [Fact(Timeout = 30_000)]
    public async Task ARungWhoseFrameNeverComesEndsAtItsBudgetAndTheRampMovesOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProviderWrapper();
        var ramp = ImmutableArray.Create(TimeSpan.FromMilliseconds(100));
        var source = new StallingCaptureSource();

        var probe = AdaptiveExposureRamp.ProbeAsync(source, new NullSolver(), ramp, minStarsMatched: 15, clock, ct).AsTask();
        await source.Started.WaitAsync(ct);
        // The budget of a 100 ms rung is 5.5 s.
        await clock.WaitForTimerDueAfterAsync(TimeSpan.FromSeconds(5), ct);
        probe.IsCompleted.ShouldBeFalse("no frame has come and the clock has not moved");
        clock.Advance(TimeSpan.FromSeconds(6));

        var result = await probe;
        result.Success.ShouldBeFalse();
        result.FailureReason.ShouldNotBeNull().ShouldContain("timed out");
        result.ExposureUsed.ShouldBe(TimeSpan.FromMilliseconds(100));
        source.SawCancellation.ShouldBeTrue("the rung's own token ended the capture, the caller's did not");
        ct.IsCancellationRequested.ShouldBeFalse();
    }

    /// <summary>A source whose capture starts and then waits for ever, for as long as its token lives.</summary>
    private sealed class StallingCaptureSource : ICaptureSource
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public bool SawCancellation { get; private set; }

        public string DisplayName => "Stalling";
        public double FocalLengthMm => 200;
        public double ApertureMm => 50;
        public double PixelSizeMicrons => 3.0;

        public ValueTask<CaptureResult> CaptureAsync(TimeSpan exposure, CancellationToken ct = default)
            => throw new NotSupportedException("the ramp only captures and solves");

        public async ValueTask<CaptureAndSolveResult> CaptureAndSolveAsync(TimeSpan exposure, IPlateSolver solver, CancellationToken ct = default)
        {
            _started.TrySetResult();
            try
            {
                await new TaskCompletionSource().Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }

            throw new InvalidOperationException("unreachable: nothing completes the wait");
        }
    }

    private sealed class NullSolver : IPlateSolver
    {
        public string Name => "none";

        public float Priority => 0f;

        public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

        public Task<PlateSolveResult> SolveFileAsync(string fitsFile, ImageDim? imageDim = null, float range = IPlateSolver.DefaultRange,
            WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
