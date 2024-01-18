using Astap.Lib.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using static Astap.Lib.CollectionHelper;

namespace Astap.Lib.Astrometry.PlateSolve;

public class CombinedPlateSolver(IReadOnlyList<IPlateSolver> solvers) : IPlateSolver
{
    public CombinedPlateSolver(IPlateSolver plateSolver, params IPlateSolver[] other)
        : this(ConcatToReadOnlyList(plateSolver, other))
    {
        // calls primary constructor
    }

    private IReadOnlyList<IPlateSolver> Solvers { get; } = solvers;

    private IPlateSolver? Selected { get; set; }

    public string Name => Selected?.Name ?? $"Combined plate solver ({Solvers.Count})";

    public float Priority => Selected?.Priority ?? 0;

    public async Task<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
    {
        if (Selected is not null)
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

        return (Selected = supported.OrderByDescending(p => p.Priority).FirstOrDefault()) is not null;
    }

    /// <inheritdoc/>
    public async Task<WCS?> SolveFileAsync(string fitsFile, ImageDim? imageDim = null, float range = 0.03F, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
    {
        if (Selected is { } selected)
        {
            return await selected.SolveFileAsync(fitsFile, imageDim, range, searchOrigin, searchRadius, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException("Need to call CheckSupportAsync first");
        }
    }
}
