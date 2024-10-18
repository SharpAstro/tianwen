using System;

namespace TianWen.Lib.Astrometry.VSOP87;

internal static partial class Uranus
{
    internal static void GetBody3d(double t, Span<double> temp)
    {
        temp[0] = GetX(t);
        temp[1] = GetY(t);
        temp[2] = GetZ(t);
    }
}
