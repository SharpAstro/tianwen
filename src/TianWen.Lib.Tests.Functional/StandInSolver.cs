using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A plate solver standing in for a real one in the node's tests: it answers at once with a fixed solution or with no
/// match, or it never answers until cancelled. What those tests are about is the node's plumbing, and a real blind solve
/// of the fake camera's random field runs for minutes.
/// </summary>
internal sealed class StandInSolver : IPlateSolverFactory
{
    private readonly WCS? _answer;
    private readonly bool _answers;
    private int _solves;

    private StandInSolver(WCS? answer, bool answers)
    {
        _answer = answer;
        _answers = answers;
    }

    /// <summary>Answers every solve at once with <paramref name="answer"/>, or with no match when null.</summary>
    public static StandInSolver Answering(WCS? answer) => new StandInSolver(answer, answers: true);

    /// <summary>Takes every solve and never answers it: a run stays where it is until it is cancelled.</summary>
    public static StandInSolver NeverAnswering() => new StandInSolver(null, answers: false);

    /// <summary>How many solves it was asked for.</summary>
    public int Solves => Volatile.Read(ref _solves);

    public string Name => "stand-in";

    public float Priority => 0;

    public IPlateSolver? SelectedPlateSolver => this;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

    public Task<PlateSolveResult> SolveFileAsync(string fitsFile, ImageDim? imageDim = default, float range = 0.03f, WCS? searchOrigin = default,
        double? searchRadius = default, CancellationToken cancellationToken = default)
        => SolveAsync(cancellationToken);

    Task<PlateSolveResult> IPlateSolver.SolveImageAsync(Image image, ImageDim? imageDim, float range, WCS? searchOrigin, double? searchRadius,
        CancellationToken cancellationToken)
        => SolveAsync(cancellationToken);

    private async Task<PlateSolveResult> SolveAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _solves);
        if (!_answers)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        return new PlateSolveResult(_answer, TimeSpan.FromMilliseconds(5));
    }
}
