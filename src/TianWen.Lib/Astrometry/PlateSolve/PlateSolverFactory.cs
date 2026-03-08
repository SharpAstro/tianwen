using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Astrometry.PlateSolve;

public interface IPlateSolverFactory : IPlateSolver
{
    IPlateSolver? SelectedPlateSolver { get; }
}

internal sealed class PlateSolverFactory(IEnumerable<IPlateSolver> solvers) : IPlateSolverFactory
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

    public async Task<WCS?> SolveFileAsync(string fitsFile, ImageDim? imageDim = null, float range = 0.03F, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
    {
        foreach (var solver in await EnsureSolversAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var result = await solver.SolveFileAsync(fitsFile, imageDim, range, searchOrigin, searchRadius, cancellationToken);
                if (result is not null)
                {
                    return result;
                }
            }
            catch (PlateSolverException)
            {
                // try next solver
            }
        }

        throw new PlateSolverException("No plate solver could solve the image");
    }

    public async Task<WCS?> SolveImageAsync(Image image, ImageDim? imageDim = null, float range = 0.03F, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
    {
        foreach (var solver in await EnsureSolversAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var result = await solver.SolveImageAsync(image, imageDim, range, searchOrigin, searchRadius, cancellationToken);
                if (result is not null)
                {
                    return result;
                }
            }
            catch (PlateSolverException)
            {
                // try next solver
            }
        }

        throw new PlateSolverException("No plate solver could solve the image");
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
