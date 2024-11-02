using System;
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
    private IReadOnlyList<IPlateSolver> Solvers { get; } = solvers.Where(solver => solver.GetType() != typeof(PlateSolverFactory)).ToList();

    private IPlateSolver? _selected;

    public IPlateSolver? SelectedPlateSolver => Interlocked.CompareExchange(ref _selected, null, null);

    public string Name => throw new System.NotImplementedException();

    public float Priority => throw new System.NotImplementedException();

    public async Task<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedPlateSolver is not null)
        {
            return true;
        }

        var count = Solvers.Count;
        var checkSupportTasks = new Task<bool>[count];
        for (var i = 0; i < count; i++)
        {
            checkSupportTasks[i] = Solvers[i].CheckSupportAsync(cancellationToken);
        }

        var results = await Task.WhenAll(checkSupportTasks);

        var supported = new List<IPlateSolver>();

        for (var i = 0; i < count; i++)
        {
            if (results[i])
            {
                supported.Add(Solvers[i]);
            }
        }

        var selected = supported.OrderByDescending(p => p.Priority).FirstOrDefault();
        if (selected is not null)
        {
            Interlocked.CompareExchange(ref _selected, selected, null);

            return true;
        }
        else
        {
            return false;
        }
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
