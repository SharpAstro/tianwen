using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.ColorCalibration;
using TianWen.Lib.Imaging.Grading;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The absolute transparency (#1974 section 2): the shared zero-point routine recovers a known zero point from stars matched to a
/// catalogue through a WCS, the fitter recovers the clear zero point and extinction over airmass from header-WCS frames, and a whole
/// run uniformly 40 % dimmer is Cloud against the stored model with no clear frame in it, while a clear run and a run with no WCS keep
/// their verdicts exactly.
/// </summary>
[Collection("Imaging")]
public class PhotometricTransparencyTests(ITestOutputHelper output) : IDisposable
{
    private const int Size = 256;
    private const double PixelScaleDeg = 3.0 / 3600.0;
    private const double CenterRaHours = 12.0;
    private const double CenterDecDeg = -40.0;
    private const double TrueZeroPoint0 = 18.0;
    private const double TrueExtinction = 0.25;

    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static WCS FieldWcs() => new(CenterRA: CenterRaHours, CenterDec: CenterDecDeg)
    {
        CRPix1 = (Size + 1) * 0.5,
        CRPix2 = (Size + 1) * 0.5,
        CD1_1 = -PixelScaleDeg,
        CD1_2 = 0,
        CD2_1 = 0,
        CD2_2 = PixelScaleDeg,
    };

    /// <summary>A catalogue of stars placed on a seeded field, each at the sky position the WCS gives its pixel.</summary>
    private sealed class Field
    {
        public required ICelestialObjectDB Db { get; init; }
        public required (double X, double Y, double V)[] Stars { get; init; }
    }

    private static Field BuildField(int count, int seed)
    {
        var wcs = FieldWcs();
        var random = new Random(seed);
        var byIndex = new Dictionary<CatalogIndex, CelestialObject>();
        var byCell = new Dictionary<(int, int), List<CatalogIndex>>();
        var stars = new (double X, double Y, double V)[count];
        static (int, int) Cell(double raHours, double decDeg) => ((int)Math.Floor(raHours * 60.0 * 15.0 / 6.0), (int)Math.Floor(decDeg * 60.0 / 6.0));
        for (var i = 0; i < count; i++)
        {
            // Away from the edge and from each other (a 24 px grid cell holds at most one), so a measured aperture holds one star.
            var x = 20 + (i % 9) * 26 + random.Next(-3, 4);
            var y = 20 + (i / 9) * 26 + random.Next(-3, 4);
            var v = 8.0 + 3.0 * random.NextDouble();
            stars[i] = (x, y, v);
            var sky = wcs.PixelToSky(x, y)!.Value;
            var idx = (CatalogIndex)(ulong)(i + 1);
            byIndex[idx] = new CelestialObject(idx, ObjectType.Star, sky.RA, sky.Dec, Constellation.Centaurus, (Half)(float)v, Half.NaN, (Half)0.6f, new HashSet<string>());
            var key = Cell(sky.RA, sky.Dec);
            if (!byCell.TryGetValue(key, out var list))
            {
                byCell[key] = list = [];
            }
            list.Add(idx);
        }
        var grid = Substitute.For<IRaDecIndex>();
        grid[Arg.Any<double>(), Arg.Any<double>()].Returns(call =>
        {
            var key = Cell((double)call[0], (double)call[1]);
            var result = new List<CatalogIndex>();
            for (var dRa = -1; dRa <= 1; dRa++)
            {
                for (var dDec = -1; dDec <= 1; dDec++)
                {
                    if (byCell.TryGetValue((key.Item1 + dRa, key.Item2 + dDec), out var list))
                    {
                        result.AddRange(list);
                    }
                }
            }
            return (IReadOnlyCollection<CatalogIndex>)result;
        });
        var db = Substitute.For<ICelestialObjectDB>();
        db.CoordinateGrid.Returns(grid);
        db.TryLookupByIndex(Arg.Any<CatalogIndex>(), out Arg.Any<CelestialObject>()).Returns(call =>
        {
            if (byIndex.TryGetValue((CatalogIndex)call[0], out var obj))
            {
                call[1] = obj;
                return true;
            }
            call[1] = default(CelestialObject);
            return false;
        });
        return new Field { Db = db, Stars = stars };
    }

    private static float FluxAt(double v, double zeroPoint) => (float)Math.Pow(10.0, (zeroPoint - v) / 2.5);

    [Fact]
    public void TheSharedRoutineRecoversAKnownZeroPoint()
    {
        var field = BuildField(54, seed: 1);
        var bag = new ConcurrentBag<ImagedStar>();
        foreach (var (x, y, v) in field.Stars)
        {
            bag.Add(new ImagedStar(2.5f, 3.0f, 50f, FluxAt(v, 23.0), (float)x, (float)y, 0.05f));
        }

        PhotometricZeroPoint.TryMeasure(new StarList(bag), FieldWcs(), field.Db, Size, Size, 0.0, out var result).ShouldBeTrue();

        result.ZeroPoint.ShouldBe(23.0f, 0.01f);
        result.Stars.ShouldBeGreaterThanOrEqualTo(PhotometricZeroPoint.MinStars);
        result.Scatter.ShouldBeLessThan(0.05f);
    }

    [Fact]
    public void TooFewMatchesOrNoWcsReadsNoZeroPoint()
    {
        var field = BuildField(54, seed: 1);
        var bag = new ConcurrentBag<ImagedStar>(field.Stars.Take(10).Select(s => new ImagedStar(2.5f, 3.0f, 50f, FluxAt(s.V, 23.0), (float)s.X, (float)s.Y, 0.05f)));
        PhotometricZeroPoint.TryMeasure(new StarList(bag), FieldWcs(), field.Db, Size, Size, 0.0, out _).ShouldBeFalse("ten stars are too few");

        var all = new ConcurrentBag<ImagedStar>(field.Stars.Select(s => new ImagedStar(2.5f, 3.0f, 50f, FluxAt(s.V, 23.0), (float)s.X, (float)s.Y, 0.05f)));
        PhotometricZeroPoint.TryMeasure(new StarList(all), default, field.Db, Size, Size, 0.0, out _).ShouldBeFalse("a default WCS has no CD matrix");
    }

    private static SkyCovariates At(double airmass)
        => new(0, -30, 0, 0, 180, airmass, false, 0, -30, 0, 90, 0, 0, 0);

    /// <summary>The model's zero-point line is fitted from clear frames, a dim minority clipped away, and reads a 40 % dimmer frame as 0.6.</summary>
    [Fact]
    public void TheModelFitsTheClearZeroPointAndExtinctionOverAirmass()
    {
        var random = new Random(5);
        var samples = new List<SkySample>();
        foreach (var night in LightGradingTests.SyntheticSkyNight(300, seed: 3))
        {
            var x = night.Covariates.Airmass;
            var zp = TrueZeroPoint0 - TrueExtinction * x + 0.02 * (random.NextDouble() - 0.5) * 2;
            // One frame in seven is dim by half a magnitude: the fit must not take it as clear.
            if (samples.Count % 7 == 3)
            {
                zp -= 0.5;
            }
            samples.Add(new SkySample(night.Covariates, night.Surface.Level, night.Surface.GradientStrength, zp));
        }

        var model = ExpectedSkyModel.Fit(samples) ?? throw new InvalidOperationException("fits");

        model.HasZeroPoint.ShouldBeTrue();
        output.WriteLine($"zp0 {model.ZeroPoint0:0.000} k {model.Extinction:0.000} scatter {model.ZeroPointScatter:0.000} kept {model.ZeroPointCount}");
        model.ZeroPoint0.ShouldBe(TrueZeroPoint0, 0.03);
        model.Extinction.ShouldBe(TrueExtinction, 0.03);
        model.ZeroPointScatter.ShouldBeLessThan(0.05);
        model.Transparency(At(1.4), TrueZeroPoint0 - TrueExtinction * 1.4).ShouldBe(1.0, 0.03);
        model.Transparency(At(1.4), TrueZeroPoint0 - TrueExtinction * 1.4 - 2.5 * Math.Log10(1 / 0.6)).ShouldBe(0.6, 0.02);
        float.IsNaN((float)model.Transparency(At(1.4), double.NaN)).ShouldBeTrue();

        var noWcs = ExpectedSkyModel.Fit([.. samples.Select(static s => s with { ZeroPoint = double.NaN })]);
        noWcs.ShouldNotBeNull();
        noWcs.HasZeroPoint.ShouldBeFalse("no frame had a WCS");
        float.IsNaN((float)noWcs.Transparency(At(1.4), 17.0)).ShouldBeTrue();
    }

    private static ImmutableArray<ExpectedSkyEntry> StoredModel(LightGrade[] clearNights, out ExpectedSkyKey key)
    {
        var random = new Random(9);
        var samples = clearNights.Select(g => new SkySample(g.Covariates, g.Surface.Level, g.Surface.GradientStrength,
            TrueZeroPoint0 - TrueExtinction * g.Covariates.Airmass + 0.02 * (random.NextDouble() - 0.5) * 2)).ToList();
        var model = ExpectedSkyModel.Fit(samples) ?? throw new InvalidOperationException("the clear nights fit");
        var first = clearNights[0];
        key = new ExpectedSkyKey(first.SiteLatitude, first.SiteLongitude, first.Rig, first.Key.SensorType, first.Key.FilterKey, first.Key.ExposureMs, first.Key.Bin);
        return [new ExpectedSkyEntry(key, model, 2)];
    }

    private static ImmutableDictionary<string, LightGrade> Table(IEnumerable<LightGrade> frames)
        => frames.Select((g, i) => ($"f{i:000}", g)).ToImmutableDictionary(e => e.Item1, e => e.g, StringComparer.OrdinalIgnoreCase);

    private static LightGrade[] RunWithZeroPoint(int seed, double transmitted, bool hasWcs)
    {
        var random = new Random(seed);
        return [.. LightGradingTests.SyntheticSkyNight(120, seed: seed).Select(g => g with
        {
            ZeroPoint = hasWcs
                ? (float)(TrueZeroPoint0 - TrueExtinction * g.Covariates.Airmass + 2.5 * Math.Log10(transmitted) + 0.02 * (random.NextDouble() - 0.5) * 2)
                : float.NaN,
        })];
    }

    /// <summary>A whole night 40 % dimmer, every frame of it equally bad and no clear frame among them, is Cloud against the stored zero point.</summary>
    [Fact]
    public void AWholeRunFortyPercentDimmerIsCloudWithNoClearFrameInIt()
    {
        var stored = StoredModel([.. LightGradingTests.SyntheticSkyNight(300, seed: 3), .. LightGradingTests.SyntheticSkyNight(300, seed: 5)], out _);
        var dim = Table(RunWithZeroPoint(seed: 11, transmitted: 0.6, hasWcs: true));

        var verdicts = dim.Keys.Select(n => LightGrading.Verdict(dim, dim[n], expectedSky: stored)).ToArray();

        verdicts.ShouldAllBe(v => v.Causes.HasFlag(GradeCause.Cloud));
        verdicts.ShouldAllBe(v => v.Shown <= LightGrading.PoorBelow);
        var sample = LightGrading.Verdict(dim, dim["f040"], expectedSky: stored);
        sample.Transparency.ShouldBe(0.6f, 0.05f);
        output.WriteLine(LightGrading.CloudInWords(sample));
        // The frames scatter by +-0.02 mag about 60 %, so a named frame reads 59 to 62.
        LightGrading.CloudInWords(sample).ShouldMatch(@"transmits (59|60|61|62) % of a clear night's light");

        // Without the stored model the run is only its own reference, which is #1976's relative guard and no part of this one.
        var alone = LightGrading.Verdict(dim, dim["f040"]);
        float.IsNaN((float)alone.Transparency).ShouldBeTrue();
        alone.Causes.ShouldBe(GradeCause.None, "a run is never its own transparency reference");
    }

    [Fact]
    public void AClearRunAndARunWithNoWcsKeepTheirVerdicts()
    {
        var stored = StoredModel([.. LightGradingTests.SyntheticSkyNight(300, seed: 3), .. LightGradingTests.SyntheticSkyNight(300, seed: 5)], out _);
        var clear = Table(RunWithZeroPoint(seed: 13, transmitted: 1.0, hasWcs: true));
        var noWcs = Table(RunWithZeroPoint(seed: 13, transmitted: 0.6, hasWcs: false));
        var noZeroPointStored = ImmutableArray.Create(new ExpectedSkyEntry(stored[0].Key, stored[0].Model with { ZeroPointCount = 0, ZeroPoint0 = double.NaN, Extinction = double.NaN }, 2));

        foreach (var name in clear.Keys)
        {
            var v = LightGrading.Verdict(clear, clear[name], expectedSky: stored);
            v.Transparency.ShouldBeGreaterThan(0.85f, name);
            v.Causes.ShouldBe(GradeCause.None, name);
            var without = clear.ToImmutableDictionary(static e => e.Key, static e => e.Value with { ZeroPoint = float.NaN }, StringComparer.OrdinalIgnoreCase);
            v.ShouldBe(LightGrading.Verdict(without, without[name], expectedSky: stored) with { Transparency = v.Transparency });

            // A frame with a zero point against a rig whose stored model has none reads exactly as one without.
            LightGrading.Verdict(clear, clear[name], expectedSky: noZeroPointStored)
                .ShouldBe(LightGrading.Verdict(without, without[name], expectedSky: noZeroPointStored));
        }
        foreach (var name in noWcs.Keys)
        {
            var v = LightGrading.Verdict(noWcs, noWcs[name], expectedSky: stored);
            float.IsNaN((float)v.Transparency).ShouldBeTrue(name);
            v.Causes.HasFlag(GradeCause.Cloud).ShouldBeFalse(name);
        }
    }

    // ---- the fitter, over FITS lights with a header WCS and stars rendered at a known zero point ----

    private static ImageMeta MetaFor(DateTimeOffset when, double ha)
    {
        var lst = SiteContext.ComputeLST(when, 145.2f);
        var ra = ((lst - ha) % 24.0 + 24.0) % 24.0;
        return new ImageMeta
        {
            Instrument = "Synthetic",
            Telescope = "Scope",
            ExposureStartTime = when,
            ExposureDuration = TimeSpan.FromSeconds(60),
            FrameType = FrameType.Light,
            Filter = Filter.Luminance,
            BinX = 1,
            BinY = 1,
            SensorType = SensorType.Monochrome,
            Latitude = -37.9f,
            Longitude = 145.2f,
            Gain = 100,
            Offset = 10,
            ObjectName = "Field",
            TargetRA = ra,
            TargetDec = -60,
            PixelSizeX = 3.76f,
            PixelSizeY = 3.76f,
        };
    }

    private static void WriteStarLight(string path, ImageMeta meta, Field field, double transmitted, bool withWcs, Random random)
    {
        var airmass = SkyCovariates.From(meta).Airmass;
        var zeroPoint = TrueZeroPoint0 - TrueExtinction * airmass + 2.5 * Math.Log10(transmitted);
        var level = Math.Exp(6.8 + 0.3 * airmass);
        var data = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                data[y, x] = (float)(level * (1 + 0.01 * airmass * ((double)x / Size - 0.5)) + 2.0 * (random.NextDouble() - 0.5));
            }
        }
        const double sigma = 1.8;
        foreach (var (sx, sy, v) in field.Stars)
        {
            var amplitude = FluxAt(v, zeroPoint) / (2 * Math.PI * sigma * sigma);
            for (var y = (int)sy - 8; y <= (int)sy + 8; y++)
            {
                for (var x = (int)sx - 8; x <= (int)sx + 8; x++)
                {
                    var r2 = (x - sx) * (x - sx) + (y - sy) * (y - sy);
                    data[y, x] += (float)(amplitude * Math.Exp(-r2 / (2 * sigma * sigma)));
                }
            }
        }
        var image = new Image([data], BitDepth.Int16, maxValue: 30000f, minValue: 0f, pedestal: 0f, meta);
        image.WriteToFitsFile(path, withWcs ? FieldWcs() : null);
    }

    [Fact(Timeout = 240_000)]
    public async Task TheFitterRecoversTheClearZeroPointAndExtinctionFromFramesWithAHeaderWcs()
    {
        var field = BuildField(54, seed: 2);
        var folder = _folders.Create("zp").FullName;
        var random = new Random(77);
        const int perNight = 24;
        foreach (var day in new[] { 10, 11 })
        {
            for (var i = 0; i < perNight; i++)
            {
                var when = new DateTimeOffset(2026, 6, day, 11, 0, 0, TimeSpan.Zero).AddMinutes(i * 9);
                var ha = -5.0 + 10.0 * i / (perNight - 1);
                // A frame in eight is half a magnitude dim (a cloud frame the name did not catch); one in six has no WCS at all.
                var dim = i % 8 == 3 ? 0.63 : 1.0;
                WriteStarLight(Path.Combine(folder, $"Light_{day}_{i:000}.fits"), MetaFor(when, ha), field, dim, withWcs: i % 6 != 5, random);
            }
        }

        var result = await ExpectedSkyFitter.FitAsync([folder], logger: null, cancellationToken: TestContext.Current.CancellationToken, catalogue: field.Db);

        result.Entries.Length.ShouldBe(1);
        result.WithZeroPoint.ShouldBeGreaterThan(30);
        result.WithZeroPoint.ShouldBeLessThan(result.Measured, "frames with no WCS read no zero point");
        var model = result.Entries[0].Model;
        output.WriteLine($"zp0 {model.ZeroPoint0:0.000} k {model.Extinction:0.000} scatter {model.ZeroPointScatter:0.000} kept {model.ZeroPointCount} of {result.WithZeroPoint}");
        model.HasZeroPoint.ShouldBeTrue();
        model.Extinction.ShouldBe(TrueExtinction, 0.04);
        // The detector's flux is the star's up to a constant fraction of the profile, so the zero point is the truth up to that offset.
        model.ZeroPoint0.ShouldBe(TrueZeroPoint0, 0.35);
        model.ZeroPointScatter.ShouldBeLessThan(0.15);

        // A frame with the rig's clear light reads about 1, a 40 % dimmer one about 0.6.
        var x = 1.4;
        var clearZp = model.ZeroPoint0 - model.Extinction * x;
        model.Transparency(At(x), clearZp).ShouldBe(1.0, 0.01);
        model.Transparency(At(x), clearZp + 2.5 * Math.Log10(0.6)).ShouldBe(0.6, 0.01);
    }

    [Fact]
    public async Task WithoutACatalogueTheFitterReadsNoZeroPoint()
    {
        var field = BuildField(54, seed: 2);
        var folder = _folders.Create("nocat").FullName;
        var random = new Random(3);
        foreach (var day in new[] { 10, 11 })
        {
            for (var i = 0; i < 16; i++)
            {
                var when = new DateTimeOffset(2026, 6, day, 11, 0, 0, TimeSpan.Zero).AddMinutes(i * 9);
                WriteStarLight(Path.Combine(folder, $"Light_{day}_{i:000}.fits"), MetaFor(when, -5.0 + 10.0 * i / 15), field, 1.0, withWcs: true, random);
            }
        }

        var result = await ExpectedSkyFitter.FitAsync([folder], logger: null, cancellationToken: TestContext.Current.CancellationToken);

        result.WithZeroPoint.ShouldBe(0);
        result.Entries.Length.ShouldBe(1);
        result.Entries[0].Model.HasZeroPoint.ShouldBeFalse();
    }
}
