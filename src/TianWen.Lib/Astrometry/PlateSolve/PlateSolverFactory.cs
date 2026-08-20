using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Astrometry.PlateSolve;

public interface IPlateSolverFactory : IPlateSolver
{
    IPlateSolver? SelectedPlateSolver { get; }
}

internal sealed class PlateSolverFactory(IEnumerable<IPlateSolver> solvers, ILogger<PlateSolverFactory> logger) : IPlateSolverFactory
{
    private readonly SemaphoreSlim _initSem = new SemaphoreSlim(1, 1);

    private IPlateSolver[]? _sortedSolvers;

    public IPlateSolver? SelectedPlateSolver => Interlocked.CompareExchange(ref _sortedSolvers, null, null) is { Length: > 0 } s ? s[0] : null;

    public string Name => SelectedPlateSolver?.Name ?? throw new InvalidOperationException("No plate solver selected");

    public float Priority => SelectedPlateSolver?.Priority ?? throw new InvalidOperationException("No plate solver selected");

    public async ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
    {
        if (_sortedSolvers is { Length: > 0 })
        {
            return true;
        }

        using var @lock = await _initSem.AcquireLockAsync(cancellationToken);

        // double check after lock acquisition
        if (_sortedSolvers is { Length: > 0 })
        {
            return true;
        }

        var supportedSolvers = new ConcurrentBag<IPlateSolver>();

        await Parallel.ForEachAsync(solvers, cancellationToken, async (solver, cancellationToken) =>
        {
            if (solver.GetType() is { IsSealed: true } type && type == typeof(PlateSolverFactory))
            {
                return;
            }

            if (await solver.CheckSupportAsync(cancellationToken))
            {
                supportedSolvers.Add(solver);
            }
        });

        _ = Interlocked.Exchange(ref _sortedSolvers, supportedSolvers.OrderByDescending(solver => solver.Priority).ToArray());

        return _sortedSolvers.Length > 0;
    }

    public async Task<PlateSolveResult> SolveFileAsync(string fitsFile, ImageDim? imageDim = null, float range = 0.03F, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("PlateSolve file: {File}, imageDim={ImageDim}, searchOrigin={SearchOrigin}, searchRadius={SearchRadius}",
            fitsFile, imageDim, searchOrigin, searchRadius);

        var attempts = new List<string>();
        foreach (var solver in await EnsureSolversAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                logger.LogDebug("Trying solver: {SolverName} (priority={Priority})", solver.Name, solver.Priority);
                var result = await solver.SolveFileAsync(fitsFile, imageDim, range, searchOrigin, searchRadius, cancellationToken);
                if (result.Solution is not null)
                {
                    logger.LogInformation("Solved by {SolverName} in {Elapsed}ms: RA={RA:F4}h Dec={Dec:F2}°",
                        solver.Name, result.Elapsed.TotalMilliseconds, result.Solution.Value.CenterRA, result.Solution.Value.CenterDec);
                    return result;
                }
                else
                {
                    attempts.Add($"{solver.Name}: no solution");
                    logger.LogDebug("Solver {SolverName} returned no solution", solver.Name);
                }
            }
            // Catch anything a solver can throw, not just PlateSolverException. The point of this loop
            // is fallback, and it only delivers that if ONE solver's failure cannot end it: an
            // IOException out of a FITS parse used to escape here and abort the chain, so a later
            // solver that could have handled the file was never asked. A cancellation is different --
            // it is the caller's decision, not a solver's failure -- so it still propagates.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                attempts.Add($"{solver.Name}: {Summarise(ex.Message)}");
                logger.LogDebug(ex, "Solver {SolverName} failed", solver.Name);
            }
        }

        // ONE warning naming every solver and what it said. The per-solver lines above are Debug, which
        // is right for detail, but it left a failed solve with no record at all at the default level --
        // while a SUCCESSFUL one logged at Information. That asymmetry is backwards: failure is exactly
        // when the reason is wanted, and a status string in the UI is gone by the time anyone looks.
        logger.LogWarning("No plate solver could solve the image. Tried: {Attempts}", string.Join(" | ", attempts));
        throw new PlateSolverException("No plate solver could solve the image");
    }

    public async Task<PlateSolveResult> SolveImageAsync(Image image, ImageDim? imageDim = null, float range = 0.03F, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("PlateSolve image: {Width}x{Height}, imageDim={ImageDim}, searchOrigin={SearchOrigin}, searchRadius={SearchRadius}",
            image.Width, image.Height, imageDim, searchOrigin, searchRadius);

        var attempts = new List<string>();
        foreach (var solver in await EnsureSolversAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                logger.LogDebug("Trying solver: {SolverName} (priority={Priority})", solver.Name, solver.Priority);
                var result = await solver.SolveImageAsync(image, imageDim, range, searchOrigin, searchRadius, cancellationToken);
                if (result.Solution is not null)
                {
                    logger.LogInformation("Solved by {SolverName} in {Elapsed}ms: RA={RA:F4}h Dec={Dec:F2}°",
                        solver.Name, result.Elapsed.TotalMilliseconds, result.Solution.Value.CenterRA, result.Solution.Value.CenterDec);
                    return result;
                }
                else
                {
                    attempts.Add($"{solver.Name}: no solution");
                    logger.LogDebug("Solver {SolverName} returned no solution", solver.Name);
                }
            }
            // Catch anything a solver can throw, not just PlateSolverException. The point of this loop
            // is fallback, and it only delivers that if ONE solver's failure cannot end it: an
            // IOException out of a FITS parse used to escape here and abort the chain, so a later
            // solver that could have handled the file was never asked. A cancellation is different --
            // it is the caller's decision, not a solver's failure -- so it still propagates.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                attempts.Add($"{solver.Name}: {Summarise(ex.Message)}");
                logger.LogDebug(ex, "Solver {SolverName} failed", solver.Name);
            }
        }

        // ONE warning naming every solver and what it said. The per-solver lines above are Debug, which
        // is right for detail, but it left a failed solve with no record at all at the default level --
        // while a SUCCESSFUL one logged at Information. That asymmetry is backwards: failure is exactly
        // when the reason is wanted, and a status string in the UI is gone by the time anyone looks.
        logger.LogWarning("No plate solver could solve the image. Tried: {Attempts}", string.Join(" | ", attempts));
        throw new PlateSolverException("No plate solver could solve the image");
    }

    /// <summary>Keeps one solver's failure readable in a joined summary line.</summary>
    private static string Summarise(string message)
    {
        var firstLine = message.AsSpan();
        var newline = firstLine.IndexOfAny('\r', '\n');
        if (newline >= 0)
        {
            firstLine = firstLine[..newline];
        }
        return firstLine.Length > 200 ? string.Concat(firstLine[..200], "...") : firstLine.ToString();
    }

    private async ValueTask<IPlateSolver[]> EnsureSolversAsync(CancellationToken cancellationToken)
    {
        if (_sortedSolvers is not { Length: > 0 })
        {
            await CheckSupportAsync(cancellationToken).ConfigureAwait(false);
        }

        return Interlocked.CompareExchange(ref _sortedSolvers, null, null)
            ?? throw new InvalidOperationException("No plate solver supported");
    }
}
