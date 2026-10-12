using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// #1537, the ring band's seam, on a twin with a truth: a ringed Saturn blurred as a stack is, on a sky pedestal, taken 40 minutes apart
/// (22.6 degrees of turn), the later carried to the earlier's instant by the compose's turn and set against the earlier, by distance past
/// the rings' margin. The feather (<see cref="RingFeather"/>, <c>--ring-feather 2,4</c>) is an option because it cuts both ways, as
/// measured on 2026-10-12: it takes the seam out of a featureless Saturn and leaves a spot beside the band where it was.
/// </summary>
public sealed class PlanetaryRingFeatherTests
{
    private const int Width = 260;
    private const int Height = 170;
    private const double Level = 0.6;
    private static readonly DateTimeOffset Night = new(2026, 10, 7, 12, 30, 0, TimeSpan.Zero);
    private static readonly DiskPlacement Disk = new(130.3, 85.4, 40, 4.11);

    [Theory(Timeout = 600_000)]
    [InlineData(0.0, 0.0)]
    [InlineData(0.3, 0.05)]
    [Trait("Category", "Heavy")]
    public void TheFeatherTakesTheSeamOutOfAFeaturelessSaturnAndLeavesASpotBesideTheBandUnturned(double belts, double spots)
    {
        // Measured 2026-10-12 (RMS off the truth, % of the disk's level, 0 to 3 px past the margin, the floor 16 to 24 px out): featureless,
        // the turn as it is 1.39 against a floor of 0.88 and the feather 0.02; belted with 5 % spots, the turn 1.94 and the feather 4.76.
        var ct = TestContext.Current.CancellationToken;
        var ingested = new List<PlanetaryMonoStack>();
        foreach (var at in new[] { Night, Night.AddMinutes(40) })
        {
            var sharp = PlanetaryRender.Render(Map(belts, spots), PhysicalEphemeris.Compute(CatalogIndex.Saturn, at), Disk, Width, Height, 0.85,
                supersample: 2, rings: SaturnRings.Structured(0.17, 0.70, 0.1, 0.50));
            float[] stacked = [.. Blurred(sharp, 2).Select(v => (float)((v * Level) + 0.05))];
            var (stack, refusal) = PlanetaryComposition.Ingest($"{at:yyyy-MM-dd-HHmm}_{at.Second / 6}-Test-G-Sat.fits", Image.FromChannel(ToPlane(stacked)),
                planet: CatalogIndex.Saturn, instant: at);
            ingested.Add(stack.ShouldNotBeNull(refusal));
        }
        var (registration, registerRefusal) = PlanetaryComposition.Register(ingested);
        registration.ShouldNotBeNull(registerRefusal);
        registration.ReferenceIndex.ShouldBe(0);
        var truth = registration.Stacks[0].Image.GetChannelSpan(0).ToArray();
        ct.ThrowIfCancellationRequested();

        var (turn, turnFloor) = OffTheTruth(PlanetaryComposition.Derotate(registration.Stacks), truth);
        ct.ThrowIfCancellationRequested();
        var (feathered, _) = OffTheTruth(PlanetaryComposition.Derotate(registration.Stacks, ringFeatherSigmas: (2, 4)), truth);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"belts {belts}, spots {spots}: 0 to 3 px past the rings' margin, the turn {turn:0.000} %, feathered {feathered:0.000} %; the floor 16 to 24 px out {turnFloor:0.000} %"));
        if (spots == 0)
        {
            turn.ShouldBeGreaterThan(1.3 * turnFloor, "the turn leaves a seam beside the band");
            feathered.ShouldBeLessThan(0.5 * turnFloor, "on a featureless planet the pixels kept beside the band are right");
        }
        else
        {
            feathered.ShouldBeGreaterThan(2 * turn, "a spot beside the band kept where it was is wrong by the planet's turn");
        }
    }

    // RMS off the truth of the carried stack, % of the level, 0 to 3 px past the rings' 2 px margin and 16 to 24 px out, inside the
    // globe's middle (|u| < 0.8 radii across the axis, inside 0.85 radii).
    private static (double Edge, double Floor) OffTheTruth((PlanetaryCompositionDerotation? Derotation, string? Refusal) result, float[] truth)
    {
        var derotation = result.Derotation.ShouldNotBeNull(result.Refusal);
        var carried = derotation.Stacks[1].Image.GetChannelSpan(0);
        var place = derotation.Placement;
        var options = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(CatalogIndex.Saturn, derotation.Instant));
        var seen = new MetricDisk(place.CenterX, place.CenterY, place.EquatorialRadius, options.AxisRatio, place.NorthAngleDeg)
        {
            Rings = DiskRings.Of(place.NorthAngleDeg, place.NorthAngleDeg, options, options.Rings ?? throw new InvalidOperationException("Saturn has rings")),
        };
        var (cos, sin) = (Math.Cos(place.NorthAngleDeg * Math.PI / 180), Math.Sin(place.NorthAngleDeg * Math.PI / 180));
        var (edge, edgeCount, floor, floorCount) = (0.0, 0, 0.0, 0);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var across = ((-(x - place.CenterX) * sin) + ((y - place.CenterY) * cos)) / place.EquatorialRadius;
                if (Math.Abs(across) >= 0.8 || seen.RadiiAt(x, y) >= 0.85 || seen.RingTouched(x, y, 2))
                {
                    continue;
                }
                var past = seen.RingDistance(x, y, 26) - 2;
                var d = 100 * (carried[(y * Width) + x] - truth[(y * Width) + x]) / Level;
                if (past < 3)
                {
                    (edge, edgeCount) = (edge + (d * d), edgeCount + 1);
                }
                else if (past is >= 16 and < 24)
                {
                    (floor, floorCount) = (floor + (d * d), floorCount + 1);
                }
            }
        }
        edgeCount.ShouldBeGreaterThan(200);
        floorCount.ShouldBeGreaterThan(200);
        return (Math.Sqrt(edge / edgeCount), Math.Sqrt(floor / floorCount));
    }

    // Saturn-like belts of the given depth with spots of the given share of the albedo, six to a turn of longitude.
    private static PlanetMap Map(double belts, double spots)
    {
        var values = new float[360 * 180];
        for (var row = 0; row < 180; row++)
        {
            var latitude = (89.5 - row) * Math.PI / 180;
            for (var column = 0; column < 360; column++)
            {
                var longitude = (column + 0.5) * Math.PI / 180;
                values[(row * 360) + column] = (float)(1 - (belts * Math.Pow(Math.Sin(4 * latitude), 2)) + (spots * Math.Cos(6 * longitude) * Math.Pow(Math.Cos(latitude), 2)));
            }
        }
        return new PlanetMap(values, 360, 180);
    }

    // A separable Gaussian of sigma pixels, the edges held.
    private static float[] Blurred(float[] plane, double sigma)
    {
        var reach = (int)Math.Ceiling(4 * sigma);
        var kernel = Enumerable.Range(-reach, (2 * reach) + 1).Select(k => Math.Exp(-k * k / (2 * sigma * sigma))).ToArray();
        var total = kernel.Sum();
        var rows = new float[plane.Length];
        var result = new float[plane.Length];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                double v = 0;
                for (var k = -reach; k <= reach; k++)
                {
                    v += kernel[k + reach] * plane[(y * Width) + Math.Clamp(x + k, 0, Width - 1)];
                }
                rows[(y * Width) + x] = (float)(v / total);
            }
        }
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                double v = 0;
                for (var k = -reach; k <= reach; k++)
                {
                    v += kernel[k + reach] * rows[(Math.Clamp(y + k, 0, Height - 1) * Width) + x];
                }
                result[(y * Width) + x] = (float)(v / total);
            }
        }
        return result;
    }

    private static float[,] ToPlane(float[] values)
    {
        var plane = new float[Height, Width];
        Buffer.BlockCopy(values, 0, plane, 0, values.Length * sizeof(float));
        return plane;
    }
}
