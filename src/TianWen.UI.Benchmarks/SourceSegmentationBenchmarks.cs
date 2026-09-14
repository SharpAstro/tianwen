using System;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Imaging.Sources;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Cost of the source-detection stack on a synthetic frame with a star field and a nebula: the background
/// map alone, the segmentation with its default two background passes, and the edge-spread reading of the
/// nebula. Frame sides are the two real masters it was read on (2048 is a quarter of a 3840 by 2160 frame's
/// pixels, 4096 is above a 3173 by 3144 master's).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class SourceSegmentationBenchmarks
{
    [Params(2048, 4096)]
    public int Side;

    private float[] _plane = [];
    private BackgroundMap? _map;
    private SegmentationMap? _segments;
    private int _nebulaLabel;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(17);
        var n = Side * Side;
        _plane = new float[n];
        for (var i = 0; i < n; i++)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            _plane[i] = 0.1f + 0.002f * (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        // One star per 64 by 64 cell on average, a gentle brightness spread, plus one 40 px nebula.
        var stars = n / 4096;
        for (var s = 0; s < stars; s++)
        {
            AddGaussian(rng.Next(8, Side - 8), rng.Next(8, Side - 8), 0.02f + 0.3f * rng.NextSingle(), 1.6f);
        }

        AddGaussian(Side / 2, Side / 2, 0.03f, 40f);
        _map = BackgroundMap.Estimate(_plane, Side, Side);
        _segments = SourceSegmentation.Detect(_plane, Side, Side, _map);
        var best = 0;
        for (var i = 0; i < _segments.Segments.Length; i++)
        {
            var seg = _segments.Segments[i];
            if (!seg.IsCompact && seg.Area > best)
            {
                best = seg.Area;
                _nebulaLabel = seg.Label;
            }
        }
    }

    [Benchmark(Description = "BackgroundMap.Estimate (64 px cells)")]
    public float Background() => BackgroundMap.Estimate(_plane, Side, Side).GlobalRms;

    [Benchmark(Description = "SourceSegmentation.Detect (two background passes, deblend)")]
    public int Detect() => SourceSegmentation.Detect(_plane, Side, Side, _map ?? throw new InvalidOperationException()).Segments.Length;

    [Benchmark(Description = "StarMask + StructureMask + SkyMask (margin 3)")]
    public int Masks()
    {
        var seg = _segments ?? throw new InvalidOperationException();
        var star = seg.StarMask(3);
        var structure = seg.StructureMask();
        var sky = seg.SkyMask(3);
        return star.WordsPerRow + structure.WordsPerRow + sky.WordsPerRow;
    }

    [Benchmark(Description = "EdgeSpreadProfile.Measure on the nebula (36 sectors)")]
    public float EdgeSpread()
    {
        var seg = _segments ?? throw new InvalidOperationException();
        return _nebulaLabel == 0 ? float.NaN : EdgeSpreadProfile.Measure(_plane, Side, Side, seg, _nebulaLabel).EdgeWidth;
    }

    private void AddGaussian(int cx, int cy, float amplitude, float sigma)
    {
        var r = (int)MathF.Ceiling(4f * sigma);
        var twoSigmaSq = 2f * sigma * sigma;
        for (var dy = -r; dy <= r; dy++)
        {
            var y = cy + dy;
            if (y < 0 || y >= Side)
            {
                continue;
            }

            for (var dx = -r; dx <= r; dx++)
            {
                var x = cx + dx;
                if (x < 0 || x >= Side)
                {
                    continue;
                }

                _plane[y * Side + x] += amplitude * MathF.Exp(-(dx * dx + dy * dy) / twoSigmaSq);
            }
        }
    }
}
