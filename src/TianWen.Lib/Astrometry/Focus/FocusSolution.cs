using System;

namespace TianWen.Lib.Astrometry.Focus;

public readonly record struct FocusSolution(double BestFocus, double A, double B, double Error, int Iterations)
{
    public double DistFromCenter(int focusPosition) => Math.Abs(BestFocus - focusPosition);
}
