using System;

namespace Astap.Lib.Astrometry.VSOP87;

internal static partial class Mercury
{
    internal static void GetBody3d(double t, Span<double> temp)
    {
        temp[0] = GetX(t);
        temp[1] = GetY(t);
        temp[2] = GetZ(t);
    }
}
