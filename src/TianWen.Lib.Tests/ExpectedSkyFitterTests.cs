using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Grading;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="ExpectedSkyFitter"/> and <see cref="ExpectedSkyStore"/> (#1974) over small synthetic FITS lights whose sky follows a
/// known airmass law: the fit recovers the law, leaves out a <c>BAD_CLOUD_</c> name and a twilight frame, keys two gains apart, and a
/// saved model reads back equal, a second folder merging into the file without disturbing the first.
/// </summary>
[Collection("Imaging")]
public class ExpectedSkyFitterTests(ITestOutputHelper output) : IDisposable
{
    private const double Latitude = -37.9;
    private const double Longitude = 145.2;
    private const int Size = 256;

    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    // The true law: ln(level) = 6.8 + 0.3 X, in counts.
    private static double TrueLogLevel(double airmass) => 6.8 + 0.3 * airmass;

    private static ImageMeta MetaFor(DateTimeOffset when, short gain, double ha, double dec)
    {
        var lst = SiteContext.ComputeLST(when, (float)Longitude);
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
            Latitude = (float)Latitude,
            Longitude = (float)Longitude,
            Gain = gain,
            Offset = 10,
            ObjectName = "Field",
            TargetRA = ra,
            TargetDec = dec,
            PixelSizeX = 3.76f,
            PixelSizeY = 3.76f,
        };
    }

    /// <summary>Writes one light; <paramref name="skyFactor"/> scales its sky (1 = on the law).</summary>
    private static void WriteLight(string path, ImageMeta meta, Random random, double skyFactor = 1.0)
    {
        var airmass = SkyCovariates.From(meta).Airmass;
        var level = Math.Exp(TrueLogLevel(airmass) + 0.01 * (random.NextDouble() - 0.5) * 2) * skyFactor;
        // A tilt that grows with airmass, so the gradient strength is positive and varies.
        var tilt = 0.01 * airmass;
        var data = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                data[y, x] = (float)(level * (1 + tilt * ((double)x / Size - 0.5)) + 2.0 * (random.NextDouble() - 0.5));
            }
        }
        new Image([data], BitDepth.Int16, maxValue: 30000f, minValue: 0f, pedestal: 0f, meta).WriteToFitsFile(path);
    }

    private static DateTimeOffset NightTime(int day, int index)
        // 11:00 to 17:00 UTC on a June night at 145 E (local 21:00 to 03:00): astronomical dark.
        => new DateTimeOffset(2026, 6, day, 11, 0, 0, TimeSpan.Zero).AddMinutes(index * 9);

    private string WriteRig(string name, short gain, int perNight, bool withRejectedAndTwilight)
    {
        var folder = _folders.Create(name).FullName;
        var random = new Random(1974 + gain);
        foreach (var day in new[] { 10, 11 })
        {
            for (var i = 0; i < perNight; i++)
            {
                var ha = -5.0 + 10.0 * i / (perNight - 1);
                var meta = MetaFor(NightTime(day, i), gain, ha, -60);
                WriteLight(Path.Combine(folder, $"Light_{day}_{i:000}.fits"), meta, random);
            }
        }
        if (withRejectedAndTwilight)
        {
            // Three times the sky and named a cloud reject: it must not move the fit.
            WriteLight(Path.Combine(folder, "BAD_CLOUD_Light_cloud.fits"), MetaFor(NightTime(10, 5), gain, 0.5, -60), random, skyFactor: 3.0);
            // 20:30 UTC is 06:30 local, nautical twilight at the site, and its sky is bright.
            WriteLight(Path.Combine(folder, "Light_dawn.fits"), MetaFor(new DateTimeOffset(2026, 6, 10, 20, 30, 0, TimeSpan.Zero), gain, 0.5, -60), random, skyFactor: 5.0);
        }
        return folder;
    }

    private static SkyCovariates At(double airmass)
        => new(0, 0, -2, 0, 0, airmass, false, 0, -20, 0.5, 90, 0, 0, 0);

    [Fact(Timeout = 120_000)]
    public async Task TheFitRecoversTheAirmassLawAndLeavesOutARejectedNameAndATwilightFrame()
    {
        var folder = WriteRig("sky-a", gain: 100, perNight: 20, withRejectedAndTwilight: true);

        var result = await ExpectedSkyFitter.FitAsync([folder], logger: null, cancellationToken: TestContext.Current.CancellationToken);

        result.FilesSeen.ShouldBe(42);
        result.Rejected.ShouldBe(1);
        result.Twilight.ShouldBe(1);
        result.Measured.ShouldBe(40);
        result.Entries.Length.ShouldBe(1);
        var entry = result.Entries[0];
        entry.Nights.ShouldBe(2);
        entry.Key.Rig.ShouldContain("100|10");
        var model = entry.Model;
        output.WriteLine($"terms [{string.Join(' ', model.Terms)}] frames {model.FrameCount}/{model.OfferedCount} level sd {model.LevelScatter:0.0000}");
        model.LevelScatter.ShouldBeLessThan(0.05);
        foreach (var airmass in new[] { 1.2, 1.35, 1.5 })
        {
            model.Expected(At(airmass)).LogLevel.ShouldBe(TrueLogLevel(airmass), 0.05, $"the law at X = {airmass}");
        }
        // A cloud-reject-sized sky reads far above what the fit expects, so leaving it out was what kept the fit on the law.
        model.Excess(At(1.35), Math.Exp(TrueLogLevel(1.35)) * 3, 100).LevelSigma.ShouldBeGreaterThan(10);
    }

    [Fact(Timeout = 120_000)]
    public async Task TwoGainsAreTwoKeysAndTooFewFramesAreNoModel()
    {
        var a = WriteRig("sky-both-a", gain: 100, perNight: 20, withRejectedAndTwilight: false);
        var b = WriteRig("sky-both-b", gain: 200, perNight: 20, withRejectedAndTwilight: false);
        var small = WriteRig("sky-both-c", gain: 300, perNight: 5, withRejectedAndTwilight: false);

        var result = await ExpectedSkyFitter.FitAsync([a, b, small], logger: null, cancellationToken: TestContext.Current.CancellationToken);

        Directory.Exists(a).ShouldBeTrue();
        Directory.Exists(b).ShouldBeTrue();
        Directory.Exists(small).ShouldBeTrue();
        result.Entries.Select(e => e.Key.Rig.Split('|')[0]).Order().ToArray().ShouldBe(["100", "200"]);
        result.TooFew.ShouldBe(1, "ten frames of gain 300 are under the minimum");
    }

    [Fact(Timeout = 120_000)]
    public async Task ASavedModelReadsBackEqualAndASecondFolderMergesWithoutDisturbingTheFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, _folders.Create("sky-store"));
        (await ExpectedSkyStore.LoadAsync(external, ct)).IsDefault.ShouldBeFalse("none saved is empty, not not-asked");
        (await ExpectedSkyStore.LoadAsync(external, ct)).ShouldBeEmpty();

        var first = await ExpectedSkyFitter.FitAsync([WriteRig("sky-m1", gain: 100, perNight: 20, withRejectedAndTwilight: false)], null, cancellationToken: ct);
        await ExpectedSkyStore.SaveAsync(external, first.Entries, ct);
        var afterFirst = await ExpectedSkyStore.LoadAsync(external, ct);
        afterFirst.Length.ShouldBe(1);
        AssertSame(afterFirst[0], first.Entries[0]);

        var second = await ExpectedSkyFitter.FitAsync([WriteRig("sky-m2", gain: 200, perNight: 20, withRejectedAndTwilight: false)], null, cancellationToken: ct);
        var merged = await ExpectedSkyStore.SaveAsync(external, second.Entries, ct);
        merged.Length.ShouldBe(2);
        var reread = await ExpectedSkyStore.LoadAsync(external, ct);
        reread.Length.ShouldBe(2);
        AssertSame(ExpectedSkyStore.Find(reread, first.Entries[0].Key).ShouldNotBeNull(), first.Entries[0]);
        AssertSame(ExpectedSkyStore.Find(reread, second.Entries[0].Key).ShouldNotBeNull(), second.Entries[0]);

        // Saving the same key again replaces it rather than adding a third.
        (await ExpectedSkyStore.SaveAsync(external, second.Entries, ct)).Length.ShouldBe(2);
    }

    private static void AssertSame(ExpectedSkyEntry read, ExpectedSkyEntry written)
    {
        read.Key.ShouldBe(written.Key);
        read.Nights.ShouldBe(written.Nights);
        read.Model.Terms.ShouldBe(written.Model.Terms);
        read.Model.LevelCoefficients.ShouldBe(written.Model.LevelCoefficients);
        read.Model.GradientCoefficients.ShouldBe(written.Model.GradientCoefficients);
        read.Model.LevelScatter.ShouldBe(written.Model.LevelScatter);
        read.Model.GradientScatter.ShouldBe(written.Model.GradientScatter);
        read.Model.FrameCount.ShouldBe(written.Model.FrameCount);
        read.Model.OfferedCount.ShouldBe(written.Model.OfferedCount);
    }
}
