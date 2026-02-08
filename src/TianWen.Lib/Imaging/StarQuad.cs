using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace TianWen.Lib.Imaging;

public readonly record struct StarQuad(float Dist1, float Dist2, float Dist3, float Dist4, float Dist5, float Dist6, float X, float Y) : IComparable<StarQuad>
{
    public int CompareTo(StarQuad other) => Dist1.CompareTo(other.Dist1);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool WithinTolerance(in StarQuad other, float tolerance) =>
        MathF.Abs(Dist1 - other.Dist1) <= tolerance &&
        MathF.Abs(Dist2 - other.Dist2) <= tolerance &&
        MathF.Abs(Dist3 - other.Dist3) <= tolerance &&
        MathF.Abs(Dist4 - other.Dist4) <= tolerance &&
        MathF.Abs(Dist5 - other.Dist5) <= tolerance &&
        MathF.Abs(Dist6 - other.Dist6) <= tolerance;

    const float ErrorDiv = 1/6f;
    public float Error(in StarQuad other) => ErrorDiv * (
        MathF.Abs(Dist1 - other.Dist1) +
        MathF.Abs(Dist2 - other.Dist2) +
        MathF.Abs(Dist3 - other.Dist3) +
        MathF.Abs(Dist4 - other.Dist4) +
        MathF.Abs(Dist5 - other.Dist5) +
        MathF.Abs(Dist6 - other.Dist6)
    );
}