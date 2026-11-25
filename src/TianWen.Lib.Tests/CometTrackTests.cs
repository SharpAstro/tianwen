using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A comet's track kept as a file (<see cref="CometTrack"/>, #2489): what a comet stack registers on with no network,
/// read by its bytes like any original, so one workbench key always means one ephemeris. The file reads back as it was
/// written, says nothing of when, refuses itself in words where it is no track, and refuses frames it was not asked for.
/// </summary>
public sealed class CometTrackTests : IDisposable
{
    private static readonly DateTimeOffset Night = new(2026, 8, 16, 20, 0, 0, TimeSpan.Zero);

    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static CometTrack Track(int samples = 5, double lat = 48.2, double lon = 16.3) => new(
        CometTrack.CurrentSchemaVersion, CometTrack.Horizons, "10P", lat, lon, 200,
        [.. Enumerable.Range(0, samples).Select(static i => new EphemerisSample(Night.AddMinutes(30 * i), 300.1 + 0.01 * i, -12.5 + 0.005 * i))]);

    [Fact]
    public async Task ATrackReadsBackAsItWasWrittenAndOneAnswerIsOneSetOfBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("track-").FullName;
        var (a, b) = (Path.Combine(folder, "a.json"), Path.Combine(folder, "b.json"));
        var track = Track();

        await track.WriteAsync(a, ct);
        await track.WriteAsync(b, ct);

        File.ReadAllBytes(a).ShouldBe(File.ReadAllBytes(b), "nothing in the file says when it was written, so one answer keeps one step key");
        CometTrack.TryRead(a, out var read, out var refusal).ShouldBeTrue(refusal);
        read.Designation.ShouldBe("10P");
        read.Source.ShouldBe(CometTrack.Horizons);
        (read.SiteLatDeg, read.SiteLonDeg, read.SiteElevMetres).ShouldBe((48.2, 16.3, 200.0));
        read.Samples.ShouldBe(track.Samples, "every position to the last bit, and each instant");
        File.Exists(a + ".tmp").ShouldBeFalse("the file is written whole and then put in place");
    }

    [Fact]
    public async Task ATrackWrittenOutOfOrderIsReadInTimeOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_folders.Create("order-").FullName, "track.json");
        var track = Track();
        await (track with { Samples = track.Samples.Reverse().ToImmutableArray() }).WriteAsync(path, ct);

        CometTrack.TryRead(path, out var read, out var refusal).ShouldBeTrue(refusal);

        read.Samples.ShouldBe(track.Samples, "the first and last samples are the window's ends whatever order the file holds");
    }

    [Theory]
    [InlineData("not json at all", "is no comet track")]
    [InlineData("""{ "SchemaVersion": 2, "Source": "JPL Horizons", "Designation": "10P", "SiteLatDeg": 48.2, "SiteLonDeg": 16.3, "SiteElevMetres": 200, "Samples": [] }""", "of shape 2")]
    [InlineData("""{ "SchemaVersion": 1, "Source": "JPL Horizons", "Designation": " ", "SiteLatDeg": 48.2, "SiteLonDeg": 16.3, "SiteElevMetres": 200, "Samples": [] }""", "names no comet")]
    [InlineData("""{ "SchemaVersion": 1, "Source": "JPL Horizons", "Designation": "10P", "SiteLatDeg": 48.2, "SiteLonDeg": 16.3, "SiteElevMetres": 200, "Samples": [ { "TimeUtc": "2026-08-16T20:00:00+00:00", "RaDeg": 300.1, "DecDeg": -12.5 } ] }""", "a rate needs two instants")]
    [InlineData("""{ "SchemaVersion": 1, "Source": "JPL Horizons", "Designation": "10P", "SiteLatDeg": 48.2, "SiteLonDeg": 16.3, "SiteElevMetres": 200, "Samples": [ { "TimeUtc": "2026-08-16T20:00:00+00:00", "RaDeg": 300.1, "DecDeg": -12.5 }, { "TimeUtc": "2026-08-16T20:00:00+00:00", "RaDeg": 300.2, "DecDeg": -12.4 } ] }""", "a rate needs two instants")]
    public void AFileThatIsNoTrackIsRefusedInWords(string text, string said)
    {
        var path = Path.Combine(_folders.Create("bad-").FullName, "track.json");
        File.WriteAllText(path, text);

        CometTrack.TryRead(path, out var read, out var refusal).ShouldBeFalse();

        read.ShouldBeNull();
        refusal.ShouldContain(said);
    }

    [Fact]
    public void AMissingFileIsNoTrack()
    {
        var path = Path.Combine(_folders.Create("missing-").FullName, "none.json");

        CometTrack.TryRead(path, out _, out var refusal).ShouldBeFalse();

        refusal.ShouldBe($"there is no comet track at {path}");
    }

    [Fact]
    public void ATrackServesFramesItCoversFromItsOwnSiteAndRefusesAnyOthersInWords()
    {
        var track = Track(); // 20:00 to 22:00, from (48.2, 16.3)
        var (from, to) = (Night.AddMinutes(10), Night.AddMinutes(110));

        track.RefusalFor(48.2, 16.3, from, to).ShouldBeNull();
        track.RefusalFor(48.25, 16.35, from, to).ShouldBeNull("a few kilometres move the comet by a thousandth of a pixel");
        track.RefusalFor(49.2, 16.3, from, to).ShouldNotBeNull().ShouldContain("the one seen from the frames' own site");
        track.RefusalFor(float.NaN, float.NaN, from, to).ShouldNotBeNull().ShouldContain("state no site");
        track.RefusalFor(48.2, 16.3, from, Night.AddMinutes(150)).ShouldNotBeNull().ShouldContain("it has to cover every frame");
        track.RefusalFor(48.2, 16.3, Night.AddMinutes(-5), to).ShouldNotBeNull().ShouldContain("it has to cover every frame");

        // The antimeridian: 179.99 east and 179.99 west are 0.02 degrees apart.
        Track(lon: 179.99).RefusalFor(48.2, -179.99, from, to).ShouldBeNull();
    }

    [Fact]
    public void AGroupFitsOnlyTheSamplesAroundItsWindowPaddedByOneFileStep()
    {
        var track = Track(samples: 9); // 20:00 to 00:00, a sample every 30 minutes

        var around = track.SamplesAround(Night.AddMinutes(100), Night.AddMinutes(130));

        // 100..130 padded by one 30-minute step is 70..160.
        around.Select(static s => s.TimeUtc).ShouldBe([Night.AddMinutes(90), Night.AddMinutes(120), Night.AddMinutes(150)]);
        track.SamplesAround(Night.AddMinutes(-600), Night.AddMinutes(600)).Length.ShouldBe(9);
        track.SamplesAround(Night.AddMinutes(1000), Night.AddMinutes(1010)).ShouldBeEmpty();
    }

    [Fact]
    public void LongitudesApartWrapAtTheAntimeridian()
    {
        CometTrack.LongitudeApartDeg(179.99, -179.99).ShouldBe(0.02, 1e-9);
        CometTrack.LongitudeApartDeg(16.3, 16.0).ShouldBe(0.3, 1e-9);
    }

    [Fact]
    public async Task ATrackIsWrittenWithLineFeedsAndInAFolderThatDidNotExist()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_folders.Create("track-").FullName, "not", "yet", "10P.json");

        await Track().WriteAsync(path, ct);

        var bytes = File.ReadAllBytes(path);
        bytes.ShouldNotContain((byte)'\r', "one answer is one set of bytes on every machine");
        bytes.ShouldContain((byte)'\n');
    }

    [Fact]
    public void ATrackFileThatCannotBeReadIsRefusedInWordsNotThrown()
    {
        var path = Path.Combine(_folders.Create("track-").FullName, "10P.json");
        File.WriteAllText(path, "{}");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        // A file another process holds open is refused in words (on Linux it reads fine, so only the Windows lock is asserted).
        if (OperatingSystem.IsWindows())
        {
            CometTrack.TryRead(path, out _, out var refusal).ShouldBeFalse();
            refusal.ShouldContain("cannot be read");
        }
    }

    [Fact]
    public void TheRequestIsTheNightsWindowFromItsLightsOwnSiteAndTheCometTheyName()
    {
        var folder = _folders.Create("night-").FullName;
        var lights = Directory.CreateDirectory(Path.Combine(folder, "LIGHT")).FullName;
        WriteLight(Path.Combine(lights, "a.fits"), Night, "10P");
        WriteLight(Path.Combine(lights, "b.fits"), Night.AddMinutes(40), "10P");
        WriteLight(Path.Combine(folder, "dark.fits"), Night.AddHours(-3), "10P", FrameType.Dark);

        CometTrackFetch.TryRequestFor(folder, designation: null, out var request, out var lightCount, out var refusal).ShouldBeTrue(refusal);

        lightCount.ShouldBe(2, "the lights alone, never a dark");
        request.Designation.ShouldBe("10P");
        request.SiteLatDeg.ShouldBe(48.2, 1e-4);
        request.SiteLonDeg.ShouldBe(16.3, 1e-4);
        // Padded by one step past the first light's start and the last light's end, as the stack's own request is.
        request.Start.ShouldBe(Night - request.Step);
        request.Stop.ShouldBe(Night.AddMinutes(40) + TimeSpan.FromSeconds(60) + request.Step);
    }

    [Fact]
    public void LightsOfTwoCometsWantTheOneNamedAndLightsFromTwoSitesAreRefused()
    {
        var two = _folders.Create("two-").FullName;
        WriteLight(Path.Combine(two, "a.fits"), Night, "10P");
        WriteLight(Path.Combine(two, "b.fits"), Night.AddMinutes(5), "C/2025 R2");

        CometTrackFetch.TryRequestFor(two, designation: null, out _, out _, out var refusal).ShouldBeFalse();
        refusal.ShouldContain("name the comet");
        CometTrackFetch.TryRequestFor(two, designation: "10P", out var named, out _, out var none).ShouldBeTrue(none);
        named.Designation.ShouldBe("10P");

        var sites = _folders.Create("sites-").FullName;
        WriteLight(Path.Combine(sites, "a.fits"), Night, "10P");
        WriteLight(Path.Combine(sites, "b.fits"), Night.AddMinutes(5), "10P", latitude: 40f);

        CometTrackFetch.TryRequestFor(sites, designation: null, out _, out _, out var twoSites).ShouldBeFalse();
        twoSites.ShouldContain("more than one site");

        var empty = _folders.Create("empty-").FullName;
        CometTrackFetch.TryRequestFor(empty, designation: null, out _, out _, out var nothing).ShouldBeFalse();
        nothing.ShouldContain("there is no light");
    }

    [Fact]
    public async Task TheTrackIsWhatHorizonsAnsweredInTimeOrderAndTooFewPositionsAreNoTrack()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new CometTrackRequest("10P", 48.2, 16.3, 200, Night, Night.AddHours(2), TimeSpan.FromMinutes(10));
        var answer = Track().Samples;
        var source = new AnsweringSource(answer.Reverse().ToImmutableArray());

        var (track, refusal) = await CometTrackFetch.FetchAsync(request, source, ct);

        refusal.ShouldBeNull();
        track.ShouldNotBeNull();
        source.Asked.ShouldBe(("10P", 48.2, 16.3, 200.0, Night, Night.AddHours(2), TimeSpan.FromMinutes(10)));
        (track.Designation, track.SiteLatDeg, track.SiteLonDeg, track.SiteElevMetres).ShouldBe(("10P", 48.2, 16.3, 200.0));
        track.Samples.ShouldBe(answer, "sorted by time");

        var (none, said) = await CometTrackFetch.FetchAsync(request, new AnsweringSource([answer[0]]), ct);
        none.ShouldBeNull();
        said.ShouldNotBeNull().ShouldContain("a track needs two");
    }

    // A light's header as a camera writes it: a time, an exposure, the comet's name and the site.
    private static void WriteLight(string path, DateTimeOffset start, string objectName, FrameType frameType = FrameType.Light, float latitude = 48.2f)
    {
        // Every positional field stated: a record struct made with new() and an initialiser leaves the rest at zero and
        // null, not at their declared defaults.
        var meta = new ImageMeta("synth", start, TimeSpan.FromSeconds(60), frameType, "",
            3.76f, 3.76f, 130, FocuserPositions.None, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
            RowOrder.TopDown, latitude, 16.3f, ObjectName: objectName, SiteElevation: 200f);
        new Image([new float[8, 8]], BitDepth.Float32, 1f, 0f, 0f, meta).WriteToFitsFile(path);
    }

    private sealed class AnsweringSource(ImmutableArray<EphemerisSample> answer) : IHorizonsObserverSource
    {
        public (string, double, double, double, DateTimeOffset, DateTimeOffset, TimeSpan)? Asked { get; private set; }

        public Task<ImmutableArray<EphemerisSample>> TryFetchTrackAsync(string designation, double siteLatDeg, double siteLonDeg, double siteElevMetres,
            DateTimeOffset start, DateTimeOffset stop, TimeSpan step, CancellationToken cancellationToken)
        {
            Asked = (designation, siteLatDeg, siteLonDeg, siteElevMetres, start, stop, step);
            return Task.FromResult(answer);
        }
    }
}
