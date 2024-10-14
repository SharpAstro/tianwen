﻿using Astap.Lib.Astrometry.PlateSolve;
using Astap.Lib.Imaging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Astap.Lib.Tests.Fakes;

internal class FakePlateSolver : IPlateSolver
{
    public string Name => throw new NotImplementedException();

    public float Priority => throw new NotImplementedException();

    public Task<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<WCS?> SolveFileAsync(string fitsFile, ImageDim? imageDim = null, float range = 0.03F, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
    {
        if (searchOrigin is not null)
        {
            return Task.FromResult(searchOrigin);
        }

        if (Image.TryReadFitsFile(fitsFile, out var image))
        {
            // TODO: Read WCS from FITS file
        }

        return Task.FromResult(null as WCS?);
    }
}