using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
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
        File.ReadAllBytes(a).ShouldNotContain((byte)'\r', "its lines end in LF on every computer, so a track written on Windows and on Linux is one set of bytes");
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
    public async Task AFileThatCannotBeReadIsRefusedInWordsAndNeverThrown()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_folders.Create("locked-").FullName, "track.json");
        await Track().WriteAsync(path, ct);

        bool read;
        string? refusal;
        // Held open for writing with no sharing, as another program writing it would.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            read = CometTrack.TryRead(path, out _, out refusal);
        }

        read.ShouldBeFalse();
        refusal.ShouldNotBeNull().ShouldContain("cannot be read");
    }

    [Fact]
    public void AGroupsRateIsFittedThroughTheSamplesThatBracketItsFrames()
    {
        var track = Track(); // 20:00, 20:30, 21:00, 21:30, 22:00

        track.Bracketing(Night.AddMinutes(40), Night.AddMinutes(70)).Select(static sample => sample.TimeUtc).ToArray()
            .ShouldBe([Night.AddMinutes(30), Night.AddMinutes(60), Night.AddMinutes(90)], "one sample past each end, as a live request pads by one step");
        track.Bracketing(Night.AddMinutes(30), Night.AddMinutes(60)).Select(static sample => sample.TimeUtc).ToArray()
            .ShouldBe([Night.AddMinutes(30), Night.AddMinutes(60)], "a sample at an end is that end");
        track.Bracketing(Night, Night.AddMinutes(120)).Length.ShouldBe(5, "a group as long as the night takes the whole file");
    }

    /// <summary>
    /// The pipeline's fit of a track FILE is the fit of the samples bracketing the group, never of the whole file
    /// (<see cref="StackingPipeline.FitTrackFile"/>). On a curved track the two differ: a comet whose
    /// pixel x is 30 h + 20 h^2 over a six-hour file moves 160 px/h between 3 h and 3.5 h, where a line through the
    /// whole file says 150.
    /// </summary>
    [Fact]
    public void ATrackFilesRateIsFittedOnTheGroupsWindowAndNotOnTheWholeFile()
    {
        // 6" a pixel, north up and east left, centred on the field the comet tests use.
        const double Scale = 6.0 / 3600.0;
        var wcs = new WCS(8.468, -41.24) { CRPix1 = 1500, CRPix2 = 1500, CD1_1 = -Scale, CD1_2 = 0, CD2_1 = 0, CD2_2 = Scale };
        var samples = ImmutableArray.CreateBuilder<EphemerisSample>();
        for (var half = 0; half <= 12; half++)
        {
            var hours = half / 2.0;
            var (ra, dec) = wcs.PixelToSky(1000 + 30 * hours + 20 * hours * hours, 1200 - 10 * hours).ShouldNotBeNull();
            samples.Add(new EphemerisSample(Night.AddHours(hours), ra * 15.0, dec));
        }

        var track = Track() with { Samples = samples.ToImmutable() };

        var group = StackingPipeline.FitTrackFile(wcs, track, Night.AddHours(3.1), Night.AddHours(3.4)).ShouldNotBeNull();
        var whole = CometRateSolver.SolveCanvasRatePxPerHour(wcs, track.Samples.AsSpan()).ShouldNotBeNull();

        group.PxPerHour.X.ShouldBe(160f, 0.5f, "the samples at 3 h and 3.5 h, which bracket the group");
        group.PxPerHour.Y.ShouldBe(-10f, 0.5f);
        group.SampleCount.ShouldBe(2);
        whole.PxPerHour.X.ShouldBe(150f, 0.5f, "premise: the whole file's line is another rate, so the test tells the two apart");
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
    public async Task TheRequestIsTheNightsWindowFromItsLightsOwnSiteAndTheCometTheyName()
    {
        var folder = _folders.Create("night-").FullName;
        var lights = Directory.CreateDirectory(Path.Combine(folder, "LIGHT")).FullName;
        WriteLight(Path.Combine(lights, "a.fits"), Night, "10P");
        WriteLight(Path.Combine(lights, "b.fits"), Night.AddMinutes(40), "10P");
        WriteLight(Path.Combine(folder, "dark.fits"), Night.AddHours(-3), "10P", FrameType.Dark);

        var (request, lightCount, refusal) = await CometTrackFetch.RequestForAsync(folder, designation: null, TestContext.Current.CancellationToken);

        refusal.ShouldBeNull();
        lightCount.ShouldBe(2, "the lights alone, never a dark");
        request.Designation.ShouldBe("10P");
        request.SiteLatDeg.ShouldBe(48.2, 1e-4);
        request.SiteLonDeg.ShouldBe(16.3, 1e-4);
        // Padded by one step past the first light's start and the last light's end, as the stack's own request is.
        request.Start.ShouldBe(Night - request.Step);
        request.Stop.ShouldBe(Night.AddMinutes(40) + TimeSpan.FromSeconds(60) + request.Step);
    }

    [Fact]
    public async Task LightsOfTwoCometsWantTheOneNamedAndLightsFromTwoSitesAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var two = _folders.Create("two-").FullName;
        WriteLight(Path.Combine(two, "a.fits"), Night, "10P");
        WriteLight(Path.Combine(two, "b.fits"), Night.AddMinutes(5), "C/2025 R2");

        (await CometTrackFetch.RequestForAsync(two, designation: null, ct)).Refusal.ShouldNotBeNull().ShouldContain("name the comet");
        var (named, _, none) = await CometTrackFetch.RequestForAsync(two, designation: "10P", ct);
        none.ShouldBeNull();
        named.Designation.ShouldBe("10P");

        var sites = _folders.Create("sites-").FullName;
        WriteLight(Path.Combine(sites, "a.fits"), Night, "10P");
        WriteLight(Path.Combine(sites, "b.fits"), Night.AddMinutes(5), "10P", latitude: 40f);

        (await CometTrackFetch.RequestForAsync(sites, designation: null, ct)).Refusal.ShouldNotBeNull().ShouldContain("more than one site");

        var empty = _folders.Create("empty-").FullName;
        (await CometTrackFetch.RequestForAsync(empty, designation: null, ct)).Refusal.ShouldNotBeNull().ShouldContain("there is no light");

        // The antimeridian: lights at 179.99 E and 179.99 W are one site, as a stack's own check says (SameSite).
        var dateline = _folders.Create("dateline-").FullName;
        WriteLight(Path.Combine(dateline, "a.fits"), Night, "10P", longitude: 179.99f);
        WriteLight(Path.Combine(dateline, "b.fits"), Night.AddMinutes(5), "10P", longitude: -179.99f);
        (await CometTrackFetch.RequestForAsync(dateline, designation: null, ct)).Refusal.ShouldBeNull();
    }

    /// <summary>
    /// The lights are the ones a stack takes (the stack's own scan): another tool's composite beside them is an
    /// integration, so it neither names a second comet nor stretches the window. Astro Pixel Processor keeps
    /// <c>IMAGETYP='LIGHT'</c> on its composites, states the whole integration as <c>EXPTIME</c> and marks them only by
    /// <c>NUMFRAME</c>; on 10P's night six of them, with <c>OBJECT='10p Temepl'</c>, made the request refuse the folder
    /// and, once the comet was named, ask Horizons for two hours past the last light.
    /// </summary>
    [Fact]
    public async Task AnotherToolsCompositeBesideTheLightsIsNoLightOfTheNight()
    {
        var folder = _folders.Create("night-app-").FullName;
        WriteLight(Path.Combine(folder, "a.fits"), Night, "10P");
        WriteLight(Path.Combine(folder, "b.fits"), Night.AddMinutes(40), "10P");
        var proc = Directory.CreateDirectory(Path.Combine(folder, "PROC")).FullName;
        var composite = WriteForeignComposite(Path.Combine(proc, "10p_Tempel_Comet-RGB.fits"), Night.AddMinutes(90), "10p Temepl", numFrame: 132, exposureSeconds: 7920);
        Image.TryReadFitsHeader(composite, out var info).ShouldBeTrue();
        info.FrameType.ShouldBe(FrameType.Light, "premise: it says it is a light");
        info.IsMaster.ShouldBeFalse("premise: no master flag keeps it out");
        info.IsIntegration.ShouldBeTrue("premise: its count card is what marks it");

        var (request, lightCount, refusal) = await CometTrackFetch.RequestForAsync(folder, designation: null, TestContext.Current.CancellationToken);

        refusal.ShouldBeNull("the composite names no second comet");
        lightCount.ShouldBe(2);
        request.Designation.ShouldBe("10P");
        request.Stop.ShouldBe(Night.AddMinutes(40) + TimeSpan.FromSeconds(60) + request.Step, "the window ends with the last light, not with the composite's hours");
    }

    /// <summary>
    /// The verb, with Horizons stood in for: the track is written into the folder <c>-o</c> names, made first so an
    /// answer is never lost; a folder with no light is refused in words before anything is asked.
    /// </summary>
    [Fact]
    public async Task TheVerbWritesTheTrackIntoAFolderItMakesAndAsksNothingOfAFolderWithNoLight()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = _folders.Create("verb-night-").FullName;
        WriteLight(Path.Combine(frames, "a.fits"), Night.AddMinutes(10), "10P");
        WriteLight(Path.Combine(frames, "b.fits"), Night.AddMinutes(50), "10P");
        var output = Path.Combine(_folders.Create("verb-out-").FullName, "tracks", "10P", "track.json");
        var said = new ConcurrentQueue<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Enqueue(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Enqueue("error: " + call.Arg<string>()));
        var asked = 0;
        var verb = new CometSubCommand(host, NullLogger<CometSubCommand>.Instance)
        {
            Fetch = (request, _) =>
            {
                asked++;
                return Task.FromResult<(CometTrack?, string?)>((CometTrack.From(request, Track().Samples), null));
            },
        }.Build();

        (await verb.Parse(["track", frames, "-o", output]).InvokeAsync(cancellationToken: ct)).ShouldBe(0, string.Join(Environment.NewLine, said));

        asked.ShouldBe(1);
        CometTrack.TryRead(output, out var written, out var refusal).ShouldBeTrue(refusal);
        written.Designation.ShouldBe("10P");
        written.Samples.ShouldBe(Track().Samples);

        said.Clear();
        var empty = _folders.Create("verb-empty-").FullName;
        (await verb.Parse(["track", empty, "-o", Path.Combine(empty, "track.json")]).InvokeAsync(cancellationToken: ct)).ShouldBe(1);
        said.ShouldContain(line => line.Contains("there is no light"));
        asked.ShouldBe(1, "nothing is asked of Horizons for a folder with no light");
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
    private static void WriteLight(string path, DateTimeOffset start, string objectName, FrameType frameType = FrameType.Light, float latitude = 48.2f,
        float longitude = 16.3f)
    {
        // Every positional field stated: a record struct made with new() and an initialiser leaves the rest at zero and
        // null, not at their declared defaults.
        var meta = new ImageMeta("synth", start, TimeSpan.FromSeconds(60), frameType, "",
            3.76f, 3.76f, 130, FocuserPositions.None, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
            RowOrder.TopDown, latitude, longitude, ObjectName: objectName, SiteElevation: 200f);
        new Image([new float[8, 8]], BitDepth.Float32, 1f, 0f, 0f, meta).WriteToFitsFile(path);
    }

    // A composite as Astro Pixel Processor writes it: IMAGETYP LIGHT, the whole integration as EXPTIME, NUMFRAME its one
    // mark, and the reference light's site and object copied over (the cards read off 10P's PROC folder).
    private static string WriteForeignComposite(string path, DateTimeOffset start, string objectName, int numFrame, double exposureSeconds)
    {
        var hdu = nom.tam.fits.FitsFactory.HDUFactory(new float[4, 4]);
        hdu.AddValue("IMAGETYP", "LIGHT", "");
        hdu.AddValue("DATE-OBS", start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture), "");
        hdu.AddValue("EXPTIME", exposureSeconds, "");
        hdu.AddValue("EXPOSURE", 60.0, "");
        hdu.AddValue("NUMFRAME", numFrame, "");
        hdu.AddValue("OBJECT", objectName, "");
        hdu.AddValue("SITELAT", 48.2, "");
        hdu.AddValue("SITELONG", 16.3, "");
        hdu.AddValue("SOFTWARE", "Astro Pixel Processor by Aries Productions", "");
        var fits = new nom.tam.fits.Fits();
        fits.AddHDU(hdu);
        using (var file = new nom.tam.util.BufferedFile(path, FileAccess.ReadWrite, FileShare.None))
        {
            fits.Write(file);
            file.Flush();
        }

        return path;
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
