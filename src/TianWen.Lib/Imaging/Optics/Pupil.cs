using System;

namespace TianWen.Lib.Imaging.Optics;

/// <summary>
/// A telescope's entrance pupil: a clear circle with a central obstruction and, on a Newtonian, the vanes of the spider that
/// holds its secondary. It is rasterised by area, so a vane narrower than a sample dims the samples it crosses instead of
/// vanishing between them or blocking whole ones.
/// </summary>
/// <param name="DiameterM">The clear aperture.</param>
/// <param name="ObstructionRatio">The central obstruction's diameter over the aperture's.</param>
/// <param name="Vanes">How many spider vanes run out from the obstruction to the rim, evenly spaced (0 for none).</param>
/// <param name="VaneWidthM">Each vane's width.</param>
/// <param name="VaneAngleDeg">The first vane's direction, from the grid's +x axis toward +y.</param>
public readonly record struct Pupil(double DiameterM, double ObstructionRatio = 0, int Vanes = 0, double VaneWidthM = 0, double VaneAngleDeg = 0)
{
    /// <summary>
    /// The transmission on an <paramref name="n"/> by <paramref name="n"/> grid of samples <paramref name="spacingM"/> apart,
    /// centred on sample (n/2, n/2), row-major: each sample's open fraction, from <paramref name="supersample"/> squared points
    /// across it.
    /// </summary>
    public float[] Rasterise(int n, double spacingM, int supersample = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spacingM);
        ArgumentOutOfRangeException.ThrowIfLessThan(supersample, 1);

        var outer = DiameterM / 2;
        var inner = outer * ObstructionRatio;
        var halfWidth = VaneWidthM / 2;
        Span<double> vaneCos = stackalloc double[Math.Max(Vanes, 1)];
        Span<double> vaneSin = stackalloc double[Math.Max(Vanes, 1)];
        for (var j = 0; j < Vanes; j++)
        {
            var angle = (VaneAngleDeg + (360.0 * j / Vanes)) * Math.PI / 180;
            (vaneSin[j], vaneCos[j]) = Math.SinCos(angle);
        }

        var pupil = new float[n * n];
        var weight = 1.0 / (supersample * supersample);
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var open = 0.0;
                for (var sy = 0; sy < supersample; sy++)
                {
                    var py = (y - (n / 2) + ((sy + 0.5) / supersample) - 0.5) * spacingM;
                    for (var sx = 0; sx < supersample; sx++)
                    {
                        var px = (x - (n / 2) + ((sx + 0.5) / supersample) - 0.5) * spacingM;
                        var r2 = (px * px) + (py * py);
                        if (r2 > outer * outer || r2 < inner * inner)
                        {
                            continue;
                        }
                        var blocked = false;
                        for (var j = 0; j < Vanes && !blocked; j++)
                        {
                            // A vane is a half-line from the centre: along it (t > 0) and within half its width across it.
                            var along = (px * vaneCos[j]) + (py * vaneSin[j]);
                            var across = (-px * vaneSin[j]) + (py * vaneCos[j]);
                            blocked = along > 0 && Math.Abs(across) < halfWidth;
                        }
                        if (!blocked)
                        {
                            open += weight;
                        }
                    }
                }
                pupil[(y * n) + x] = (float)open;
            }
        }
        return pupil;
    }
}
