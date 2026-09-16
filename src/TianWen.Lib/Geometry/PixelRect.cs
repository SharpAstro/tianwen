namespace TianWen.Lib.Geometry;

/// <summary>
/// An axis-aligned rectangle of PIXELS, 0-based, with the origin at the raster's top-left and Y
/// growing downwards.
/// </summary>
/// <remarks>
/// <para>This is the codebase's one pixel rectangle, replacing the framework drawing type that used
/// to be imported into 67 files. That type is portable in itself (its assembly is part of the shared
/// framework, unlike the GDI+ one), but the name carries a drawing and Windows association this
/// codebase does not want in its imaging and device layers, and <c>Devices</c> had already kept it
/// out entirely in favour of its own <see cref="Devices.RoiRect"/>.</para>
/// <para>It lives in its own namespace rather than in <c>Imaging</c> so a device driver can state a
/// sensor's geometry without taking a dependency on the imaging layer to do it.</para>
/// <para><b>Width and Height are extents, not bounds.</b> <see cref="Right"/> and
/// <see cref="Bottom"/> are therefore EXCLUSIVE, exactly as the type it replaces defines them, so a
/// loop is <c>for (var y = r.Top; y &lt; r.Bottom; y++)</c>. Do not read them as the last row or
/// column; the FITS section cards are the place where an inclusive convention lives, and
/// <c>FitsSection</c> is the single point of conversion for it.</para>
/// </remarks>
/// <param name="X">Left edge, inclusive.</param>
/// <param name="Y">Top edge, inclusive.</param>
/// <param name="Width">Horizontal extent in pixels.</param>
/// <param name="Height">Vertical extent in pixels.</param>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>The empty rectangle, at the origin with no extent.</summary>
    public static PixelRect Empty { get; } = new PixelRect(0, 0, 0, 0);

    /// <summary>Left edge, inclusive. Same as <see cref="X"/>.</summary>
    public int Left => X;

    /// <summary>Top edge, inclusive. Same as <see cref="Y"/>.</summary>
    public int Top => Y;

    /// <summary>Right edge, EXCLUSIVE: the first column past the rectangle.</summary>
    public int Right => X + Width;

    /// <summary>Bottom edge, EXCLUSIVE: the first row past the rectangle.</summary>
    public int Bottom => Y + Height;

    /// <summary>
    /// True when the rectangle covers no pixels. A negative extent counts as empty, so a subtraction
    /// that overshoots degrades to "nothing" rather than to a rectangle that reads as inside out.
    /// </summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Whether the pixel at (<paramref name="x"/>, <paramref name="y"/>) lies inside.</summary>
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>Whether <paramref name="point"/> lies inside.</summary>
    public bool Contains(PixelPoint point) => Contains(point.X, point.Y);

    /// <summary>Whether <paramref name="other"/> lies wholly inside this rectangle.</summary>
    public bool Contains(PixelRect other)
        => other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    /// <summary>This rectangle translated by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    /// <remarks>
    /// Returns a new value rather than mutating in place, unlike the mutating <c>Offset</c> it
    /// replaces: this is a readonly struct, so an in-place version would silently discard the change
    /// on any copy.
    /// </remarks>
    public PixelRect Offset(int dx, int dy) => new PixelRect(X + dx, Y + dy, Width, Height);

    /// <summary>The overlap of two rectangles, or <see cref="Empty"/> when they do not overlap.</summary>
    public static PixelRect Intersect(PixelRect a, PixelRect b)
    {
        var x = System.Math.Max(a.X, b.X);
        var y = System.Math.Max(a.Y, b.Y);
        var right = System.Math.Min(a.Right, b.Right);
        var bottom = System.Math.Min(a.Bottom, b.Bottom);
        return right > x && bottom > y ? new PixelRect(x, y, right - x, bottom - y) : Empty;
    }

    /// <summary>Whether the two rectangles share at least one pixel.</summary>
    public bool IntersectsWith(PixelRect other)
        => other.X < Right && X < other.Right && other.Y < Bottom && Y < other.Bottom;

    /// <summary>
    /// Builds a rectangle from edges, where <paramref name="right"/> and <paramref name="bottom"/>
    /// are EXCLUSIVE.
    /// </summary>
    public static PixelRect FromLTRB(int left, int top, int right, int bottom)
        => new PixelRect(left, top, right - left, bottom - top);

    public override string ToString() => $"[{X},{Y} {Width}x{Height}]";
}
