namespace TianWen.Lib.Imaging;

/// <summary>
/// What one channel's sky looks like (<see cref="Image.SkySurface"/>), in the channel's own counts.
/// </summary>
/// <param name="Level">The fitted sky surface at the frame centre.</param>
/// <param name="GradientStrength">The magnitude of the surface's linear terms, the change across one frame width or height.</param>
/// <param name="GradientAngleDeg">The pixel-frame direction the sky brightens in: 0 along +X (columns), 90 along +Y (rows,
/// down a top-down frame). NaN for a flat sky.</param>
/// <param name="Patchiness">The tile medians' median absolute deviation about the fitted surface.</param>
public readonly record struct SkySurface(float Level, float GradientStrength, float GradientAngleDeg, float Patchiness)
{
    /// <summary>Nothing known: a frame too small to fit.</summary>
    public static SkySurface Unknown => new(float.NaN, float.NaN, float.NaN, float.NaN);
}
