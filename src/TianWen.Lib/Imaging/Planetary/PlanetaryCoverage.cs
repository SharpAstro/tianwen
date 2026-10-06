using System;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// How much of a stack's frame weight reached each pixel of its output grid, tallied from each frame's footprint as its shift moves it
/// (#1300): what a crop to where the frames reached reads (<see cref="PlanetaryStackOptions.CropToCoverage"/>). The weight a stack folds is
/// not it: per-point quality weighting gives a frame's sky less weight than its planet, so a crop read off the folded weight took the sky
/// for under-covered and cut down to the planet (the Meade 16 Saturn to 459 x 203 of 512 x 320, into its faint halo).
/// </summary>
/// <remarks>
/// A footprint is added to a difference array, four corners a frame, and summed once at the end: constant work a frame, one pass over the
/// grid. A frame registered through a mesh is placed by the mesh at the grid's corners, where no alignment point bends it and the mesh is
/// the global shift. Not thread-safe: a stack folds its frames in one order (<see cref="PlanetaryFrameBatches"/>), and so adds them.
/// </remarks>
internal sealed class PlanetaryCoverage(int width, int height)
{
    private readonly double[,] _corners = new double[height + 1, width + 1];

    /// <summary>The output grid's width.</summary>
    public int Width => width;

    /// <summary>The output grid's height.</summary>
    public int Height => height;

    /// <summary>
    /// A frame's footprint on the output grid, <c>[left, right) x [top, bottom)</c> in output pixels: a pixel counts when its sample
    /// lies inside the frame.
    /// </summary>
    public void Add(double left, double top, double right, double bottom, float weight)
    {
        var x0 = Math.Clamp((int)Math.Ceiling(left), 0, width);
        var x1 = Math.Clamp((int)Math.Ceiling(right), 0, width);
        var y0 = Math.Clamp((int)Math.Ceiling(top), 0, height);
        var y1 = Math.Clamp((int)Math.Ceiling(bottom), 0, height);
        if (x1 <= x0 || y1 <= y0 || !(weight > 0))
        {
            return;
        }
        _corners[y0, x0] += weight;
        _corners[y0, x1] -= weight;
        _corners[y1, x0] -= weight;
        _corners[y1, x1] += weight;
    }

    /// <summary>A frame of <paramref name="frameWidth"/> x <paramref name="frameHeight"/> sampled at <c>(x + dx, y + dy)</c>.</summary>
    public void Add(double dx, double dy, int frameWidth, int frameHeight, float weight)
        => Add(-dx, -dy, frameWidth - dx, frameHeight - dy, weight);

    /// <summary>A frame of <paramref name="frameWidth"/> x <paramref name="frameHeight"/> registered through <paramref name="mesh"/>.</summary>
    public void Add(DisplacementMesh mesh, int frameWidth, int frameHeight, float weight)
    {
        var (lx, ty) = mesh.Sample(0, 0);
        var (rx, by) = mesh.Sample(width - 1, height - 1);
        Add((lx + rx) / 2, (ty + by) / 2, frameWidth, frameHeight, weight);
    }

    /// <summary>The weight that reached each pixel.</summary>
    public float[,] Plane()
    {
        var plane = new float[height, width];
        var row = new double[width + 1];
        for (var y = 0; y < height; y++)
        {
            var running = 0.0;
            for (var x = 0; x < width; x++)
            {
                row[x] += _corners[y, x];
                running += row[x];
                plane[y, x] = (float)running;
            }
        }
        return plane;
    }
}
