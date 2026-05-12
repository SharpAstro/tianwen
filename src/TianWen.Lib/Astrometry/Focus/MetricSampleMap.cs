using DIR.Lib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Astrometry.Focus;

public class MetricSampleMap(SampleKind kind, AggregationMethod aggregationMethod)
{
    private readonly ConcurrentDictionary<int, ConcurrentBag<float>> _samples = [];

    public SampleKind Kind { get; } = kind;

    /// <summary>Returns all focus positions that have at least one sample.</summary>
    public int[] Keys() => [.. _samples.Keys];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ConcurrentBag<float> Samples(int focusPos) => _samples.GetOrAdd(focusPos, pFocusPos => []);

    public bool AddSampleAtFocusPosition(int currentPos, float sample)
    {
        if (!float.IsNaN(sample) && sample > 0)
        {
            // add the sample
            Samples(currentPos).Add(sample);

            return true;
        }

        return false;
    }

    public bool TryGetBestFocusSolution([NotNullWhen(true)] out FocusSolution? solution, out int min, out int max, int maxIterations = 20)
    {
        var keys = _samples.Keys.ToArray();
        Array.Sort(keys);

        if (keys.Length > 2)
        {
            min = keys[0];
            max = keys[^1];
        }
        else
        {
            min = -1;
            max = -1;

            solution = null;
            return false;
        }

        // ignore outliers if we have more than 10 samples
        var offset = keys.Length > 10 ? 1 : 0;
        var count = keys.Length - (offset * 2);

        var data = new float[count, 2];
        for (int i = 0; i < count; i++)
        {
            var focusPos = keys[i + offset];

            var aggregated = Aggregate(focusPos);
            if (!aggregated.HasValue)
            {
                solution = null;
                return false;
            }

            data[i, 0] = focusPos;
            data[i, 1] = aggregated.Value;
        }

        solution = Hyperbola.FindBestHyperbolaFit(data, max_iterations: maxIterations);
        return true;
    }

    public float? Aggregate(int focusPos)
    {
        if (_samples.TryGetValue(focusPos, out var samples))
        {
            return Aggregate(samples);
        }
        else
        {
            return default;
        }
    }

    public float Aggregate(IReadOnlyCollection<float> samples)
    {
        switch (aggregationMethod)
        {
            case AggregationMethod.Median:
                var median = Median(samples.ToArray());
                return !float.IsNaN(median) ? median : default;

            case AggregationMethod.Minimum:
                return samples.Count > 0 ? samples.Min() : default;

            case AggregationMethod.Average:
                return samples.Count > 0 ? samples.Average() : default;

            default:
                throw new NotSupportedException($"Aggregation method {aggregationMethod} is not supported");
        }
    }

    /// <summary>
    /// Renders the V-curve fit + per-position sample dots onto <paramref name="renderer"/>'s
    /// surface using DIR.Lib drawing primitives (CPU, no Magick.NET, no GPU). The hyperbola
    /// is a thick polyline; the chart axes are dashed lines; each sample focus position is a
    /// small filled dot. Coordinate frame: x = focus position (mapped to [xMargin, width-xMargin]
    /// pixels), y = HFD / metric value (flipped so larger metric draws higher).
    /// </summary>
    /// <param name="hyperbolaColor">Curve / axis / dot colour. Defaults to OrangeRed (255,69,0).</param>
    public void Draw(
        in FocusSolution solution,
        int minPos,
        int maxPos,
        RgbaImageRenderer renderer,
        int xMargin,
        int yMargin,
        RGBAColor32? hyperbolaColor = null)
    {
        var center = solution.BestFocus;
        var maxDist = Math.Max(solution.DistFromCenter(minPos), solution.DistFromCenter(maxPos));
        var range = maxDist * 2;

        var imageWidth = (int)renderer.Width;
        var imageHeight = (int)renderer.Height;
        var allX = imageWidth - (xMargin * 2);
        var allY = imageHeight - (yMargin * 2);
        var scaleX = range / allX;
        double scaledLeftX = solution.BestFocus - (allX / 2 * scaleX);
        double? scaleY = null;

        // Sample the hyperbola at 1 px x-resolution; the polyline default impl emits one
        // DrawLine call per segment which is essentially free on a buffer this size
        // (see DIR.Lib.Benchmarks DrawingPrimitiveBenchmarks).
        var points = new (float X, float Y)[allX];
        for (var x = 0; x < allX; x++)
        {
            var position = scaledLeftX + (x * scaleX);
            var y = Hyperbola.CalculateValueAtPosition(position, center, solution.A, solution.B);
            var renderedY = y * (scaleY ??= allY / y);
            points[x] = ((float)(x + xMargin), (float)(imageHeight - yMargin - renderedY));
        }

        // OrangeRed (#FF4500) matches the prior Magick default.
        var color = hyperbolaColor ?? new RGBAColor32(0xFF, 0x45, 0x00, 0xFF);

        // Hyperbola fit
        renderer.DrawPolyline(points, color, thickness: 2);

        // Dashed Y axis (left edge) and X axis (bottom edge). Dash=3 / gap=3 matches the
        // ImageMagick StrokeDashArray(3) shorthand (single-value pattern = equal on/off).
        renderer.DrawLineDashed(xMargin, 0, xMargin, imageHeight - yMargin / 2,
            color, dashLength: 3f, gapLength: 3f);
        renderer.DrawLineDashed(xMargin / 2, imageHeight - yMargin, imageWidth, imageHeight - yMargin,
            color, dashLength: 3f, gapLength: 3f);

        // Sample dots — small filled circles at each (focus pos, aggregated metric).
        if (!_samples.IsEmpty && scaleY is not null)
        {
            foreach (var sample in _samples)
            {
                var distFromCenter = sample.Key - center;
                var x = distFromCenter < 0 ? maxDist + distFromCenter : maxDist + distFromCenter;
                var scaledX = xMargin + x / scaleX;

                var metric = sample.Value.Count is 1
                    ? sample.Value.First()
                    : Aggregate(sample.Value);
                var renderedY = metric * scaleY.Value;
                var flippedY = imageHeight - yMargin - renderedY;

                // 4x4 filled disc — visible at chart scale, doesn't obscure the hyperbola.
                // RectInt is (LowerRight exclusive, UpperLeft inclusive).
                var cx = (int)scaledX;
                var cy = (int)flippedY;
                var dot = new RectInt(
                    new PointInt(cx + 2, cy + 2),
                    new PointInt(cx - 2, cy - 2));
                renderer.FillEllipse(dot, color);
            }
        }
    }
}
