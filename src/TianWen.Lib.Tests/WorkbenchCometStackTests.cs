using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A comet's night stacked on the comet as a step of a project (#2489): the frames' folder is the image's original, the
/// comet's track is a file the step takes (the owner's call of 2026-11-25: a step never asks Horizons, its key holds
/// the ephemeris), and each group gives the comet master and the star layer beside it, the files
/// <c>tianwen stack --comet-track</c> writes.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchCometStackTests(ITestOutputHelper output) : IDisposable
{
    // The field SolveHintCacheTests renders and solves, seen from a site that is night at the frames' time.
    private const double TargetRA = 8.468;
    private const double TargetDec = -41.24;
    private const float Latitude = -33.9f;
    private const float Longitude = 151.2f;
    private const float Elevation = 120f;
    private const int Frames = 6;

    // Where the comet is at the first frame's start, in the frames' pixels, and how it moves: 30 minutes carry it 21 px.
    private static readonly Vector2 AnchorPx = new(1500f, 1400f);
    private static readonly Vector2 RatePxPerHour = new(36f, -24f);
    private static readonly DateTimeOffset NightStart = new(2026, 1, 1, 14, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Exposure = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(240);

    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    /// <summary>The pipeline's own lines in the test's output, for the step and for the verb.</summary>
    private sealed class PipelineLog(ITestOutputHelper output) : ILogger<StackingPipeline>
    {
        private readonly XunitLogger _inner = new(output);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private static string Said(WorkbenchReport report)
        => string.Join("; ", report.Images.Select(static image => image.Refusal).Concat(report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));

    private static ImageReport Of(WorkbenchReport report, string name) => report.Images.Single(image => image.Name == name);

    /// <summary>The real <c>stack</c> verb on <paramref name="catalogDb"/>, saying what it says into <paramref name="said"/>.</summary>
    private static Command Verb(ConcurrentQueue<string> said, ILogger<StackingPipeline> log, ICelestialObjectDB catalogDb, IExternal external,
        Func<StackingOptions, int>? sink = null)
    {
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Enqueue(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Enqueue("error: " + call.Arg<string>()));
        return new StackSubCommand(host, log, catalogDb, new SharpenPipeline(), external) { OptionsSink = sink }.Build();
    }

    private static CometTrack AnyTrack() => new(
        CometTrack.CurrentSchemaVersion, CometTrack.Horizons, "10P", Latitude, Longitude, Elevation,
        [new EphemerisSample(NightStart, 127.0, -41.2), new EphemerisSample(NightStart.AddHours(1), 127.01, -41.21)]);

    /// <summary>A setting that says nothing, a rate given by halves, and a star layer's setting where no star layer is
    /// made are refused in words; the stack's own settings are refused as the stack refuses them.</summary>
    [Theory]
    [InlineData("""{"rateX":1.5}""", "give both or neither")]
    [InlineData("""{"rateX":1.5,"rateY":-2,"composite":false}""", "a rate carries no position")]
    [InlineData("""{"rateX":1.5,"rateY":-2,"starLayer":false}""", "a rate carries no position")]
    [InlineData("""{"starLayer":false,"maskArcsec":120}""", "starLayer is off")]
    [InlineData("""{"maskArcsec":0}""", "maskArcsec is arcseconds")]
    [InlineData("""{"quadStars":3}""", "a quad is four stars")]
    [InlineData("""{"comet":"10P"}""", "cannot be read")]
    public void SettingsThatCannotRunAreRefusedInWords(string settings, string says)
    {
        new CometStackOperation().TryReadSettings(settings, out _, out var refusal).ShouldBeFalse();

        refusal.ShouldNotBeNull().ShouldContain(says);
    }

    /// <summary>
    /// What a step's line MEANS, through the verb's real options: where the comet's track is or what its rate is, and the
    /// star layer's settings, resolve to the options the step runs with. No folder is scanned
    /// (<see cref="StackSubCommand.OptionsSink"/>).
    /// </summary>
    [Fact]
    public async Task AStepsLineSaysItsTrackOrItsRateAndResolvesThroughTheVerbToTheOptionsTheStepRunsWith()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = _folders.Create("frames-").FullName;
        var into = Path.Combine(_folders.Create("into-").FullName, "masters");
        var trackPath = Path.Combine(_folders.Create("track-").FullName, "10P.json");
        var track = AnyTrack();
        await track.WriteAsync(trackPath, ct);
        var operation = new CometStackOperation();
        StackingOptions? resolved = null;
        var said = new ConcurrentQueue<string>();
        var verb = Verb(said, new PipelineLog(output), Substitute.For<ICelestialObjectDB>(), Substitute.For<IExternal>(), options =>
        {
            resolved = options;
            return 0;
        });

        var lines = new (string Name, CometStackSettings Settings, bool WithTrack, string[] Says)[]
        {
            ("a track at the defaults", new CometStackSettings(), true, ["--comet-track", trackPath]),
            ("a track, its star layer's settings moved", new CometStackSettings { Composite = false, MaskArcsec = 120, QuadStars = 60 }, true,
                ["--comet-track", trackPath, "--no-composite", "--comet-mask-arcsec", "120", "--quad-stars", "60"]),
            ("no star layer", new CometStackSettings { StarLayer = false }, true, ["--comet-track", trackPath, "--no-star-layer"]),
            ("a rate", new CometStackSettings { RateX = 1.5f, RateY = -2f }, false, ["--comet-rate", "1.5,-2"]),
        };

        foreach (var (name, settings, withTrack, says) in lines)
        {
            operation.Refusal(settings).ShouldBeNull(name);
            var line = operation.CommandLine(settings, withTrack ? [frames, trackPath] : [frames], into).ToArray();
            output.WriteLine(StepCommandLine.Display(line));
            line[..10].ShouldBe(
                [StepCommandLine.Program, "stack", frames, "--output", into, "--no-plate-solve", "--output-format", "none", "--no-clear-night-references", "--nominal-host"],
                name);
            foreach (var word in says)
            {
                line.ShouldContain(word, name);
            }

            resolved = null;
            said.Clear();

            var exit = await verb.Parse(line[2..]).InvokeAsync(cancellationToken: ct);

            exit.ShouldBe(0, $"{name}: {string.Join(Environment.NewLine, said)}");
            var options = resolved.ShouldNotBeNull(name);
            var expected = CometStackOperation.OptionsOf(settings, frames, into, withTrack ? track : null);
            // The track is read twice, so it is held by what it says; the enhance options are the one member the verb
            // always builds where a step, which never enhances, has none.
            (options with { EnhanceOptions = null, CometTrack = null }).ShouldBe(expected with { CometTrack = null }, name);
            if (withTrack)
            {
                var read = options.CometTrack.ShouldNotBeNull(name);
                read.Designation.ShouldBe(track.Designation, name);
                read.Samples.ShouldBe(track.Samples, name);
            }
            else
            {
                options.CometTrack.ShouldBeNull(name);
            }

            // Every master is left unsolved and the reference frame is still solved where a track places the comet.
            options.SolveMaster.ShouldBeFalse(name);
        }
    }

    /// <summary>The verb refuses a track it cannot read and a track beside a designation in words, before any scan.</summary>
    [Fact]
    public async Task TheVerbRefusesATrackItCannotReadAndATrackBesideADesignation()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = _folders.Create("frames-").FullName;
        var trackPath = Path.Combine(_folders.Create("track-").FullName, "10P.json");
        await AnyTrack().WriteAsync(trackPath, ct);
        var said = new ConcurrentQueue<string>();
        var verb = Verb(said, new PipelineLog(output), Substitute.For<ICelestialObjectDB>(), Substitute.For<IExternal>(), static _ => 0);

        (await verb.Parse([frames, "--comet-track", Path.Combine(frames, "none.json")]).InvokeAsync(cancellationToken: ct)).ShouldBe(1);
        said.ShouldContain(line => line.Contains("there is no comet track at"));

        said.Clear();
        (await verb.Parse([frames, "--comet-track", trackPath, "--comet", "10P"]).InvokeAsync(cancellationToken: ct)).ShouldBe(1);
        said.ShouldContain(line => line.Contains("names its own comet"));
    }

    /// <summary>A star remover stood in for, serving a mono input, a colour one or both, which leaves its input as it is.</summary>
    private sealed class StandInRemover(string name, bool mono = true, bool colour = true) : IStarRemover, IEnhancerAvailability
    {
        public string Name => name;

        public bool CanServe(int channelCount, EnhanceOptions options) => channelCount == 1 ? mono : colour;

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default) => Task.FromResult(input);
    }

    // A small night (four of StackingCloudEmptiedGroupTest's lights: 51 N, 0 E, 2026-12-21 from midnight; four, since the
    // stack drops a group of fewer as a ghost sub-group, MinSubGroupFrames) and a track of 10P asked from the given
    // latitude, every ten minutes from 23:50 to 00:20, in a project of their own.
    private async Task<(string Project, string Track)> SmallNightAsync(string name, float trackLatitude, CancellationToken ct)
    {
        var frames = Path.Combine(_folders.Create($"{name}-night-").FullName, "2026-12-21");
        Directory.CreateDirectory(frames);
        StackingCloudEmptiedGroupTest.WriteFrames(frames, FrameType.Light, skyBackground: 100.0, count: 4);
        var trackPath = Path.Combine(_folders.Create($"{name}-track-").FullName, "10P.json");
        var start = new DateTimeOffset(2026, 12, 20, 23, 50, 0, TimeSpan.Zero);
        await new CometTrack(CometTrack.CurrentSchemaVersion, CometTrack.Horizons, "10P", trackLatitude, 0f, 0,
            [.. Enumerable.Range(0, 4).Select(i => new EphemerisSample(start.AddMinutes(10 * i), 127.0 + 0.001 * i, -41.2))]).WriteAsync(trackPath, ct);
        var project = _folders.Create($"{name}-project-").FullName;
        await WorkbenchProjectFile.WriteAsync(project, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("night", frames, WorkbenchFixtures.Edit(CometStackOperation.Id, null, "track")),
            WorkbenchFixtures.Picture("track", trackPath)), ct);
        return (project, trackPath);
    }

    /// <summary>
    /// The step's key holds the ephemeris and what serves the star layer: a track whose bytes moved is another key, and
    /// so is a star remover that subtracts the body where none did, each said in words; a remover that serves one kind
    /// of master says which. All from a status, which stacks nothing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheKeyHoldsTheTrackItsBytesAndWhatServesTheStarLayer()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, trackPath) = await SmallNightAsync("key", 51f, ct);
        async Task<StepReport> StepUnder(IStarRemover? remover)
            => Of(await new WorkbenchRunner(new OperationCatalog([new CometStackOperation(starRemover: remover)])).StatusAsync(project, cancellationToken: ct), "night").Steps[0];

        var none = await StepUnder(null);
        none.Key.ShouldNotBeNullOrEmpty();
        none.ServedBy.ShouldBe("star layer: the body excluded, no star remover serves");

        // One position of the comet moved by a ten-thousandth of a degree: another ephemeris, another key.
        CometTrack.TryRead(trackPath, out var track, out var refusal).ShouldBeTrue(refusal);
        await (track with { Samples = track.Samples.SetItem(1, track.Samples[1] with { RaDeg = track.Samples[1].RaDeg + 1e-4 }) }).WriteAsync(trackPath, ct);
        var moved = await StepUnder(null);
        moved.Key.ShouldNotBe(none.Key, "the track is an original read by its bytes, so the key holds the ephemeris");

        var subtracting = await StepUnder(new StandInRemover("Stand-in remover"));
        subtracting.ServedBy.ShouldBe("star layer: the body subtracted by Stand-in remover");
        subtracting.Key.ShouldNotBe(moved.Key, "a body subtracted is other pixels than a body excluded");

        (await StepUnder(new StandInRemover("Stand-in remover", colour: false))).ServedBy
            .ShouldBe("star layer: the body subtracted by Stand-in remover on a mono master, excluded on a colour one");
        (await StepUnder(new StandInRemover("Stand-in remover", mono: false))).ServedBy
            .ShouldBe("star layer: the body subtracted by Stand-in remover on a colour master, excluded on a mono one");
    }

    /// <summary>
    /// A track asked from another site reaches the person through the step: the group is skipped with the track's own
    /// reason, worded for a track and not for <c>--comet</c>, before anything is registered or solved.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ATrackFromAnotherSiteSkipsTheGroupAndTheStepSaysWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, _) = await SmallNightAsync("site", 40f, ct);
        // A catalogue is there (the step is served); the track is refused before it is asked anything.
        var runner = new WorkbenchRunner(new OperationCatalog([new CometStackOperation(new PipelineLog(output), Substitute.For<ICelestialObjectDB>())]));

        var report = await runner.RunAsync(project, cancellationToken: ct);

        var step = Of(report, "night").Steps[0];
        step.State.ShouldBe(StepState.Refused, Said(report));
        var message = step.Message.ShouldNotBeNull();
        message.ShouldContain("was asked from (40.0000, 0.0000) and the frames were taken at (51.0000, 0.0000)");
        message.ShouldContain("give a track asked from these frames' site over their night, or a rate");
        message.ShouldNotContain("--comet");
    }

    /// <summary>
    /// A comet's night (a field that plate-solves, with a comet moving across it) stacked as a step on its track, with no
    /// network: the comet master is registered on the comet at the track's rate and the star layer on the stars, each a
    /// result of the step under its name, and both are the files the step's line makes the verb write, to the byte.
    /// </summary>
    // Heavy by measurement: 57.5 s on Linux and 68.6 s on Windows in its first CI run (#2517), two stacks of six 9 Mpx
    // frames, each with its star layer, and a plate solve of the reference in each.
    [Fact(Timeout = 600_000)]
    [Trait("Category", "Heavy")]
    public async Task TheCometStepsMastersAreTheFilesTianwenStackWritesOnTheTrack()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        var frames = Path.Combine(_folders.Create("comet-night-").FullName, "2026-01-01");
        var lights = Directory.CreateDirectory(Path.Combine(frames, "LIGHT")).FullName;
        var project = _folders.Create("comet-").FullName;
        var trackPath = Path.Combine(_folders.Create("comet-track-").FullName, "10P.track.json");
        await WriteCometNightAsync(lights, trackPath, db, ct);

        await WorkbenchProjectFile.WriteAsync(project, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("night", frames, WorkbenchFixtures.Edit(CometStackOperation.Id, null, "track")),
            WorkbenchFixtures.Picture("track", trackPath)), ct);
        var log = new PipelineLog(output);
        // No star remover: the star layer EXCLUDES the body, and there is no composite (that needs the body subtracted).
        var runner = new WorkbenchRunner(new OperationCatalog([new CometStackOperation(log, db)]));
        var store = Path.Combine(project, StepStore.FolderName);

        var first = await runner.RunAsync(project, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        var step = Of(first, "night").Steps[0];
        step.State.ShouldBe(StepState.Ran, Said(first));
        first.Succeeded.ShouldBeTrue(Said(first));
        StepResultsList.TryRead(step.Output.ShouldNotBeNull(), out var names).ShouldBeTrue();
        var group = names.Single(static name => !name.EndsWith(CometStackOperation.StarsEnding, StringComparison.Ordinal));
        names.Order(StringComparer.Ordinal).ToArray().ShouldBe([group, group + CometStackOperation.StarsEnding],
            "the comet master and its star layer; no composite, since no star remover serves here");
        string Stored(string name) => Path.Combine(store, StepStore.FileNameOf(StepKey.OfResult(step.Key, name)));
        var (comet, stars) = (Stored(group), Stored(group + CometStackOperation.StarsEnding));

        // The comet master is registered on the comet, at the rate its track gives once projected through the reference
        // frame's plate solution; the star layer on the stars.
        var cometHeader = HeaderOf(comet);
        cometHeader.ShouldContain("ALIGNMNT= 'Comet");
        cometHeader.ShouldContain("TRACKOBJ= '10P");
        cometHeader.ShouldContain("DRIFTSRC= 'Horizons");
        CardNumber(cometHeader, "DRIFTX").ShouldBe(RatePxPerHour.X, 1.0, "the rate the track was made with, read back through the reference's solve");
        CardNumber(cometHeader, "DRIFTY").ShouldBe(RatePxPerHour.Y, 1.0);
        HeaderOf(stars).ShouldContain("ALIGNMNT= 'Sidereal");

        // The step's line through the real verb: the same two files, to the byte.
        var line = step.CommandLine.ToArray();
        line.ShouldContain("--comet-track");
        line.ShouldContain(trackPath, "the line names the track the step was given");
        var into = Path.Combine(_folders.Create("verb-").FullName, "output");
        line[4] = into;
        var said = new ConcurrentQueue<string>();
        var verb = Verb(said, log, db, new FakeExternal(output, root: _folders.Create("memory-")));
        output.WriteLine(StepCommandLine.Display(line));

        var exit = await verb.Parse(line[2..]).InvokeAsync(cancellationToken: ct);

        exit.ShouldBe(0, string.Join(Environment.NewLine, said));
        ShouldBeTheSameFile(comet, Path.Combine(into, $"master_{group}.fits"), $"the comet master of '{group}'");
        ShouldBeTheSameFile(stars, Path.Combine(into, $"master_{group}{CometStackOperation.StarsEnding}.fits"), $"the star layer of '{group}'");
        File.Exists(Path.Combine(into, $"master_{group}{CometStackOperation.CompositeEnding}.fits")).ShouldBeFalse("the verb writes no composite either");

        // Run again: the track is an original read by its bytes, so nothing has moved and nothing is stacked again.
        var second = await runner.RunAsync(project, cancellationToken: ct);
        Of(second, "night").Steps[0].State.ShouldBe(StepState.Kept, Said(second));
    }

    // The ASCII header blocks of a FITS file, up to and with its END card.
    private static string HeaderOf(string fits)
    {
        var bytes = File.ReadAllBytes(fits);
        var text = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 2880 * 16));
        var end = text.IndexOf("END" + new string(' ', 77), StringComparison.Ordinal);
        return end < 0 ? text : text[..(end + 80)];
    }

    // The number a card holds, from its 80 columns: the value between the '=' and any '/'.
    private static double CardNumber(string header, string keyword)
    {
        for (var at = 0; at + 80 <= header.Length; at += 80)
        {
            var card = header.Substring(at, 80);
            if (card.StartsWith(keyword.PadRight(8), StringComparison.Ordinal) && card[8] == '=')
            {
                var value = card[10..];
                var slash = value.IndexOf('/');
                return double.Parse((slash < 0 ? value : value[..slash]).Trim(), CultureInfo.InvariantCulture);
            }
        }

        throw new ShouldAssertException($"the header holds no {keyword} card");
    }

    private static void ShouldBeTheSameFile(string actual, string expected, string what)
    {
        File.Exists(expected).ShouldBeTrue($"{what}: the verb wrote no {Path.GetFileName(expected)}");
        var (a, e) = (File.ReadAllBytes(actual), File.ReadAllBytes(expected));
        var at = a.AsSpan().CommonPrefixLength(e);
        if (at == a.Length && at == e.Length)
        {
            return;
        }

        // A FITS header is 80-character cards, so the card holding the first difference says what differs.
        var card = at / 80 * 80;
        string Card(byte[] bytes) => card + 80 <= bytes.Length ? Encoding.ASCII.GetString(bytes, card, 80).TrimEnd() : "(past its end)";
        Assert.Fail($"{what}: the step's file and the verb's differ at byte {at} (lengths {a.Length} and {e.Length}). "
            + $"The 80 bytes there read '{Card(a)}' in the step's and '{Card(e)}' in the verb's.");
    }

    /// <summary>
    /// A comet's night: the field the fake camera renders from the catalogue, six frames five minutes apart from one site,
    /// with a comet added to each where it is at the frame's mid-exposure; and its track, the comet's place every five
    /// minutes from before the first frame to after the last, turned into the sky through the first frame's plate
    /// solution as Horizons would state it.
    /// </summary>
    private async Task WriteCometNightAsync(string lights, string trackPath, ICelestialObjectDB db, CancellationToken ct)
    {
        var time = new FakeTimeProviderWrapper(NightStart);
        var external = new FakeExternal(output, time);
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 2), external.BuildServiceProvider());
        await camera.ConnectAsync(ct);
        camera.TrueBestFocus = 1000;
        camera.FocusPosition = 1000;
        camera.FocalLength = 130;
        camera.Target = new Target(TargetRA, TargetDec, "10P", null);
        camera.CelestialObjectDB = db;
        camera.Latitude = Latitude;
        camera.Longitude = Longitude;
        camera.SiteElevation = Elevation;
        ICameraDriver driver = camera;

        WCS? solution = null;
        for (var i = 0; i < Frames; i++)
        {
            await camera.StartExposureAsync(Exposure, cancellationToken: ct);
            await time.SleepAsync(Exposure, ct);
            (await camera.GetImageReadyAsync(ct)).ShouldBeTrue();
            var frame = (await driver.GetImageAsync(ct)).ShouldNotBeNull();
            if (solution is null)
            {
                var solved = await new CatalogPlateSolver(db, NullLogger.Instance).SolveImageAsync(
                    frame, frame.GetImageDim(), searchOrigin: new WCS(TargetRA, TargetDec), searchRadius: 3d, cancellationToken: ct);
                solution = solved.Solution.ShouldNotBeNull("the premise is a field that plate-solves");
            }

            var mid = frame.ImageMeta.ExposureStartTime + frame.ImageMeta.ExposureDuration / 2;
            WithComet(frame, PlaceAt(mid)).WriteToFitsFile(Path.Combine(lights, $"10P_{i:D2}.fits"));
            await time.SleepAsync(Gap, ct);
        }

        var wcs = solution.ShouldNotBeNull();
        var samples = ImmutableArray.CreateBuilder<EphemerisSample>();
        var last = NightStart + (Exposure + Gap) * Frames;
        for (var at = NightStart - TimeSpan.FromMinutes(10); at <= last + TimeSpan.FromMinutes(10); at += TimeSpan.FromMinutes(5))
        {
            var place = PlaceAt(at);
            // PixelToSky answers RA in hours; a track states it in degrees.
            var (ra, dec) = wcs.PixelToSky(place.X, place.Y).ShouldNotBeNull();
            samples.Add(new EphemerisSample(at, ra * 15.0, dec));
        }

        await new CometTrack(CometTrack.CurrentSchemaVersion, CometTrack.Horizons, "10P", Latitude, Longitude, Elevation, samples.ToImmutable())
            .WriteAsync(trackPath, ct);
    }

    // Where the comet is at an instant, in the frames' pixels.
    private static Vector2 PlaceAt(DateTimeOffset at) => AnchorPx + RatePxPerHour * (float)(at - NightStart).TotalHours;

    // The frame with a comet added: a round head 3 px in sigma, 4000 ADU at its peak, held under the sensor's full scale.
    private static Image WithComet(Image frame, Vector2 at)
    {
        const float Peak = 4000f;
        const float Sigma = 3f;
        var source = frame.GetChannelArray(0);
        var (height, width) = (source.GetLength(0), source.GetLength(1));
        var plane = new float[height, width];
        Array.Copy(source, plane, source.Length);
        var max = frame.MaxValue;
        for (var y = Math.Max(0, (int)at.Y - 15); y <= Math.Min(height - 1, (int)at.Y + 15); y++)
        {
            for (var x = Math.Max(0, (int)at.X - 15); x <= Math.Min(width - 1, (int)at.X + 15); x++)
            {
                var r2 = (x - at.X) * (x - at.X) + (y - at.Y) * (y - at.Y);
                plane[y, x] = MathF.Min(65535f, plane[y, x] + Peak * MathF.Exp(-r2 / (2 * Sigma * Sigma)));
                max = MathF.Max(max, plane[y, x]);
            }
        }

        return new Image([plane], frame.BitDepth, max, frame.MinValue, frame.Pedestal, frame.ImageMeta);
    }
}
