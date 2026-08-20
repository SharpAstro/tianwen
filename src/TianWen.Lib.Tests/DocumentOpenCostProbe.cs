using System;
using System.Diagnostics;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Diagnostic probe: times every full-frame traversal a viewer document open performs, on an image
/// shaped exactly as <c>Image.TryReadTiff</c> produces one (unit-scaled floats, MaxValue 1.0, the
/// source bit depth). Answers "why is opening a big TIFF slow" with a per-stage breakdown instead of
/// an inference from the code shape. Not an assertion -- read the output.
/// </summary>
[Collection("Imaging")]
public class DocumentOpenCostProbe(ITestOutputHelper output)
{
    private const string EnvVar = "TIANWEN_DOC_OPEN_COST_PROBE";

    private const int Width = 6000;
    private const int Height = 4000;

    [Theory]
    [InlineData("drawing", BitDepth.Int8)]
    [InlineData("photo", BitDepth.Int8)]
    [InlineData("starfield", BitDepth.Int8)]
    [InlineData("starfield", BitDepth.Int16)]
    [InlineData("starfield", BitDepth.Float32)]
    public async Task TimeEveryTraversal(string kind, BitDepth bitDepth)
    {
        // Env-gated: five 24 MP cases allocate 288 MB each and take minutes, which a bare
        // dotnet test should not pay for a diagnostic. Same posture as DetectionPurityProbe.
        Assert.SkipUnless(Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 }, $"{EnvVar} not set");

        var ct = TestContext.Current.CancellationToken;
        var image = Build(kind, bitDepth);
        var (channelCount, w, h) = image.Shape;
        var mp = w * (double)h / 1e6;
        output.WriteLine($"--- {kind} {w}x{h} ch={channelCount} bitDepth={bitDepth} maxValue={image.MaxValue} ({mp:F1} MP)");

        // Touch every plane first. 288 MB of freshly allocated arrays would otherwise charge their
        // page faults to whichever stage ran first, which is how the first run of this probe made
        // Statistics look 15x more expensive than the traversal beside it.
        var warm = 0.0;
        for (var c = 0; c < channelCount; c++)
        {
            var span = image.GetChannelSpan(c);
            for (var i = 0; i < span.Length; i += 1024) { warm += span[i]; }
        }
        output.WriteLine($"(warm-up touched planes, checksum {warm:F0})");

        var t = Stopwatch.StartNew();
        for (var c = 0; c < channelCount; c++) { _ = image.Statistics(c); }
        output.WriteLine($"Statistics x{channelCount}          {t.ElapsedMilliseconds,6} ms");

        t.Restart();
        _ = image.Statistics(0);
        output.WriteLine($"Statistics x1              {t.ElapsedMilliseconds,6} ms");

        t.Restart();
        var pedestals = new float[channelCount];
        var (_, _) = image.ScanBackgroundRegion(pedestals);
        output.WriteLine($"ScanBackgroundRegion       {t.ElapsedMilliseconds,6} ms");

        t.Restart();
        var bg = image.Background(image.ReferenceStarChannel);
        output.WriteLine($"Background                 {t.ElapsedMilliseconds,6} ms   bg={bg.background:G4} starLevel={bg.starLevel:G4} noise={bg.noise_level:G4} thr={bg.threshold:G4}");

        t.Restart();
        var stars = await image.FindStarsAsync(image.ReferenceStarChannel, snrMin: 10f, maxStars: 2000,
            logger: new XunitLogger(output), cancellationToken: ct);
        output.WriteLine($"FindStarsAsync             {t.ElapsedMilliseconds,6} ms   -> {stars.Count} stars");

        if (stars.StarMask is { } mask)
        {
            t.Restart();
            var (_, _) = image.ScanBackgroundRegion(pedestals, squareSize: 48, mask);
            output.WriteLine($"ScanBackgroundRegion(mask) {t.ElapsedMilliseconds,6} ms");

            t.Restart();
            for (var c = 0; c < channelCount; c++) { _ = image.GetStarMaskedMedianAndMADScaledToUnit(c, mask); }
            output.WriteLine($"StarMaskedMedianMAD x{channelCount}  {t.ElapsedMilliseconds,6} ms");
        }
    }

    /// <summary>Unit-scaled float planes with MaxValue 1.0 -- the exact shape the TIFF importer emits.</summary>
    private static Image Build(string kind, BitDepth bitDepth)
    {
        const int channels = 3;
        var planes = new float[channels][,];
        for (var c = 0; c < channels; c++) { planes[c] = new float[Height, Width]; }
        var rng = new Random(42);

        switch (kind)
        {
            case "drawing":
                // Mostly paper-white with hard black linework and a few grey fills: flat regions, hard
                // edges, no noise floor -- an architectural drawing.
                for (var c = 0; c < channels; c++)
                {
                    var p = planes[c];
                    for (var y = 0; y < Height; y++)
                    {
                        for (var x = 0; x < Width; x++) { p[y, x] = 1f; }
                    }
                    for (var y = 0; y < Height; y += 137)
                    {
                        for (var x = 0; x < Width; x++) { p[y, x] = 0f; }
                    }
                    for (var x = 0; x < Width; x += 211)
                    {
                        for (var y = 0; y < Height; y++) { p[y, x] = 0f; }
                    }
                    for (var y = Height / 4; y < Height / 2; y++)
                    {
                        for (var x = Width / 4; x < Width / 2; x++) { p[y, x] = 0.72f; }
                    }
                }
                break;

            case "photo":
                // Smooth gradient plus 8-bit-quantised noise: an ordinary photograph.
                for (var c = 0; c < channels; c++)
                {
                    var p = planes[c];
                    for (var y = 0; y < Height; y++)
                    {
                        for (var x = 0; x < Width; x++)
                        {
                            var v = 0.35f + 0.4f * (x / (float)Width) + 0.15f * (y / (float)Height)
                                    + (float)(rng.NextDouble() - 0.5) * (6f / 255f);
                            p[y, x] = Math.Clamp(v, 0f, 1f);
                        }
                    }
                }
                break;

            default:
                // A stretched 8-bit astro export: low background, read noise, a few thousand stars.
                for (var c = 0; c < channels; c++)
                {
                    var p = planes[c];
                    for (var y = 0; y < Height; y++)
                    {
                        for (var x = 0; x < Width; x++)
                        {
                            p[y, x] = Math.Clamp(0.08f + (float)(rng.NextDouble() - 0.5) * (4f / 255f), 0f, 1f);
                        }
                    }
                    for (var s = 0; s < 3000; s++)
                    {
                        var sx = rng.Next(20, Width - 20);
                        var sy = rng.Next(20, Height - 20);
                        var peak = 0.2f + (float)rng.NextDouble() * 0.75f;
                        for (var dy = -4; dy <= 4; dy++)
                        {
                            for (var dx = -4; dx <= 4; dx++)
                            {
                                var g = peak * MathF.Exp(-(dx * dx + dy * dy) / 4.5f);
                                p[sy + dy, sx + dx] = Math.Clamp(p[sy + dy, sx + dx] + g, 0f, 1f);
                            }
                        }
                    }
                }
                break;
        }

        var meta = new ImageMeta("synth", DateTimeOffset.UtcNow, TimeSpan.Zero, FrameType.Light, "",
            0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Color, 0, 0,
            RowOrder.TopDown, float.NaN, float.NaN);
        return new Image(planes, bitDepth, 1.0f, 0f, 0f, meta);
    }
}
