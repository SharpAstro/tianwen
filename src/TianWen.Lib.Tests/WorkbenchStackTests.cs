using System;
using System.Collections.Concurrent;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The stack of a set of frames as a step of a project (#2467): a folder is the image's original, each group's master
/// is a result of the step under its group's name, and that master is the file <c>tianwen stack</c> writes. The step's
/// settings are the verb's options that choose what is stacked and how, and every one of them reaches the pipeline
/// through the verb as it does through the step.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchStackTests(ITestOutputHelper output) : IDisposable
{
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

    private static void ShouldBeTheSameFile(string actual, string expected, string what)
    {
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

    /// <summary>The real <c>stack</c> verb, saying what it says into <paramref name="said"/>: a queue, since the verb
    /// reports a stack's progress from pool threads while its own lines come from the run.</summary>
    private static Command Verb(ConcurrentQueue<string> said, ILogger<StackingPipeline> log, IExternal external, Func<StackingOptions, int>? sink = null)
    {
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Enqueue(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Enqueue("error: " + call.Arg<string>()));
        return new StackSubCommand(host, log, Substitute.For<ICelestialObjectDB>(), new SharpenPipeline(), external) { OptionsSink = sink }.Build();
    }

    /// <summary>
    /// What a step's command line MEANS, read through the verb's real option definitions: the pipeline's options it
    /// resolves to are the options the step runs with, compared whole. No folder is scanned
    /// (<see cref="StackSubCommand.OptionsSink"/>), so every setting is held to its option here and a stack is held to
    /// the verb's once, below.
    /// </summary>
    [Fact]
    public async Task AStepsLineResolvesThroughTheVerbToTheOptionsTheStepRunsWith()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = _folders.Create("frames-").FullName;
        var into = Path.Combine(_folders.Create("into-").FullName, "masters");
        var operation = new StackOperation();
        StackingOptions? resolved = null;
        var said = new ConcurrentQueue<string>();
        var verb = Verb(said, new PipelineLog(output), Substitute.For<IExternal>(), options =>
        {
            resolved = options;
            return 0;
        });

        // Every setting away from its default, but the one a stated drizzle leaves no room for.
        var everySetting = new StackSettings
        {
            GroupFilter = "M42",
            GroupExclude = "Ha",
            GroupTemperatureTolerance = 2,
            SplitByPierSide = true,
            IncludeMaybeCloud = true,
            IncludeIntegrations = true,
            QualityRejectSigma = 0,
            RejectCloud = false,
            RequireGainMatch = false,
            CentroidDebayer = DebayerAlgorithm.AHD,
            SnrMin = 7.5f,
            MinStars = 300,
            QuadStars = 60,
            ReferenceFrame = "_0003",
            WarpInterpolation = WarpInterpolation.Bilinear,
            ColourAlign = false,
            Strategy = IntegrationStrategyKind.BayerDrizzle,
            StackDebayer = DebayerAlgorithm.VNG,
            RejectLowSigma = 2.25f,
            RejectHighSigma = 2.75f,
            HotPixelSigma = 0,
            DrizzlePixfrac = 0.7f,
            DrizzleMinFrames = 12,
        };
        // A stated strategy that is no drizzle, and the choice of a drizzle switched off.
        var noDrizzle = new StackSettings { Strategy = IntegrationStrategyKind.InRamAllFrames, BayerDrizzle = false, QualityRejectSigma = 2.5f };
        var lines = new (string Name, StackSettings Settings)[]
        {
            ("the defaults", new StackSettings()),
            ("every setting", everySetting),
            ("no drizzle", noDrizzle),
            ("no choice of a drizzle", new StackSettings { BayerDrizzle = false }),
        };

        foreach (var (name, settings) in lines)
        {
            operation.Refusal(settings).ShouldBeNull(name);
            var line = operation.CommandLine(settings, [frames], into).ToArray();
            output.WriteLine(StepCommandLine.Display(line));
            line[..10].ShouldBe(
                [StepCommandLine.Program, "stack", frames, "--output", into, "--no-plate-solve", "--output-format", "none", "--no-clear-night-references", "--nominal-host"],
                name);
            resolved = null;
            said.Clear();

            var exit = await verb.Parse(line[2..]).InvokeAsync(cancellationToken: ct);

            exit.ShouldBe(0, $"{name}: {string.Join(Environment.NewLine, said)}");
            // The enhance options are the one member the verb always builds where a step, which never enhances, has none.
            (resolved.ShouldNotBeNull(name) with { EnhanceOptions = null }).ShouldBe(StackOperation.OptionsOf(settings, frames, into), name);
        }

        operation.CommandLine(new StackSettings(), [frames], into).Length.ShouldBe(10,"settings at their defaults put nothing on the line");

        // A setting added later is replayed above away from its default, or this says which one is not.
        var defaults = JsonNode.Parse(operation.DefaultSettings).ShouldNotBeNull().AsObject();
        var stated = new[] { everySetting, noDrizzle }
            .Select(static settings => JsonSerializer.SerializeToNode(settings, OperationSettingsJsonContext.Default.StackSettings).ShouldNotBeNull().AsObject())
            .ToArray();
        defaults.Count.ShouldBe(operation.Description.Settings.Length, "every setting is described, and nothing else is");
        foreach (var (setting, value) in defaults)
        {
            operation.Description.Settings.Select(static described => described.Name).ShouldContain(setting);
            stated.Any(settings => !JsonNode.DeepEquals(settings[setting], value)).ShouldBeTrue(
                $"the setting '{setting}' is at its default on every line replayed here, so its option is not held to the verb's");
        }
    }

    /// <summary>A setting that says nothing, says a thing a second way or changes no pixel is refused in words, and so
    /// is one the operation does not have (a comet run is no part of this step).</summary>
    [Theory]
    [InlineData("""{"groupFilter":""}""", "leave it out to stack every group")]
    [InlineData("""{"groupExclude":""}""", "leave it out to leave no group out")]
    [InlineData("""{"referenceFrame":""}""", "leave it out to let the stack choose")]
    [InlineData("""{"quadStars":3}""", "a quad is four stars")]
    [InlineData("""{"qualityRejectSigma":-1}""", "0 runs no relative gate")]
    [InlineData("""{"rejectHighSigma":0}""", "leave it out for the rejector's own")]
    [InlineData("""{"drizzlePixfrac":1.5}""", "0 to 1")]
    [InlineData("""{"strategy":"InRamAllFrames","drizzlePixfrac":0.7}""", "there is nothing they change")]
    [InlineData("""{"bayerDrizzle":false,"drizzleMinFrames":12}""", "there is nothing they change")]
    [InlineData("""{"bayerDrizzle":false,"strategy":"BayerDrizzle"}""", "say one of the two")]
    [InlineData("""{"comet":"10P"}""", "cannot be read")]
    public void SettingsThatSayNothingOrChangeNothingAreRefusedInWords(string settings, string says)
    {
        new StackOperation().TryReadSettings(settings, out _, out var refusal).ShouldBeFalse();

        refusal.ShouldNotBeNull().ShouldContain(says);
    }

    /// <summary>What is no folder, and a folder with no lights in it, are the step's refusal in words: nothing is named.</summary>
    [Fact(Timeout = 120_000)]
    public async Task AFileAndAFolderWithNoLightsAreRefusedInWords()
    {
        var ct = TestContext.Current.CancellationToken;
        var operation = new StackOperation(new PipelineLog(output));
        var folder = _folders.Create("no-lights-").FullName;
        var master = WorkbenchFixtures.WriteMaster(_folders.Create("a-file-").FullName, "master", WorkbenchFixtures.Master(seed: 1));
        var into = _folders.Create("into-").FullName;

        var ofAFile = await operation.RunIntoAsync(new StackSettings(), [OperationInput.FromFile(master)], into, say: null, ct);
        var ofNoLights = await operation.RunIntoAsync(new StackSettings(), [OperationInput.FromFile(folder)], into, say: null, ct);

        ofAFile.Outcome.Failure.ShouldBe(OperationFailure.Input);
        ofAFile.Outcome.Message.ShouldContain("takes a folder of frames");
        ofAFile.Results.ShouldBeEmpty();
        ofNoLights.Outcome.Failure.ShouldBe(OperationFailure.Input, ofNoLights.Outcome.Message);
        ofNoLights.Outcome.Message.ShouldContain("holds no group of lights to stack");
        ofNoLights.Results.ShouldBeEmpty();
    }

    [Fact]
    public void AGroupsLineSaysTheStrategyThatMadeItsMasterAndWhereItCameFrom()
    {
        var group = new GroupResult("M42_L_60s", FramesAttempted: 10, FramesMatched: 9, Result: null,
            MasterFitsPath: Path.Combine("out", "master_M42_L_60s.fits"), PreviewPngPath: null, Elapsed: TimeSpan.FromSeconds(12.34))
        {
            Strategy = IntegrationStrategyKind.InRamAllFrames,
            StrategyFrom = "stated",
        };
        // The seconds are worded in the current culture, so the expectation is built with the same format.
        var seconds = $"{12.34:F1}";

        StackOperation.GroupLine(group).ShouldBe($"M42_L_60s: 9/10 matched, integrated as InRamAllFrames (stated), wrote master_M42_L_60s.fits in {seconds}s");
        // A group no strategy was picked for says nothing of one.
        StackOperation.GroupLine(group with { Strategy = null, StrategyFrom = string.Empty })
            .ShouldBe($"M42_L_60s: 9/10 matched, wrote master_M42_L_60s.fits in {seconds}s");
        StackOperation.GroupLine(group with { SkipReason = "fewer than 2 matched" }).ShouldBe("M42_L_60s: SKIPPED (fewer than 2 matched)");
    }

    [Fact(Timeout = 300_000)]
    public async Task TheStackStepsMasterIsTheFileTianwenStackWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        // A night: ten lights of one target under one sky (the set StackingCloudEmptiedGroupTest stacks), in a folder
        // of their own, with the project somewhere else.
        var frames = Path.Combine(_folders.Create("night-").FullName, "2026-12-21");
        var lights = Path.Combine(frames, "LIGHT");
        Directory.CreateDirectory(lights);
        var firstLight = StackingCloudEmptiedGroupTest.WriteFrames(lights, FrameType.Light, skyBackground: 100.0);
        var project = _folders.Create("stack-").FullName;
        // Two settings away from their defaults, so that what the step is told reaches both stacks or the files differ.
        var night = WorkbenchFixtures.Picture("night", frames, WorkbenchFixtures.Edit(StackOperation.Id,
            new JsonObject { ["qualityRejectSigma"] = 0, ["warpInterpolation"] = "bilinear" }));
        await WorkbenchProjectFile.WriteAsync(project, WorkbenchFixtures.Project(night), ct);
        var log = new PipelineLog(output);
        var runner = new WorkbenchRunner(new OperationCatalog([new StackOperation(log)]));
        var store = Path.Combine(project, StepStore.FolderName);

        // The step's lines into a queue: the stack reports from pool threads.
        var stepSaid = new ConcurrentQueue<string>();
        var first = await runner.RunAsync(project, new WorkbenchRunOptions
        {
            Say = line =>
            {
                stepSaid.Enqueue(line);
                output.WriteLine(line);
            },
        }, ct);

        var step = Of(first, "night").Steps[0];
        step.State.ShouldBe(StepState.Ran, Said(first));
        first.Succeeded.ShouldBeTrue(Said(first));
        // The strategy the settings leave out is said with what it resolved to and by which rule: a step's is chosen
        // for the nominal computer, never for this one.
        stepSaid.ShouldContain(line => line.Contains(" matched, integrated as ") && line.Contains(" (chosen for the nominal computer), wrote "),
            string.Join(Environment.NewLine, stepSaid));
        // The step keeps the list of what its run named: one group, so one master, under the group's name.
        StepResultsList.TryRead(step.Output.ShouldNotBeNull(), out var names).ShouldBeTrue();
        var group = names.ShouldHaveSingleItem();
        var master = Path.Combine(store, StepStore.FileNameOf(StepKey.OfResult(step.Key, group)));
        File.Exists(master).ShouldBeTrue($"the master of '{group}' is in the store under its own key");
        IntegrationFitsWriter.IsTianWenMaster(master).ShouldBeTrue();
        File.Exists(StackManifest.PathFor(master)).ShouldBeTrue("a master's manifest is beside it in the store, under the result's name there");
        Directory.GetFiles(store, "master_*").ShouldBeEmpty("nothing keeps the name its run gave it");

        // The step's line through the real verb, on a computer that keeps a clear night by which every one of these
        // lights is cloud (the reference StackingCloudEmptiedGroupTest empties this very group with): the line says to
        // leave it out, so the verb stacks what the step stacked.
        var line = step.CommandLine.ToArray();
        line[..2].ShouldBe([StepCommandLine.Program, "stack"]);
        line[2].ShouldBe(frames);
        line[3].ShouldBe("--output");
        line[4].ShouldBe(Path.Combine(store, Path.GetFileNameWithoutExtension(StepStore.FileNameOf(step.Key))), "the line names the folder the step's results would go into");
        line.ShouldContain("--no-plate-solve");
        line.ShouldContain("--no-clear-night-references", "a step never asks what clear nights this computer keeps");
        line.ShouldContain("--nominal-host", "a step never chooses its strategy by this computer's free memory");
        line.ShouldContain("--quality-reject-sigma");
        line.ShouldContain("--warp-interpolation");
        var into = Path.Combine(_folders.Create("verb-").FullName, "output");
        line[4] = into;
        var external = new FakeExternal(output, root: _folders.Create("memory-"));
        await StackingCloudEmptiedGroupTest.WriteClearReferenceAsync(external, firstLight, nightDarkestSky: 1e-3f, ct);
        var said = new ConcurrentQueue<string>();
        var verb = Verb(said, log, external);
        output.WriteLine(StepCommandLine.Display(line));

        var exit = await verb.Parse(line[2..]).InvokeAsync(cancellationToken: ct);

        exit.ShouldBe(0, string.Join(Environment.NewLine, said));
        var verbMaster = Path.Combine(into, $"master_{group}.fits");
        File.Exists(verbMaster).ShouldBeTrue(string.Join(Environment.NewLine, said));
        // The verb says the same of the strategy, in the operation's words: the step's line carried --nominal-host.
        said.ShouldContain(line => line.Contains(" (chosen for the nominal computer), wrote "), string.Join(Environment.NewLine, said));
        ShouldBeTheSameFile(master, verbMaster, $"the master of '{group}'");
        // What belongs beside the master is beside it in the store: each map the verb wrote, with the verb's bytes.
        static string EndingOf(string map, string ofMaster) => Path.GetFileName(map)[Path.GetFileNameWithoutExtension(ofMaster).Length..];
        var verbMaps = IntegrationFitsWriter.ExistingSidecarsOf(verbMaster);
        var stepMaps = IntegrationFitsWriter.ExistingSidecarsOf(master);
        stepMaps.Select(map => EndingOf(map, master)).ShouldBe(verbMaps.Select(map => EndingOf(map, verbMaster)));
        stepMaps.ShouldNotBeEmpty("a stack writes at least one map beside its master");
        foreach (var (stepMap, verbMap) in stepMaps.Zip(verbMaps))
        {
            ShouldBeTheSameFile(stepMap, verbMap, $"the map {EndingOf(verbMap, verbMaster)} of '{group}'");
        }

        // An image starts from the master by its group's name, and one that names a group the stack did not give is
        // told what it gave. Neither stacks anything again.
        await WorkbenchProjectFile.WriteAsync(project, WorkbenchFixtures.Project(
            night,
            WorkbenchFixtures.StartingFromResult("target", "night", group),
            WorkbenchFixtures.StartingFromResult("other", "night", "M31_L_300s")), ct);

        var second = await runner.RunAsync(project, cancellationToken: ct);

        Of(second, "night").Steps[0].State.ShouldBe(StepState.Kept, "an image added to the project stacks nothing again");
        Of(second, "target").Refusal.ShouldBeNull();
        Of(second, "target").Key.ShouldBe(StepKey.OfResult(step.Key, group));
        Of(second, "other").Refusal.ShouldNotBeNull().ShouldContain($"it gave {group}");
    }
}
