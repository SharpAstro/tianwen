using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Synthetic data + an in-memory <see cref="IPlanetaryFrameStream"/> for the planetary stacking
/// benchmarks / profiling -- no SER fixture needed. Frames are a textured disk on a dark sky with a small
/// per-frame shift, so the grader has structure to score, the aligner has a real (small) shift to recover,
/// and <see cref="PlanetaryDisk.BoundingBox"/> finds a disk.
/// </summary>
internal static class PlanetaryBenchData
{
    /// <summary>Generates <paramref name="count"/> mono <paramref name="size"/>x<paramref name="size"/>
    /// frames (a sub-plane-sized planetary ROI), each shifted slightly so alignment does real work.</summary>
    public static float[][,] MonoDiskFrames(int count, int size)
    {
        var frames = new float[count][,];
        for (var i = 0; i < count; i++)
        {
            // Deterministic small per-frame drift (sub-pixel) without Random (which the workflow/runtime ban
            // and which would hurt reproducibility anyway).
            var dx = ((i % 7) - 3) * 0.35f;
            var dy = ((i % 5) - 2) * 0.30f;
            frames[i] = Disk(size, dx, dy);
        }

        return frames;
    }

    /// <summary>A single textured disk plane, centred + offset by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    public static float[,] Disk(int n, float dx, float dy)
    {
        var a = new float[n, n];
        double cx = (n / 2.0) + dx, cy = (n / 2.0) + dy, r = n * 0.38;
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var ddx = x - cx;
                var ddy = y - cy;
                a[y, x] = (ddx * ddx) + (ddy * ddy) < r * r
                    ? (float)(0.5 + (0.25 * Math.Sin(x * 0.6) * Math.Cos(y * 0.55)) + (0.12 * Math.Sin((x - y) * 0.3)))
                    : 0.03f;
            }
        }

        return a;
    }

    /// <summary>A 3-channel RGB master-like image (textured disk per channel) for the sharpen / adopt benches.</summary>
    public static Image RgbMaster(int n)
    {
        var channels = Image.CreateChannelData(3, n, n);
        var baseDisk = Disk(n, 0f, 0f);
        for (var c = 0; c < 3; c++)
        {
            var plane = channels[c];
            var tint = 1f - (c * 0.12f); // slight per-channel difference so WB / stats aren't degenerate
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    plane[y, x] = baseDisk[y, x] * tint;
                }
            }
        }

        return new Image(channels, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta(
            "", default, default, FrameType.Light, "",
            3.76f, 3.76f, 0, 0, default,
            1, 1, float.NaN, SensorType.Color, 0, 0,
            RowOrder.TopDown, 0f, 0f));
    }

    /// <summary>Deep-clones an <see cref="Image"/> (for benches whose callee consumes / mutates the input).</summary>
    public static Image Clone(Image source)
    {
        var ch = source.ChannelCount;
        var copy = Image.CreateChannelData(ch, source.Height, source.Width);
        for (var c = 0; c < ch; c++)
        {
            source.GetChannelSpan(c).CopyTo(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref copy[c][0, 0], copy[c].Length));
        }

        return new Image(copy, BitDepth.Float32, 1f, 0f, 0f, source.ImageMeta);
    }
}

/// <summary>In-memory mono frame stream over pre-generated planes; <see cref="LoadAsync"/> clones so the
/// caller (the stacker) can <see cref="Image.Release"/> it freely. No timestamps -> the frame-count
/// fallback window applies.</summary>
internal sealed class BenchFrameStream(float[][,] data) : IPlanetaryFrameStream
{
    public int FrameCount => data.Length;
    public int Width => data[0].GetLength(1);
    public int Height => data[0].GetLength(0);
    public PlanetaryFrameLayout Layout => PlanetaryFrameLayout.Mono;
    public bool HasTimestamps => false;
    public DateTimeOffset? TimestampOf(int index) => null;

    public ValueTask<Image> LoadAsync(int index, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Image.FromChannel((float[,])data[index].Clone(), 1f, 0f));

    public void Dispose() { }
}
