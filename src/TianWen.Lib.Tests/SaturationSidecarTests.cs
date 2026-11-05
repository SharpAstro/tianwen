using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.ColorCalibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// SPCC's clip gate is told which master pixels were saturated in the SUBS (#1991): a star clipped in every sub is smeared
/// by registration and rejection to a peak well under the master's own, so the peak gate lets it through with a colour
/// pulled toward neutral, and it drags the whole fit toward identity. The master's <c>.saturation.fits</c> sidecar carries
/// the per-pixel count of saturated subs, and a star whose aperture touches a nonzero pixel is dropped.
/// </summary>
[Collection("Scheduling")]
public sealed class SaturationSidecarTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private const int Size = Tycho2MatchStarsTests.ImageWidth;
    private const int GridSide = 7;
    private const float Sky = 100f;
    private const float Sigma = 1.6f;

    /// <summary>The colour a star of this B-V shows through the (made up) true sensor, per channel.</summary>
    private static (float R, float G, float B) TrueColour(int index)
    {
        var bv = 0.2f + (0.1f * (index % 9));
        return (1.6f - (0.8f * bv), 1f, 0.7f + (0.9f * (1f - bv)));
    }

    private static bool IsSaturatedInSubs(int index) => index % 2 == 0;

    /// <summary>Colour pulled 60 percent of the way to neutral: what a star clipped in the subs shows in the master.</summary>
    private static (float R, float G, float B) Compressed((float R, float G, float B) c)
        => (1f + ((c.R - 1f) * 0.4f), 1f, 1f + ((c.B - 1f) * 0.4f));

    private static ImageMeta Meta() => new() { Instrument = "synth", SensorType = SensorType.Color };

    /// <summary>A 49-star field on the shared fake catalogue. <paramref name="smear"/> renders the even-indexed stars with their
    /// colour compressed and returns, beside the master and the star list, the saturation map that marks them.</summary>
    private static (Image Master, StarList Stars, ICelestialObjectDB Db, WCS Wcs, Image Saturation) BuildField(bool smear)
    {
        var wcs = Tycho2MatchStarsTests.BuildTestWcs();
        var catalogue = new Tycho2MatchStarsTests.FakeCatalog();
        var detections = new ConcurrentBag<ImagedStar>();
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    planes[c][y, x] = Sky;
                }
            }
        }
        var saturation = new float[Size, Size];

        for (var i = 0; i < GridSide * GridSide; i++)
        {
            var (px, py) = (100f + ((i % GridSide) * 130f), 100f + ((i / GridSide) * 130f));
            var vMag = 9f + (0.05f * i);
            Tycho2MatchStarsTests.AddCleanPair(wcs, detections, catalogue, px, py, vMag, bMinusV: 0.2f + (0.1f * (i % 9)));

            var colour = TrueColour(i);
            var saturated = IsSaturatedInSubs(i);
            if (smear && saturated)
            {
                colour = Compressed(colour);
            }
            var amplitude = 2000f * MathF.Pow(10f, -0.4f * (vMag - 9f));
            float[] gains = [colour.R, colour.G, colour.B];
            for (var y = (int)py - 10; y <= (int)py + 10; y++)
            {
                for (var x = (int)px - 10; x <= (int)px + 10; x++)
                {
                    var d2 = ((x - px) * (x - px)) + ((y - py) * (y - py));
                    var profile = MathF.Exp(-d2 / (2f * Sigma * Sigma));
                    for (var c = 0; c < 3; c++)
                    {
                        planes[c][y, x] += amplitude * gains[c] * profile;
                    }
                    if (saturated && d2 <= 9f)
                    {
                        saturation[y, x] = 3f;
                    }
                }
            }
        }

        // One star far brighter than any matched one, in no catalogue: it sets the frame's peak, so the smeared stars sit well
        // under 0.98 of it and the peak gate cannot see them, which is the failure being pinned.
        for (var c = 0; c < 3; c++)
        {
            planes[c][Size - 20, Size - 20] = 500_000f;
        }

        var master = new Image(planes, BitDepth.Float32, 500_000f, 0f, 0f, Meta());
        var map = new Image([saturation], BitDepth.Float32, 3f, 0f, 0f, Meta());
        return (master, new StarList(detections), catalogue.Build(), wcs, map);
    }

    private static async Task<Tycho2ColorCalibration.SpccWhiteBalanceResult> FitAsync(Image master, StarList stars, ICelestialObjectDB db, WCS wcs, Image? saturation)
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);
        var r = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader R").ShouldNotBeNull();
        var g = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader G").ShouldNotBeNull();
        var b = FilterCurveDatabase.ComputeSystemThroughput("IMX533", "Baader B").ShouldNotBeNull();
        return Tycho2ColorCalibration.ComputeSpectrophotometricWhiteBalance(
            master, stars, wcs, db, r, g, b, saturationMap: saturation).ShouldNotBeNull();
    }

    [Fact]
    public async Task StarsSaturatedInTheSubsAreDroppedAndTheFitMovesBackToTheTruth()
    {
        var (clean, cleanStars, cleanDb, wcs, cleanMap) = BuildField(smear: false);
        var truth = await FitAsync(clean, cleanStars, cleanDb, wcs, saturation: null);

        var (smeared, stars, db, _, map) = BuildField(smear: true);
        var without = await FitAsync(smeared, stars, db, wcs, saturation: null);
        var with = await FitAsync(smeared, stars, db, wcs, map);

        // The peak gate is blind to them (that is the premise), so without the map the fit is pulled off the truth.
        var driftWithout = Math.Max(Math.Abs((without.R / truth.R) - 1.0), Math.Abs((without.B / truth.B) - 1.0));
        driftWithout.ShouldBeGreaterThan(0.05, "the smeared stars must bias the fit when nothing tells SPCC they were clipped");

        // With the map those stars are gone and the fit is the truth's, by the stars that remain.
        with.R.ShouldBe(truth.R, truth.R * 0.03f);
        with.B.ShouldBe(truth.B, truth.B * 0.03f);
        with.FinalMatches.ShouldBeLessThan(without.FinalMatches);
        _ = cleanMap;
    }

    [Fact]
    public async Task AMasterWithNoMapKeepsTodaysBehaviourBitForBit()
    {
        var (smeared, stars, db, wcs, _) = BuildField(smear: true);
        var plain = await FitAsync(smeared, stars, db, wcs, saturation: null);
        var emptyMap = new Image([new float[Size, Size]], BitDepth.Float32, 1f, 0f, 0f, Meta());

        var withEmptyMap = await FitAsync(smeared, stars, db, wcs, emptyMap);

        withEmptyMap.R.ShouldBe(plain.R);
        withEmptyMap.G.ShouldBe(plain.G);
        withEmptyMap.B.ShouldBe(plain.B);
        withEmptyMap.FinalMatches.ShouldBe(plain.FinalMatches);
    }

    [Fact]
    public void ThePostProcessorFindsTheSidecarBesideTheMasterAndCropsItWithTheMaster()
    {
        var dir = _folders.Create("saturation-sidecar-").FullName;
        var masterPath = Path.Combine(dir, "master_M83.fits");
        var counts = new float[40, 60];
        counts[10, 20] = 3f;
        counts[11, 21] = 1f;
        var map = new Image([counts], BitDepth.Float32, 3f, 0f, 0f, Meta());

        MasterPostProcessor.LoadSaturationMap(masterPath, crop: null).ShouldBeNull("a master with no sidecar keeps the peak gate alone");

        IntegrationFitsWriter.WriteSaturationMap(masterPath, map, frameCount: 3);

        var written = IntegrationFitsWriter.ExistingSidecarPath(IntegrationFitsWriter.SaturationPathFor(masterPath)).ShouldNotBeNull();
        written.ShouldEndWith(IntegrationFitsWriter.SaturationMapSuffix + Image.GzipSuffix);
        IntegrationFitsWriter.IsMapSidecarPath(written).ShouldBeTrue("a folder walk must not count it as a master");
        IntegrationFitsWriter.IsMapSidecarPath(masterPath).ShouldBeFalse();

        IntegrationFitsWriter.TryReadSaturationMap(masterPath, out var read).ShouldBeTrue();
        (read.Width, read.Height, read.ChannelCount).ShouldBe((60, 40, 1));
        read[0, 10, 20].ShouldBe(3f, "a count is stored exactly");
        read[0, 11, 21].ShouldBe(1f);
        read[0, 0, 0].ShouldBe(0f);

        var loaded = MasterPostProcessor.LoadSaturationMap(masterPath, crop: null).ShouldNotBeNull();
        (loaded.Width, loaded.Height).ShouldBe((60, 40));

        var cropped = MasterPostProcessor.LoadSaturationMap(masterPath, new PixelRect(18, 8, 10, 10)).ShouldNotBeNull();
        (cropped.Width, cropped.Height).ShouldBe((10, 10));
        cropped[0, 2, 2].ShouldBe(3f, "the crop keeps the map over the same pixels as the cropped master");
    }

    [Fact]
    public async Task TheMastersSolveDropsTheStarsTheSidecarMarks()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);
        var (smeared, _, db, wcs, map) = BuildField(smear: true);
        var meta = new ImageMeta { Instrument = "SVBONY SV605CC", SensorType = SensorType.Color, Filter = Filter.FromName("Optolong L-Quad Enhance") };
        var renderer = new MasterPreviewRenderer(db, NullLogger.Instance);

        var without = await renderer.SolveWhiteBalanceAsync(smeared, wcs, meta, TestContext.Current.CancellationToken);
        var with = await renderer.SolveWhiteBalanceAsync(smeared, wcs, meta, TestContext.Current.CancellationToken, map);

        without.Spcc.ShouldNotBeNull("the field must solve without the map for the comparison to mean anything");
        with.Spcc.ShouldNotBeNull();
        with.Spcc!.Value.FinalMatches.ShouldBeLessThan(without.Spcc!.Value.FinalMatches);
    }
}
