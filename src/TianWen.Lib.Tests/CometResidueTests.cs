using System;
using System.Numerics;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="CometResidue"/>, the reading that judged 10P's composites on 2026-11-25 and guards them: a round coma
/// reads clean, a green and magenta trail pair beside a star is chroma along the track, a dark streak is luminance along
/// it, and a slope cut at the model's reach is the edge's first harmonic. Each case is built so the reading it pins
/// would fail with the structure taken out, and the clean case holds the noise floor the others stand above.
/// </summary>
public sealed class CometResidueTests
{
    private const int Size = 640;
    private const float Sky = 0.5f;
    private const float Noise = 0.002f;
    private const float Reach = 250f;
    private static readonly Vector2 Body = new(320f, 320f);

    /// <summary>A coma, green-heavy as 10P's, with 1/r^2 wings.</summary>
    private static readonly float[] ComaPeak = [0.02f, 0.06f, 0.04f];

    private static float Gaussian(Random rng)
    {
        var sum = 0.0;
        for (var i = 0; i < 12; i++)
        {
            sum += rng.NextDouble();
        }

        return (float)(sum - 6.0);
    }

    private static ImageMeta Meta() => new()
    {
        Instrument = "synth",
        ExposureStartTime = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero),
        ExposureDuration = TimeSpan.FromSeconds(60),
        FrameType = FrameType.Light,
        SensorType = SensorType.Color,
    };

    // A star layer: sky, noise, and a few round stars inside the coma, as a star layer has them.
    private static float[][,] StarLayer(int seed)
    {
        var rng = new Random(seed);
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    planes[c][y, x] = Sky + Noise * Gaussian(rng);
                }
            }
        }

        foreach (var star in Stars)
        {
            for (var y = (int)star.Y - 8; y <= (int)star.Y + 8; y++)
            {
                for (var x = (int)star.X - 8; x <= (int)star.X + 8; x++)
                {
                    var d2 = (x - star.X) * (x - star.X) + (y - star.Y) * (y - star.Y);
                    for (var c = 0; c < 3; c++)
                    {
                        planes[c][y, x] += 0.3f * MathF.Exp(-d2 / (2f * 1.2f * 1.2f));
                    }
                }
            }
        }

        return planes;
    }

    private static readonly Vector2[] Stars = [new(380f, 290f), new(270f, 400f), new(330f, 230f)];

    private static Image AsImage(float[][,] planes) => new(planes, BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, imageMeta: Meta());

    private static float[][,] Copy(float[][,] planes)
    {
        var copy = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            copy[c] = new float[Size, Size];
            Array.Copy(planes[c], copy[c], planes[c].Length);
        }

        return copy;
    }

    // The composite: the star layer with a round coma added back, plus whatever the case adds.
    private static float[][,] WithComa(float[][,] star, Vector2 slope = default)
    {
        var composite = Copy(star);
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var r = Vector2.Distance(new Vector2(x, y), Body);
                    if (r > Reach)
                    {
                        continue;
                    }

                    var coma = ComaPeak[c] / (1f + r * r / (20f * 20f));
                    composite[c][y, x] += coma + slope.X * (x - Body.X) + slope.Y * (y - Body.Y);
                }
            }
        }

        return composite;
    }

    // A trail of residue beside each star, along the drift (down and to the right, as 10P's), from 10 to 40 px past the
    // star: per channel the given amounts, in sky sigma.
    private static void AddTrails(float[][,] planes, float red, float green, float blue)
    {
        var direction = Vector2.Normalize(Drift);
        var amounts = new[] { red, green, blue };
        foreach (var star in Stars)
        {
            for (var t = 10f; t <= 40f; t += 0.5f)
            {
                var p = star + t * direction;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var (x, y) = ((int)MathF.Round(p.X), (int)MathF.Round(p.Y) + dy);
                    for (var c = 0; c < 3; c++)
                    {
                        planes[c][y, x] += amounts[c] * Noise;
                    }
                }
            }
        }
    }

    /// <summary>10P's drift on the canvas, px/h: the direction every trail on the comet grid lies along.</summary>
    private static readonly Vector2 Drift = new(10.857f, 6.431f);

    // Plain noise averaged over a 21 px line: chroma (1.22 sigma per pixel) reads about 1.0 at its 99.99th percentile,
    // luminance (0.58) about 0.5. A clean plate stays inside this.
    private const float CleanBound = 2.5f;

    [Fact]
    public void ARoundComaOnAStarLayerReadsAtTheNoiseFloor()
    {
        var star = StarLayer(seed: 1);

        var reading = CometResidue.Measure(AsImage(star), AsImage(WithComa(star)), Body, Drift, Reach);

        reading.SkySigma[1].ShouldBe(Noise, Noise * 0.1f, "premise: the sky noise is read off the window's outer ring");
        reading.StarCorePixels.ShouldBeGreaterThan(0, "premise: the stars inside the reach are masked");
        foreach (var (name, track) in new[] { ("star layer", reading.StarLayer), ("model", reading.Model) })
        {
            track.ChromaLow.ShouldBeGreaterThan(-CleanBound, name);
            track.ChromaHigh.ShouldBeLessThan(CleanBound, name);
            track.LuminanceLow.ShouldBeGreaterThan(-CleanBound, name);
            track.LuminanceHigh.ShouldBeLessThan(CleanBound, name);
        }

        for (var c = 0; c < 3; c++)
        {
            reading.EdgeHarmonic[c].ShouldBeLessThan(0.1f);
        }
    }

    [Fact]
    public void AGreenAndMagentaTrailPairBesideTheStarsIsChroma()
    {
        var star = StarLayer(seed: 2);
        var composite = WithComa(star);
        // What a drizzled 10P comet layer left: the model held magenta trails (red and blue up, green down), subtracted
        // from the star layer they left green ones, and the composite carries the magenta back. Each trail is a colour
        // with no luminance of its own (green up by twice what red and blue are down), as 10P's were: run 2's star layer
        // read -0.92 in luminance while its chroma read +4.88.
        AddTrails(star, red: -3f, green: 6f, blue: -3f);
        AddTrails(composite, red: 3f, green: -6f, blue: 3f);

        var reading = CometResidue.Measure(AsImage(star), AsImage(composite), Body, Drift, Reach);

        reading.StarLayer.ChromaHigh.ShouldBeGreaterThan(6f, "the star layer's green trails: chroma 6 + 3");
        reading.Model.ChromaLow.ShouldBeLessThan(-8f, "the model's magenta trails: chroma -12 - 6");
        reading.StarLayer.LuminanceLow.ShouldBeGreaterThan(-CleanBound, "a trail of colour with no luminance is no dark streak");
    }

    [Fact]
    public void ADarkStreakBesideTheStarsIsLuminance()
    {
        var star = StarLayer(seed: 4);
        var composite = WithComa(star);
        // What a model with trail flux in it did on 10P without per-frame star removal: subtracted at every frame's body
        // position, it dug a dark streak beside each bright star, in every channel alike.
        AddTrails(star, red: -6f, green: -6f, blue: -6f);
        AddTrails(composite, red: -6f, green: -6f, blue: -6f);

        var reading = CometResidue.Measure(AsImage(star), AsImage(composite), Body, Drift, Reach);

        reading.StarLayer.LuminanceLow.ShouldBeLessThan(-4f, "the dark streaks");
        reading.StarLayer.ChromaHigh.ShouldBeLessThan(CleanBound, "a grey streak has no colour");
        reading.StarLayer.ChromaLow.ShouldBeGreaterThan(-CleanBound);
    }

    [Fact]
    public void ASlopeCutAtTheReachIsTheEdgesFirstHarmonic()
    {
        var star = StarLayer(seed: 3);
        // 0.01 sky sigma per pixel across the reach: about 2.4 sigma at the edge, one side up and the other down.
        var slope = new Vector2(0.01f * Noise, -0.004f * Noise);

        var reading = CometResidue.Measure(AsImage(star), AsImage(WithComa(star, slope)), Body, Drift, Reach);

        var expected = slope.Length() / Noise * (Reach - CometResidue.EdgeWidthPx / 2f);
        for (var c = 0; c < 3; c++)
        {
            reading.EdgeHarmonic[c].ShouldBe(expected, expected * 0.25f, $"channel {c}: the dipole the slope paints at the reach");
        }
    }
}
