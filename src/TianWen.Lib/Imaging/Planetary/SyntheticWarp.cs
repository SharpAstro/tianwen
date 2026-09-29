using System;
using System.Collections.Immutable;
using System.IO;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// One synthetic frame's warp on a node grid <see cref="GridStep"/> px apart, in the rendered window's pixels, read between the
/// nodes bilinearly: the content at window point p came from p minus the displacement there (<see cref="PlanetaryDegrade"/>).
/// </summary>
/// <param name="Nodes">The grid's nodes on a side.</param>
/// <param name="X">The displacement across, node by node, row-major.</param>
/// <param name="Y">The displacement down.</param>
public sealed record SyntheticWarpField(int Nodes, float[] X, float[] Y)
{
    /// <summary>The nodes' spacing, px.</summary>
    public const int GridStep = 4;

    /// <summary>No warp.</summary>
    public static readonly SyntheticWarpField Empty = new SyntheticWarpField(0, [], []);

    /// <summary>Whether there is no warp.</summary>
    public bool IsEmpty => Nodes == 0;

    /// <summary>The displacement at window position (<paramref name="px"/>, <paramref name="py"/>), px; none where there is no warp.</summary>
    public (double X, double Y) At(double px, double py)
    {
        if (IsEmpty)
        {
            return (0, 0);
        }
        var gx = Math.Clamp(px / GridStep, 0, Nodes - 1.001);
        var gy = Math.Clamp(py / GridStep, 0, Nodes - 1.001);
        var (x0, y0) = ((int)gx, (int)gy);
        var (tx, ty) = (gx - x0, gy - y0);
        var i = (y0 * Nodes) + x0;
        double Read(float[] f) => (((f[i] * (1 - tx)) + (f[i + 1] * tx)) * (1 - ty)) + (((f[i + Nodes] * (1 - tx)) + (f[i + Nodes + 1] * tx)) * ty);
        return (Read(X), Read(Y));
    }
}

/// <summary>
/// A synthetic frame's warp where the frame is: its field, and the detector position of the rendered window's corner (the whole
/// pixels of the frame's shift; the fraction was applied before the warp). The truth a dewarp is scored against (R5 part 2).
/// </summary>
/// <param name="OriginX">The window's corner on the detector, x.</param>
/// <param name="OriginY">The window's corner on the detector, y.</param>
/// <param name="Field">The warp in the window's pixels.</param>
public readonly record struct SyntheticWarp(int OriginX, int OriginY, SyntheticWarpField Field)
{
    /// <summary>The displacement at window position (<paramref name="windowX"/>, <paramref name="windowY"/>), px.</summary>
    public (double X, double Y) AtWindow(double windowX, double windowY) => Field.At(windowX, windowY);
}

/// <summary>
/// A synthetic capture's warps, one per frame, beside it as <c>&lt;capture&gt;.warp</c>: <c>TWWARP1</c>, the node count, then
/// each frame's window corner and its X and Y fields as little-endian floats.
/// </summary>
public static class SyntheticWarpFile
{
    private static ReadOnlySpan<byte> Magic => "TWWARP1\0"u8;

    /// <summary>The warp file beside <paramref name="capture"/>.</summary>
    public static string PathFor(string capture) => Path.ChangeExtension(capture, ".warp");

    /// <summary>Writes warps as they come, in frame order.</summary>
    public sealed class Writer : IDisposable
    {
        private readonly BinaryWriter _writer;
        private int _nodes = -1;

        /// <summary>A writer to <paramref name="path"/>, replacing it.</summary>
        public Writer(string path)
        {
            _writer = new BinaryWriter(File.Create(path));
            _writer.Write(Magic);
        }

        /// <summary>The next frame's warp; every frame's grid must have the same nodes.</summary>
        public void Append(SyntheticWarp warp)
        {
            var nodes = warp.Field.Nodes;
            if (_nodes < 0)
            {
                _nodes = nodes;
                _writer.Write(nodes);
            }
            else if (nodes != _nodes)
            {
                throw new ArgumentException($"A frame's warp has {nodes} nodes a side, the file's {_nodes}.", nameof(warp));
            }
            _writer.Write(warp.OriginX);
            _writer.Write(warp.OriginY);
            foreach (var v in warp.Field.X)
            {
                _writer.Write(v);
            }
            foreach (var v in warp.Field.Y)
            {
                _writer.Write(v);
            }
        }

        /// <inheritdoc/>
        public void Dispose() => _writer.Dispose();
    }

    /// <summary>Every frame's warp in <paramref name="path"/>, in frame order; null when it is not a warp file.</summary>
    public static ImmutableArray<SyntheticWarp>? Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
        {
            return null;
        }
        var warps = ImmutableArray.CreateBuilder<SyntheticWarp>();
        if (reader.BaseStream.Position == reader.BaseStream.Length)
        {
            return warps.ToImmutable();
        }
        var nodes = reader.ReadInt32();
        var cells = nodes * nodes;
        var frameBytes = 8 + (8L * cells);
        while (reader.BaseStream.Length - reader.BaseStream.Position >= frameBytes)
        {
            var (originX, originY) = (reader.ReadInt32(), reader.ReadInt32());
            var (x, y) = (new float[cells], new float[cells]);
            for (var i = 0; i < cells; i++)
            {
                x[i] = reader.ReadSingle();
            }
            for (var i = 0; i < cells; i++)
            {
                y[i] = reader.ReadSingle();
            }
            warps.Add(new SyntheticWarp(originX, originY, new SyntheticWarpField(nodes, x, y)));
        }
        return warps.ToImmutable();
    }
}
