using ImageMagick;
using ImageMagick.Drawing;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using TianWen.Lib.Stat;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Astrometry.Focus;

public class MetricSampleMap(SampleKind kind, AggregationMethod aggregationMethod)
{
    private readonly ConcurrentDictionary<int, ConcurrentBag<float>> _samples = [];

    public SampleKind Kind { get; } = kind;

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

    public void Draw(
        in FocusSolution solution,
        int minPos,
        int maxPos,
        IMagickImage<float> image,
        int xMargin,
        int yMargin,
        MagickColor? hyperbolaColor = null)
    {
        var center = solution.BestFocus;
        var maxDist = Math.Max(solution.DistFromCenter(minPos), solution.DistFromCenter(maxPos));
        var range = maxDist * 2;

        var allX = image.Width - (xMargin * 2);
        var allY = image.Height - (yMargin * 2);
        var scaleX = range / allX;
        double scaledLeftX = solution.BestFocus - (allX/2 * scaleX);
        double? scaleY = null;

        var points = new PointD[allX];
        for (var x = 0; x < allX; x++)
        {
            var position = scaledLeftX + (x * scaleX);
            var y = Hyperbola.CalculateValueAtPosition(position, center, solution.A, solution.B);
            var scaledY = y * (scaleY ??= allY / y);
            points[x] = new PointD(x + xMargin, image.Height - yMargin - scaledY);
        }

        var color = hyperbolaColor ?? MagickColors.OrangeRed;
        var drawables = new Drawables()
            .StrokeColor(color)
            .StrokeWidth(2)
            .StrokeLineJoin(LineJoin.Round)
            .FillColor(MagickColors.Transparent)
            .Polyline(points)
            .FillColor(color)
            .StrokeWidth(1)
            .StrokeDashArray(3)
            .Line(xMargin, 0, xMargin, image.Height - yMargin / 2)
            .Line(xMargin / 2, image.Height - yMargin, image.Width, image.Height - yMargin)
            .StrokeDashArray();

        if (!_samples.IsEmpty && scaleY is not null)
        {
            foreach (var sample in _samples)
            {
                var distFromCenter = sample.Key - center;

                var x = distFromCenter < 0 ? maxDist + distFromCenter : maxDist + distFromCenter;
                var scaledX = xMargin + x / scaleX;

                var y = sample.Value.Count is 1
                    ? sample.Value.First()
                    : Aggregate(sample.Value);
                var scaledY = y * scaleY.Value;
                var flippedY = image.Height - yMargin - scaledY;
                drawables.Circle(scaledX - 1, flippedY - 1, scaledX + 1, flippedY + 1);
            }
        }

        drawables.Draw(image);
    }
}
