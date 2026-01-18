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
    // private IReadOnlyList<IPlateSolver> Solvers { get; } = solvers.Where(solver => solver.GetType() != typeof(PlateSolverFactory)).ToList();

    private readonly SemaphoreSlim _initSem = new SemaphoreSlim(1, 1);

    private IPlateSolver? _selected;

    public IPlateSolver? SelectedPlateSolver => Interlocked.CompareExchange(ref _selected, null, null);

    public string Name => _selected?.Name ?? throw new InvalidOperationException("No plate solver selected");

    public float Priority => _selected?.Priority ?? throw new InvalidOperationException("No plate solver selected");

    public async ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedPlateSolver is not null)
        {
            return true;
        }

        using var @lock = await _initSem.AcquireLockAsync(cancellationToken);

        // double check after lock acquisition
        if (SelectedPlateSolver is not null)
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

        // TODO? consider making IPlateSolver disposable
        _ = Interlocked.Exchange(ref _selected, supportedSolvers.OrderByDescending(solver => solver.Priority).FirstOrDefault());

        return SelectedPlateSolver is not null;
    }

    public async Task<WCS?> SolveFileAsync(string fitsFile, ImageDim? imageDim = null, float range = 0.03F, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
    {
        if (SelectedPlateSolver is null)
        {
            await CheckSupportAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Interlocked.CompareExchange(ref _selected, null, null) is { } selected)
        {
            return await selected.SolveFileAsync(fitsFile, imageDim, range, searchOrigin, searchRadius, cancellationToken);
        }
        else
        {
            throw new InvalidOperationException("No plate solver supported");
        }
    }
}
