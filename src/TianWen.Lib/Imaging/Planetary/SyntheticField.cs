using System;
using System.Collections.Immutable;
using System.IO;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// What one frame of a layered synthetic capture was at each of its field points (docs/plans/planetary-restoration.md, R4 per-point,
/// #1071): the per-point truth a local frame estimator is ranked against. The window lands at <see cref="OriginX"/>,
/// <see cref="OriginY"/> on the detector, as a warp's does.
/// </summary>
/// <param name="Points">The capture's field points in the rendered window's pixels, the same for every frame.</param>
/// <param name="OriginX">The window's corner on the detector, x.</param>
/// <param name="OriginY">The window's corner on the detector, y.</param>
/// <param name="TiltX">Each point's tilt over the frame's mean (the warp there), px across.</param>
/// <param name="TiltY">The same down.</param>
/// <param name="Strehl">Each point's PSF peak over the diffraction limit's.</param>
/// <param name="Gains">Each point's true quality in a trous bands 1 to <see cref="SyntheticFieldFile.Bands"/>, point by point: its
/// tilt-removed PSF's transfer over the diffraction limit's, weighted over the band's frequencies by the truth's power in a patch about
/// the point, the least-squares gain a noise-free frame's patch would have against the truth's there.</param>
public sealed record SyntheticFieldFrame(ImmutableArray<(double X, double Y)> Points, int OriginX, int OriginY, float[] TiltX, float[] TiltY, float[] Strehl, float[] Gains)
{
    /// <summary>Point <paramref name="point"/>'s true quality in a trous band <paramref name="band"/> (1 to <see cref="SyntheticFieldFile.Bands"/>).</summary>
    public float Gain(int point, int band) => Gains[(point * SyntheticFieldFile.Bands) + band - 1];
}

/// <summary>
/// A layered synthetic capture's per-point truth, beside it as <c>&lt;capture&gt;.field</c>: <c>TWFIELD1</c>, the patch the gains were
/// read over (px), the point count and each point's window position, then each frame's window corner and, point by point, its tilt,
/// Strehl ratio and band gains, all little-endian.
/// </summary>
public static class SyntheticFieldFile
{
    /// <summary>The a trous bands each point's gain is read in.</summary>
    public const int Bands = 4;

    private static ReadOnlySpan<byte> Magic => "TWFIELD1"u8;

    /// <summary>The field file beside <paramref name="capture"/>.</summary>
    public static string PathFor(string capture) => Path.ChangeExtension(capture, ".field");

    /// <summary>Writes the points with the first frame, then each frame as it comes, in frame order.</summary>
    public sealed class Writer : IDisposable
    {
        private readonly BinaryWriter _writer;
        private readonly int _patchPx;
        private ImmutableArray<(double X, double Y)> _points;

        /// <summary>A writer to <paramref name="path"/>, replacing it, for gains read over <paramref name="patchPx"/>.</summary>
        public Writer(string path, int patchPx)
        {
            _patchPx = patchPx;
            _writer = new BinaryWriter(File.Create(path));
            _writer.Write(Magic);
        }

        /// <summary>The next frame's per-point truth; every frame must have the same points.</summary>
        public void Append(SyntheticFieldFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            if (_points.IsDefault)
            {
                _points = frame.Points;
                _writer.Write(_patchPx);
                _writer.Write(_points.Length);
                foreach (var (x, y) in _points)
                {
                    _writer.Write(x);
                    _writer.Write(y);
                }
            }
            else if (frame.Points != _points)
            {
                throw new ArgumentException("A frame's field points are not the capture's.", nameof(frame));
            }
            var count = _points.Length;
            ArgumentOutOfRangeException.ThrowIfNotEqual(frame.TiltX.Length, count);
            ArgumentOutOfRangeException.ThrowIfNotEqual(frame.Gains.Length, count * Bands);
            _writer.Write(frame.OriginX);
            _writer.Write(frame.OriginY);
            for (var p = 0; p < count; p++)
            {
                _writer.Write(frame.TiltX[p]);
                _writer.Write(frame.TiltY[p]);
                _writer.Write(frame.Strehl[p]);
                for (var b = 0; b < Bands; b++)
                {
                    _writer.Write(frame.Gains[(p * Bands) + b]);
                }
            }
        }

        /// <inheritdoc/>
        public void Dispose() => _writer.Dispose();
    }

    /// <summary>A capture's every frame and the patch their gains were read over; null when <paramref name="path"/> is not a field file.</summary>
    public static (int PatchPx, ImmutableArray<SyntheticFieldFrame> Frames)? Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
        {
            return null;
        }
        if (reader.BaseStream.Position == reader.BaseStream.Length)
        {
            return (0, []);
        }
        var patchPx = reader.ReadInt32();
        var count = reader.ReadInt32();
        var builder = ImmutableArray.CreateBuilder<(double X, double Y)>(count);
        for (var p = 0; p < count; p++)
        {
            builder.Add((reader.ReadDouble(), reader.ReadDouble()));
        }
        var points = builder.MoveToImmutable();
        var frames = ImmutableArray.CreateBuilder<SyntheticFieldFrame>();
        var frameBytes = 8 + (4L * count * (3 + Bands));
        while (reader.BaseStream.Length - reader.BaseStream.Position >= frameBytes)
        {
            var (originX, originY) = (reader.ReadInt32(), reader.ReadInt32());
            var (tiltX, tiltY, strehl, gains) = (new float[count], new float[count], new float[count], new float[count * Bands]);
            for (var p = 0; p < count; p++)
            {
                tiltX[p] = reader.ReadSingle();
                tiltY[p] = reader.ReadSingle();
                strehl[p] = reader.ReadSingle();
                for (var b = 0; b < Bands; b++)
                {
                    gains[(p * Bands) + b] = reader.ReadSingle();
                }
            }
            frames.Add(new SyntheticFieldFrame(points, originX, originY, tiltX, tiltY, strehl, gains));
        }
        return (patchPx, frames.ToImmutable());
    }
}
