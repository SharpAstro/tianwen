using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace TianWen.Lib.Imaging;

public readonly record struct StarQuad(float Dist1, float Dist2, float Dist3, float Dist4, float Dist5, float Dist6, float X, float Y) : IComparable<StarQuad>
{
    public readonly int CompareTo(StarQuad other) => Dist1.CompareTo(other.Dist1);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public readonly bool WithinTolerance(in StarQuad other, float tolerance) =>
        MathF.Abs(Dist1 - other.Dist1) <= tolerance &&
        MathF.Abs(Dist2 - other.Dist2) <= tolerance &&
        MathF.Abs(Dist3 - other.Dist3) <= tolerance &&
        MathF.Abs(Dist4 - other.Dist4) <= tolerance &&
        MathF.Abs(Dist5 - other.Dist5) <= tolerance &&
        MathF.Abs(Dist6 - other.Dist6) <= tolerance;

    /// <summary>
    /// The five SCALE-FREE distances only, for matching two star fields whose plate scales are not
    /// already known to agree.
    /// </summary>
    /// <remarks>
    /// <para><see cref="WithinTolerance"/> also tests <see cref="Dist1"/>, which is the longest side in
    /// ABSOLUTE PIXELS while Dist2..Dist6 are dimensionless ratios -- one tolerance across two units.
    /// That is right for stacking, where both frames come off the same camera and Dist1 genuinely
    /// should agree to a fraction of a pixel, and wrong for matching an image against a catalog, where
    /// the projected Dist1 depends on the very plate scale being recovered.</para>
    /// <para>Measured: with an image and a catalog field projected through the frame's own solution --
    /// so sharing a pixel frame exactly -- 15.8% of quads coincide by centre, and
    /// <c>StarReferenceTable.FindFit</c> locked 0 of 24 panels anyway. The ratios were never the
    /// problem.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public readonly bool RatiosWithinTolerance(in StarQuad other, float tolerance) =>
        MathF.Abs(Dist2 - other.Dist2) <= tolerance &&
        MathF.Abs(Dist3 - other.Dist3) <= tolerance &&
        MathF.Abs(Dist4 - other.Dist4) <= tolerance &&
        MathF.Abs(Dist5 - other.Dist5) <= tolerance &&
        MathF.Abs(Dist6 - other.Dist6) <= tolerance;

    const float ErrorDiv = 1/6f;
    public readonly float Error(in StarQuad other) => ErrorDiv * (
        MathF.Abs(Dist1 - other.Dist1) +
        MathF.Abs(Dist2 - other.Dist2) +
        MathF.Abs(Dist3 - other.Dist3) +
        MathF.Abs(Dist4 - other.Dist4) +
        MathF.Abs(Dist5 - other.Dist5) +
        MathF.Abs(Dist6 - other.Dist6)
    );
}