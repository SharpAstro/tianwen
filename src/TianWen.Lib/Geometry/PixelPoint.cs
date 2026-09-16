namespace TianWen.Lib.Geometry;

/// <summary>
/// A single pixel position, 0-based, origin at the raster's top-left with Y growing downwards.
/// The companion to <see cref="PixelRect"/>, replacing the framework drawing point for the same
/// reason; see that type for the rationale.
/// </summary>
/// <remarks>
/// Integer on purpose. Everything using this addresses a photosite or a tile origin, which are whole
/// pixels; sub-pixel work in this codebase carries its own <c>float</c>/<c>double</c> pairs (star
/// centroids, warp offsets) and must not be routed through here, because rounding a centroid into a
/// whole pixel is a measurement change disguised as a type conversion.
/// </remarks>
/// <param name="X">Column, 0-based.</param>
/// <param name="Y">Row, 0-based.</param>
public readonly record struct PixelPoint(int X, int Y)
{
    /// <summary>The origin.</summary>
    public static PixelPoint Empty { get; } = new PixelPoint(0, 0);

    /// <summary>This position translated by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    public PixelPoint Offset(int dx, int dy) => new PixelPoint(X + dx, Y + dy);

    public override string ToString() => $"({X},{Y})";
}
