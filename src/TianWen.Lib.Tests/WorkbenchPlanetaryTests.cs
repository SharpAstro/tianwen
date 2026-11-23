using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The planetary steps of a project (#2468), the AUTO stack first: a capture is an image's original, its master is the
/// step's result and its sharpened master a result beside it, and both are the files
/// <c>tianwen planetary stack --auto</c> writes. Then the compose: a set of mono stacks is one step, its colour master
/// the step's result and its luminance a result beside it, and both are the files <c>tianwen planetary compose</c>
/// writes. Then the sharpening of a master again, which is the file <c>tianwen planetary sharpen --auto</c> writes.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchPlanetaryTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static string Messages(WorkbenchReport report)
        => string.Join("; ", report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message));

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

    [Fact(Timeout = 300_000)]
    public async Task ThePlanetaryStackStepIsTheTwoFilesPlanetaryStackAutoWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("planetary-").FullName;
        // A TianWen recording: its name says the planet and the filter, its header the telescope.
        var capture = ViewerBestStackTests.WriteCapture(Path.Combine(folder, "Jupiter_Red_2024-12-15T12_56_44_OTA1.ser"),
            telescope: "254 mm f/4.7 Newtonian, Test OTA");
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            // Nothing said: the capture is read.
            WorkbenchFixtures.Picture("read", capture, WorkbenchFixtures.Edit(PlanetaryStackOperation.Id)),
            WorkbenchFixtures.StartingFromResult("read-sharpened", "read", PlanetaryStackOperation.Sharpened),
            // Everything said: the settings win over what the capture says of itself.
            WorkbenchFixtures.Picture("told", capture, WorkbenchFixtures.Edit(PlanetaryStackOperation.Id,
                new JsonObject { ["planet"] = "jupiter", ["filterNm"] = 530, ["apertureMm"] = 203, ["design"] = "sct" }))), ct);
        var runner = new WorkbenchRunner(new OperationCatalog([new PlanetaryStackOperation()]));

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        report.Succeeded.ShouldBeTrue(Messages(report));
        ImageReport Of(string name) => report.Images.Single(image => image.Name == name);
        Of("read").Steps[0].State.ShouldBe(StepState.Ran);
        Of("told").Steps[0].State.ShouldBe(StepState.Ran);
        Of("told").Steps[0].Key.ShouldNotBe(Of("read").Steps[0].Key, "what is said of a capture is part of its step's key");
        var store = Path.Combine(folder, StepStore.FolderName);
        string SharpenedOf(StepReport step) => Path.Combine(store, StepStore.FileNameOf(StepKey.OfResult(step.Key, PlanetaryStackOperation.Sharpened)));
        Of("read-sharpened").Key.ShouldBe(StepKey.OfResult(Of("read").Steps[0].Key, PlanetaryStackOperation.Sharpened));

        // The masters say what they were stacked as: the capture's own telescope, or the one the settings gave.
        WorkbenchFixtures.Read(Of("read").Steps[0].Output.ShouldNotBeNull()).ImageMeta.Telescope.ShouldContain("254");
        WorkbenchFixtures.Read(Of("told").Steps[0].Output.ShouldNotBeNull()).ImageMeta.Telescope.ShouldContain("203");

        // Each step's command line through the real verb, its two files named.
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        var stack = new PlanetaryStackSubCommand(
            host,
            new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance),
            new FakeExternal(output, root: _folders.Create("memory-"))).Build();
        foreach (var name in new[] { "read", "told" })
        {
            var step = Of(name).Steps[0];
            var line = step.CommandLine.ToArray();
            line[..3].ShouldBe([StepCommandLine.Program, "planetary", "stack"]);
            line.ShouldContain("--auto");
            line.ShouldContain("--no-telescope-memory", "a step never asks what telescope a camera was last said to be on");
            var (atMaster, atSharpened) = (Array.IndexOf(line, "--master") + 1, Array.IndexOf(line, "--sharpened") + 1);
            line[atMaster].ShouldBe(step.Output.ShouldNotBeNull(), "the line names the step's own file");
            line[atSharpened].ShouldBe(SharpenedOf(step));
            var (master, sharpened) = (Path.Combine(folder, name + "-verb.fits"), Path.Combine(folder, name + "-verb-sharpened.fits"));
            (line[atMaster], line[atSharpened]) = (master, sharpened);
            output.WriteLine(StepCommandLine.Display(line));
            said.Clear();

            var exit = await stack.Parse(line[3..]).InvokeAsync(cancellationToken: ct);

            exit.ShouldBe(0, string.Join(Environment.NewLine, said));
            ShouldBeTheSameFile(step.Output.ShouldNotBeNull(), master, $"the master of '{name}'");
            ShouldBeTheSameFile(SharpenedOf(step), sharpened, $"the sharpened master of '{name}'");
        }

        var again = await runner.RunAsync(folder, cancellationToken: ct);

        again.Images.SelectMany(static image => image.Steps).Select(static step => step.State).ShouldBe([StepState.Kept, StepState.Kept]);
    }

    private (Command Verb, List<string> Said, FakeExternal External) BuildVerb()
    {
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        var external = new FakeExternal(output, root: _folders.Create("memory-"));
        var command = new PlanetaryStackSubCommand(host, new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance), external).Build();
        return (command, said, external);
    }

    [Fact(Timeout = 300_000)]
    public async Task AStepNeverAsksWhatTelescopeACameraWasLastSaidToBeOnAndTheVerbWithItsFlagDoesNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("no-memory-").FullName;
        // No telescope in the header, the settings, the names or the folders: only the memory can say one.
        var capture = ViewerBestStackTests.WriteCapture(Path.Combine(folder, "Jupiter_Red_2024-12-15T12_56_44.ser"), instrument: "MemoryCam");
        var (verb, said, external) = BuildVerb();
        await PlanetaryTelescopeMemory.RememberAsync(external, "MemoryCam", 150, OpticalDesign.Newtonian, ct);

        async Task<string> TelescopeOfVerbAsync(string name, params string[] more)
        {
            var master = Path.Combine(folder, name + ".fits");
            said.Clear();
            var exit = await verb.Parse([capture, "--auto", "--no-png", "--master", master, "--sharpened", Path.Combine(folder, name + "-s.fits"), .. more])
                .InvokeAsync(cancellationToken: ct);
            exit.ShouldBe(0, string.Join(Environment.NewLine, said));
            return WorkbenchFixtures.Read(master).ImageMeta.Telescope ?? string.Empty;
        }

        (await TelescopeOfVerbAsync("asked")).ShouldContain("150", Case.Insensitive, "the verb without the flag takes the remembered telescope");
        (await TelescopeOfVerbAsync("flagged", "--no-telescope-memory")).ShouldNotContain("150", Case.Insensitive, "the flag keeps the memory out of the run");

        // The flag also stops a given aperture from being remembered.
        (await TelescopeOfVerbAsync("flagged-told", "--no-telescope-memory", "--aperture-mm", "203", "--design", "sct")).ShouldContain("203");
        (await PlanetaryTelescopeMemory.RecallAsync(external, "MemoryCam", ct)).ShouldBe(new RememberedTelescope(150, OpticalDesign.Newtonian));
        await TelescopeOfVerbAsync("told", "--aperture-mm", "203", "--design", "sct");
        (await PlanetaryTelescopeMemory.RecallAsync(external, "MemoryCam", ct)).ShouldBe(new RememberedTelescope(203, OpticalDesign.SCT));

        // The step is the flagged verb: the memory (150, then 203) is never asked.
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("step", capture, WorkbenchFixtures.Edit(PlanetaryStackOperation.Id))), ct);
        var report = await new WorkbenchRunner(new OperationCatalog([new PlanetaryStackOperation()])).RunAsync(folder, cancellationToken: ct);
        report.Succeeded.ShouldBeTrue(Messages(report));
        var stepTelescope = WorkbenchFixtures.Read(report.Images[0].Steps[0].Output.ShouldNotBeNull()).ImageMeta.Telescope ?? string.Empty;
        stepTelescope.ShouldNotContain("150");
        stepTelescope.ShouldNotContain("203");
        stepTelescope.ShouldBe(await TelescopeOfVerbAsync("flagged-again", "--no-telescope-memory"));
    }

    [Fact(Timeout = 120_000)]
    public async Task TheVerbRefusesTheAutoFilesAndTheMemoryFlagWithoutAutoAndOneFileWithoutItsPair()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("refusals-").FullName;
        var capture = ViewerBestStackTests.WriteCapture(Path.Combine(folder, "Jupiter_Red_2024-12-15T12_56_44.ser"));
        var (verb, said, _) = BuildVerb();

        async Task ShouldRefuseAsync(string message, params string[] args)
        {
            said.Clear();
            var exit = await verb.Parse([capture, .. args]).InvokeAsync(cancellationToken: ct);
            exit.ShouldBe(1, string.Join(Environment.NewLine, said));
            said.ShouldContain(line => line.Contains(message), string.Join(Environment.NewLine, said));
        }

        const string needsAuto = "go with --auto";
        await ShouldRefuseAsync(needsAuto, "--master", Path.Combine(folder, "m.fits"));
        await ShouldRefuseAsync(needsAuto, "--sharpened", Path.Combine(folder, "s.fits"));
        await ShouldRefuseAsync(needsAuto, "--no-telescope-memory");
        const string needsBoth = "give both, or neither";
        await ShouldRefuseAsync(needsBoth, "--auto", "--master", Path.Combine(folder, "m.fits"));
        await ShouldRefuseAsync(needsBoth, "--auto", "--sharpened", Path.Combine(folder, "s.fits"));
    }

    // Heavy by measurement on the x64 leg: 29.8 s and 39.4 s with seven stacks (#2481's and #2482's first runs), and still
    // 29.2 s with the four it needs (#2483's): a compose is about a quarter of a minute whatever it is handed, and
    // this test runs two, the step's and the verb's. The step alone stays in the PR legs (the test after this one).
    [Fact(Timeout = 300_000)]
    [Trait("Category", "Heavy")]
    public async Task ThePlanetaryComposeStepIsTheTwoFilesPlanetaryComposeWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("compose-").FullName;
        // Four mono stacks of Jupiter, one through each of red, green, blue and an IR-pass (the first run of the set
        // PlanetaryCompositionTests composes, and its IR), each written as a compose step writes a stack, so its planet,
        // instant and filter are in its header. No more of them than the step needs.
        var set = PlanetaryCompositionTests.Ingested();
        PlanetaryMonoStack[] stacks = [set[0], set[1], set[2], set[6]];
        var paths = new List<string>();
        var images = new List<ProjectImage>();
        for (var i = 0; i < stacks.Length; i++)
        {
            var path = Path.Combine(folder, stacks[i].Name + ".fits");
            PlanetaryComposition.WriteStack(stacks[i], path);
            paths.Add(path);
            images.Add(WorkbenchFixtures.Picture($"stack{i}", path));
        }

        // The colour master starts from one stack and takes the others, and a second list holds the same set from
        // another stack in another order: one step.
        images.Add(WorkbenchFixtures.Starting("colour", "stack2",
            WorkbenchFixtures.Edit(PlanetaryComposeOperation.Id, null, "stack3", "stack0", "stack1")));
        images.Add(WorkbenchFixtures.Starting("colour-again", "stack0",
            WorkbenchFixtures.Edit(PlanetaryComposeOperation.Id, null, "stack1", "stack2", "stack3")));
        images.Add(WorkbenchFixtures.StartingFromResult("luminance", "colour", PlanetaryComposeOperation.Luminance));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project([.. images]), ct);
        var runner = new WorkbenchRunner(new OperationCatalog([new PlanetaryComposeOperation()]));

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        report.Succeeded.ShouldBeTrue(Messages(report));
        ImageReport Of(string name) => report.Images.Single(image => image.Name == name);
        var step = Of("colour").Steps[0];
        step.State.ShouldBe(StepState.Ran);
        Of("colour-again").Steps[0].State.ShouldBe(StepState.Kept, "the same stacks under the same settings are one step, whoever holds it");
        Of("colour-again").Steps[0].Key.ShouldBe(step.Key);
        var master = step.Output.ShouldNotBeNull();
        var luminance = Path.Combine(folder, StepStore.FolderName, StepStore.FileNameOf(StepKey.OfResult(step.Key, PlanetaryComposeOperation.Luminance)));
        Of("luminance").Refusal.ShouldBeNull();
        Of("luminance").Key.ShouldBe(StepKey.OfResult(step.Key, PlanetaryComposeOperation.Luminance));
        File.Exists(luminance).ShouldBeTrue("the stack through the IR-pass is the luminance, kept beside the master");
        WorkbenchFixtures.Read(master).ChannelCount.ShouldBe(3);
        WorkbenchFixtures.Read(luminance).ChannelCount.ShouldBe(1);

        // The step's command line through the real verb, its two files named.
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        var compose = new PlanetaryComposeSubCommand(host, new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance)).Build();
        var line = step.CommandLine.ToArray();
        line[..3].ShouldBe([StepCommandLine.Program, "planetary", "compose"]);
        var (atMaster, atLuminance) = (Array.IndexOf(line, "-o") + 1, Array.IndexOf(line, "--luminance-output") + 1);
        line[atMaster].ShouldBe(master, "the line names the step's own file");
        line[atLuminance].ShouldBe(luminance);
        // The stacks are the project's own files, every one of them once.
        line[3..(atMaster - 1)].Order(StringComparer.Ordinal).ToArray().ShouldBe(paths.Order(StringComparer.Ordinal).ToArray());
        var (verbMaster, verbLuminance) = (Path.Combine(folder, "verb.fits"), Path.Combine(folder, "verb-luminance.fits"));
        (line[atMaster], line[atLuminance]) = (verbMaster, verbLuminance);
        output.WriteLine(StepCommandLine.Display(line));

        var exit = await compose.Parse(line[3..]).InvokeAsync(cancellationToken: ct);

        exit.ShouldBe(0, string.Join(Environment.NewLine, said));
        ShouldBeTheSameFile(master, verbMaster, "the colour master");
        ShouldBeTheSameFile(luminance, verbLuminance, "the luminance");

        var again = await runner.RunAsync(folder, cancellationToken: ct);

        again.Succeeded.ShouldBeTrue(Messages(again));
        again.Images.SelectMany(static image => image.Steps).Select(static each => each.State).ShouldBe([StepState.Kept, StepState.Kept]);
    }

    [Fact(Timeout = 120_000)]
    public async Task WithNoStackThroughAnIrPassOrLTheComposeStepIsWholeWithoutALuminance()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("compose-rgb-").FullName;
        // One run of red, green and blue alone: nothing to be the luminance.
        var stacks = PlanetaryCompositionTests.Ingested();
        var images = new List<ProjectImage>();
        for (var i = 0; i < 3; i++)
        {
            var path = Path.Combine(folder, stacks[i].Name + ".fits");
            PlanetaryComposition.WriteStack(stacks[i], path);
            images.Add(WorkbenchFixtures.Picture($"stack{i}", path));
        }

        images.Add(WorkbenchFixtures.Starting("colour", "stack0", WorkbenchFixtures.Edit(PlanetaryComposeOperation.Id, null, "stack1", "stack2")));
        images.Add(WorkbenchFixtures.StartingFromResult("luminance", "colour", PlanetaryComposeOperation.Luminance));
        // One hop past the result, with no edit and with an enabled edit: both are told in words, on the first run.
        images.Add(WorkbenchFixtures.Starting("hop", "luminance"));
        images.Add(WorkbenchFixtures.Starting("hop-edit", "luminance", WorkbenchFixtures.Edit("add", new JsonObject { ["amount"] = 1 })));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project([.. images]), ct);
        var runner = new WorkbenchRunner(new OperationCatalog([new PlanetaryComposeOperation(), new AddOperation("add")]));

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);
        var again = await runner.RunAsync(folder, cancellationToken: ct);

        ImageReport Of(WorkbenchReport of, string name) => of.Images.Single(image => image.Name == name);
        Of(report, "colour").Steps[0].State.ShouldBe(StepState.Ran, Messages(report));
        WorkbenchFixtures.Read(Of(report, "colour").Steps[0].Output.ShouldNotBeNull()).ChannelCount.ShouldBe(3);
        Of(report, "luminance").Refusal.ShouldNotBeNull().ShouldContain("gave no 'luminance'");
        Of(report, "hop").Refusal.ShouldNotBeNull().ShouldContain("gave no 'luminance'");
        Of(report, "hop-edit").Steps[0].State.ShouldBe(StepState.Blocked);
        Of(report, "hop-edit").Steps[0].Message.ShouldNotBeNull().ShouldContain("gave no 'luminance'");
        Of(again, "colour").Steps[0].State.ShouldBe(StepState.Kept, "the step is whole without the result it may leave out");
        Of(again, "luminance").Refusal.ShouldNotBeNull().ShouldContain("gave no 'luminance'");
        Of(again, "hop").Refusal.ShouldNotBeNull().ShouldContain("gave no 'luminance'");
    }

    // Heavy by measurement: 42.3 s on the Windows unit leg of #2482's first run, and 54 s (arm) and 1 m 24 s (x64) on the Linux
    // heavy run, most of it the stack of the capture and the sharpenings of its master, which the verb then repeats for the
    // line of each step and for each of its refusals and memory cases.
    [Fact(Timeout = 300_000)]
    [Trait("Category", "Heavy")]
    public async Task ThePlanetarySharpenStepIsTheFilePlanetarySharpenAutoWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("planetary-sharpen-").FullName;
        var capture = ViewerBestStackTests.WriteCapture(Path.Combine(folder, "Jupiter_Red_2024-12-15T12_56_44_OTA1.ser"),
            telescope: "254 mm f/4.7 Newtonian, Test OTA");
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("stacked", capture, WorkbenchFixtures.Edit(PlanetaryStackOperation.Id)),
            // Nothing said: what the master is, is read off the header its stack wrote.
            WorkbenchFixtures.Starting("again", "stacked", WorkbenchFixtures.Edit(PlanetarySharpenOperation.Id)),
            // Another telescope said: another step, and another sharpening.
            WorkbenchFixtures.Starting("told", "stacked", WorkbenchFixtures.Edit(PlanetarySharpenOperation.Id,
                new JsonObject { ["apertureMm"] = 203, ["design"] = "sct" }))), ct);
        var runner = new WorkbenchRunner(new OperationCatalog([new PlanetaryStackOperation(), new PlanetarySharpenOperation()]));
        var stepsSaid = new List<string>();

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions
        {
            Say = text =>
            {
                stepsSaid.Add(text);
                output.WriteLine(text);
            },
        }, ct);

        report.Succeeded.ShouldBeTrue(Messages(report));
        ImageReport Of(string name) => report.Images.Single(image => image.Name == name);
        var master = Of("stacked").Steps[0].Output.ShouldNotBeNull();
        Of("again").Steps[0].State.ShouldBe(StepState.Ran);
        Of("told").Steps[0].State.ShouldBe(StepState.Ran);
        Of("told").Steps[0].Key.ShouldNotBe(Of("again").Steps[0].Key, "what is said of a master is part of its step's key");
        // Each step says what it took its master to be: the telescope its stack wrote into the header, or the one given.
        stepsSaid.Any(static text => text.StartsWith("[again ", StringComparison.Ordinal) && text.Contains("AUTO:", StringComparison.Ordinal)
            && text.Contains("254", StringComparison.Ordinal)).ShouldBeTrue(string.Join(Environment.NewLine, stepsSaid));
        stepsSaid.Any(static text => text.StartsWith("[told ", StringComparison.Ordinal) && text.Contains("AUTO:", StringComparison.Ordinal)
            && text.Contains("203", StringComparison.Ordinal)).ShouldBeTrue(string.Join(Environment.NewLine, stepsSaid));

        // Each step's command line through the real verb, its one file named.
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        var sharpen = new PlanetarySharpenSubCommand(
            host,
            new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance),
            new FakeExternal(output, root: _folders.Create("memory-"))).Build();
        foreach (var name in new[] { "again", "told" })
        {
            var each = Of(name).Steps[0];
            var line = each.CommandLine.ToArray();
            line[..3].ShouldBe([StepCommandLine.Program, "planetary", "sharpen"]);
            line[3].ShouldBe(master, "the step sharpens the stack step's own file");
            line.ShouldContain("--auto");
            line.ShouldContain("--no-telescope-memory", "a step never asks what telescope a camera was last said to be on");
            var at = Array.IndexOf(line, "--sharpened") + 1;
            line[at].ShouldBe(each.Output.ShouldNotBeNull(), "the line names the step's own file");
            var written = Path.Combine(folder, name + "-verb.fits");
            line[at] = written;
            output.WriteLine(StepCommandLine.Display(line));
            said.Clear();

            var exit = await sharpen.Parse(line[3..]).InvokeAsync(cancellationToken: ct);

            exit.ShouldBe(0, string.Join(Environment.NewLine, said));
            ShouldBeTheSameFile(each.Output.ShouldNotBeNull(), written, $"the sharpened master of '{name}'");
        }

        // The result is not the master as stacked: an equal file would make the comparison above empty.
        var (stacked, again) = (WorkbenchFixtures.Read(master), WorkbenchFixtures.Read(Of("again").Steps[0].Output.ShouldNotBeNull()));
        stacked.GetChannelSpan(0).SequenceEqual(again.GetChannelSpan(0)).ShouldBeFalse("the step sharpened");

        // The verb without --sharpened is its research path: it reads and scores as it goes, and writes into a folder.
        // Its pixels are the step's all the same, so its file is too: AUTO sharpens one way wherever it is asked. That
        // holds where the gains are derived, which this master's are; where a limb does not fit, the research path
        // refuses and a step falls back to the preset, as a stack does.
        stepsSaid.Any(static text => text.StartsWith("[again ", StringComparison.Ordinal) && text.Contains("derived through the limb's edge", StringComparison.Ordinal))
            .ShouldBeTrue(string.Join(Environment.NewLine, stepsSaid));
        // The stack step's own sharpened master is the sharpen step's too, of the linear master it came from: a mono master has no
        // colour balance and the stack's de-rotation epoch is mid-capture, which the master's DATE-OBS and EXPTIME give. A sharpening
        // option forwarded by one routine and not the other would part them here.
        var stackSharpened = Path.Combine(folder, StepStore.FolderName,
            StepStore.FileNameOf(StepKey.OfResult(Of("stacked").Steps[0].Key, PlanetaryStackOperation.Sharpened)));
        WorkbenchFixtures.Read(stackSharpened).GetChannelSpan(0).SequenceEqual(again.GetChannelSpan(0))
            .ShouldBeTrue("the sharpen step of the stack's linear master is the stack step's own sharpened master");
        var research = _folders.Create("research-").FullName;
        said.Clear();
        var researched = await sharpen.Parse(new[] { master, "--auto", "--no-telescope-memory", "-o", research }).InvokeAsync(cancellationToken: ct);
        researched.ShouldBe(0, string.Join(Environment.NewLine, said));
        ShouldBeTheSameFile(Of("again").Steps[0].Output.ShouldNotBeNull(), Path.Combine(research, Path.GetFileNameWithoutExtension(master) + "_sharpened.fits"),
            "the verb's own AUTO against the step");

        // What goes with --sharpened and what does not, in words: each of the seven the verb lists, one by one.
        (string Name, string[] Args)[] refused =
        [
            ("--output", ["-o", research]), ("--no-write", ["--no-write"]), ("--stacked-preview", ["--stacked-preview"]),
            ("--truth", ["--truth", Path.Combine(folder, "none.truth.fits")]), ("--utc", ["--utc", "2024-12-15T12:56:44Z"]),
            ("--score-taper", ["--score-taper", "hann"]), ("--score-against", ["--score-against", "aperture"]),
        ];
        foreach (var (name, args) in refused)
        {
            said.Clear();
            var target = Path.Combine(folder, "x.fits");
            (await sharpen.Parse(new[] { master, "--auto", "--sharpened", target }.Concat(args).ToArray()).InvokeAsync(cancellationToken: ct)).ShouldBe(1, name);
            string.Join(Environment.NewLine, said).ShouldContain($"{name} does not go with it", customMessage: name);
            File.Exists(target).ShouldBeFalse(name);
        }

        // --no-telescope-memory: a master whose header names no telescope, of a camera the memory holds one for. Without the flag
        // the sharpening goes by the remembered telescope; with it the memory is neither asked nor told, and the file is the
        // one a memory holding nothing gives.
        var bare = WorkbenchFixtures.Read(master);
        var bareMaster = Path.Combine(_folders.Create("bare-").FullName, "bare.fits");
        new Image([bare.GetChannelArray(0)], BitDepth.Float32, bare.MaxValue, bare.MinValue, 0,
            bare.ImageMeta with { Telescope = "", Aperture = -1, Instrument = "Memory Test Cam" }).WriteToFitsFile(bareMaster);
        var memory = new FakeExternal(output, root: _folders.Create("remembers-"));
        await PlanetaryTelescopeMemory.RememberAsync(memory, "Memory Test Cam", 203, OpticalDesign.SCT, ct);
        var withMemory = new PlanetarySharpenSubCommand(host, new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance), memory).Build();
        var (told, untold, nothing) = (Path.Combine(folder, "told.fits"), Path.Combine(folder, "untold.fits"), Path.Combine(folder, "nothing.fits"));
        said.Clear();
        (await withMemory.Parse(new[] { bareMaster, "--auto", "--sharpened", told }).InvokeAsync(cancellationToken: ct)).ShouldBe(0, string.Join(Environment.NewLine, said));
        (await withMemory.Parse(new[] { bareMaster, "--auto", "--no-telescope-memory", "--sharpened", untold }).InvokeAsync(cancellationToken: ct)).ShouldBe(0, string.Join(Environment.NewLine, said));
        (await sharpen.Parse(new[] { bareMaster, "--auto", "--sharpened", nothing }).InvokeAsync(cancellationToken: ct)).ShouldBe(0, string.Join(Environment.NewLine, said));
        File.ReadAllBytes(told).AsSpan().SequenceEqual(File.ReadAllBytes(untold)).ShouldBeFalse("the memory's telescope was asked when the flag was not given");
        ShouldBeTheSameFile(untold, nothing, "the master with the flag, against one whose memory holds nothing");
        said.Clear();
        (await sharpen.Parse(new[] { master, "--sharpened", Path.Combine(folder, "x.fits") }).InvokeAsync(cancellationToken: ct)).ShouldBe(1);
        string.Join(Environment.NewLine, said).ShouldContain("go with --auto");
        File.Exists(Path.Combine(folder, "x.fits")).ShouldBeFalse();

        var second = await runner.RunAsync(folder, cancellationToken: ct);

        second.Images.SelectMany(static image => image.Steps).Select(static step => step.State).ShouldBe([StepState.Kept, StepState.Kept, StepState.Kept]);
    }

    [Fact(Timeout = 300_000)]
    public async Task AStrengthIsItsVerbsOptionOnTheStackAndOnTheSharpening()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("planetary-strength-").FullName;
        // A mono capture, which stacks in seconds: the strength is the one of AUTO's choices a mono master has. The two
        // that are a colour master's are held by the heavy test after this one.
        var capture = ViewerBestStackTests.WriteCapture(Path.Combine(folder, "Jupiter_Red_2024-12-15T12_56_44_OTA1.ser"),
            telescope: "254 mm f/4.7 Newtonian, Test OTA");
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("strong", capture, WorkbenchFixtures.Edit(PlanetaryStackOperation.Id, new JsonObject { ["strength"] = 1.5 })),
            // The master sharpened again at another strength: only the sharpening runs.
            WorkbenchFixtures.Starting("stronger", "strong", WorkbenchFixtures.Edit(PlanetarySharpenOperation.Id, new JsonObject { ["strength"] = 2 }))), ct);
        var runner = new WorkbenchRunner(new OperationCatalog([new PlanetaryStackOperation(), new PlanetarySharpenOperation()]));

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        report.Succeeded.ShouldBeTrue(Messages(report));
        ImageReport Of(string name) => report.Images.Single(image => image.Name == name);
        var (stacked, again) = (Of("strong").Steps[0], Of("stronger").Steps[0]);
        var ownSharpened = Path.Combine(folder, StepStore.FolderName, StepStore.FileNameOf(StepKey.OfResult(stacked.Key, PlanetaryStackOperation.Sharpened)));

        // Each step's command line through its real verb, the strength on it.
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        var renderer = new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance);
        var stack = new PlanetaryStackSubCommand(host, renderer, new FakeExternal(output, root: _folders.Create("memory-"))).Build();
        var stackLine = stacked.CommandLine.ToArray();
        stackLine.ShouldContain("--strength");
        var (verbMaster, verbSharpened) = (Path.Combine(folder, "strong-verb.fits"), Path.Combine(folder, "strong-verb-sharpened.fits"));
        (stackLine[Array.IndexOf(stackLine, "--master") + 1], stackLine[Array.IndexOf(stackLine, "--sharpened") + 1]) = (verbMaster, verbSharpened);
        output.WriteLine(StepCommandLine.Display(stackLine));

        var stackExit = await stack.Parse(stackLine[3..]).InvokeAsync(cancellationToken: ct);

        stackExit.ShouldBe(0, string.Join(Environment.NewLine, said));
        ShouldBeTheSameFile(stacked.Output.ShouldNotBeNull(), verbMaster, "the master stacked with a strength");
        ShouldBeTheSameFile(ownSharpened, verbSharpened, "its sharpened master at that strength");

        var sharpen = new PlanetarySharpenSubCommand(host, renderer, new FakeExternal(output, root: _folders.Create("memory-"))).Build();
        var sharpenLine = again.CommandLine.ToArray();
        sharpenLine.ShouldContain("--strength");
        var written = Path.Combine(folder, "stronger-verb.fits");
        sharpenLine[Array.IndexOf(sharpenLine, "--sharpened") + 1] = written;
        output.WriteLine(StepCommandLine.Display(sharpenLine));
        said.Clear();

        var sharpenExit = await sharpen.Parse(sharpenLine[3..]).InvokeAsync(cancellationToken: ct);

        sharpenExit.ShouldBe(0, string.Join(Environment.NewLine, said));
        ShouldBeTheSameFile(again.Output.ShouldNotBeNull(), written, "the master sharpened again at another strength");

        // A strength is another sharpening: the stack's own at 1.5 is not the one made again at 2. Equal files would
        // make the comparisons above empty.
        WorkbenchFixtures.Read(ownSharpened).GetChannelSpan(0).SequenceEqual(WorkbenchFixtures.Read(again.Output.ShouldNotBeNull()).GetChannelSpan(0)).ShouldBeFalse();

        // The research path takes no strength under --auto, and a list of strengths is no step's.
        var master = stacked.Output.ShouldNotBeNull();
        said.Clear();
        (await sharpen.Parse(new[] { master, "--auto", "--strength", "1.5" }).InvokeAsync(cancellationToken: ct)).ShouldBe(1);
        string.Join(Environment.NewLine, said).ShouldContain("is a setting it does not take");
        said.Clear();
        (await sharpen.Parse(new[] { master, "--auto", "--sharpened", Path.Combine(folder, "x.fits"), "--strength", "1,1.5" }).InvokeAsync(cancellationToken: ct))
            .ShouldBe(1);
        string.Join(Environment.NewLine, said).ShouldContain("one number above 0");
        File.Exists(Path.Combine(folder, "x.fits")).ShouldBeFalse();
    }

    // A colour capture: the balance and the luminance-only sharpening are a colour master's.
    private static string ColourCapture(string folder)
        => ViewerBestStackTests.WriteCapture(Path.Combine(folder, "Jupiter_2024-12-15T12_56_44_OTA1.ser"),
            telescope: "254 mm f/4.7 Newtonian, Test OTA", dispersed: true);

    // A console host that keeps what a verb says.
    private static (IConsoleHost Host, List<string> Said) Listening()
    {
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        return (host, said);
    }

    // A stack step's command line through the real verb: its master and its sharpened master are the verb's two files.
    private async Task TheStackStepIsTheTwoFilesItsVerbWritesAsync(string folder, string name, StepReport step, IConsoleHost host, List<string> said,
        MasterPreviewRenderer renderer)
    {
        var stack = new PlanetaryStackSubCommand(host, renderer, new FakeExternal(output, root: _folders.Create("memory-"))).Build();
        var sharpenedOfStep = Path.Combine(folder, StepStore.FolderName, StepStore.FileNameOf(StepKey.OfResult(step.Key, PlanetaryStackOperation.Sharpened)));
        var line = step.CommandLine.ToArray();
        var (atMaster, atSharpened) = (Array.IndexOf(line, "--master") + 1, Array.IndexOf(line, "--sharpened") + 1);
        var (master, sharpened) = (Path.Combine(folder, name + "-verb.fits"), Path.Combine(folder, name + "-verb-sharpened.fits"));
        (line[atMaster], line[atSharpened]) = (master, sharpened);
        output.WriteLine(StepCommandLine.Display(line));
        said.Clear();

        var exit = await stack.Parse(line[3..]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        exit.ShouldBe(0, string.Join(Environment.NewLine, said));
        ShouldBeTheSameFile(step.Output.ShouldNotBeNull(), master, $"the master of '{name}'");
        ShouldBeTheSameFile(sharpenedOfStep, sharpened, $"the sharpened master of '{name}'");
    }

    // Heavy by measurement, and two tests by measurement. As one test (four AUTO runs of a colour capture: two stack
    // steps, each again through the verb) it took 58.7 s on the x64 leg of #2483's first run and 3 min 35 s in the
    // heavy run on the branch, under the load of the other heavy tests, against a bound of 5 min. A Bayer capture stacks
    // and sharpens three planes, so each AUTO run is its own cost; this test has two of them and the next the other
    // two. The strength, which a mono capture has too, is held in the PR legs by the test before these.
    [Fact(Timeout = 300_000)]
    [Trait("Category", "Heavy")]
    public async Task AutosChoicesOnAColourStackAreItsVerbsOptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("planetary-choices-").FullName;
        var capture = ColourCapture(folder);
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("chosen", capture, WorkbenchFixtures.Edit(PlanetaryStackOperation.Id,
                new JsonObject { ["strength"] = 1.5, ["luminanceOnly"] = true, ["colourSaturation"] = 1.4 }))), ct);
        var runner = new WorkbenchRunner(new OperationCatalog([new PlanetaryStackOperation()]));

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        report.Succeeded.ShouldBeTrue(Messages(report));
        var chosen = report.Images.Single(static image => image.Name == "chosen").Steps[0];
        WorkbenchFixtures.Read(chosen.Output.ShouldNotBeNull()).ChannelCount.ShouldBe(3, "a Bayer capture stacks to a colour master");

        // The step's command line through the real verb: the choices are on it, and both files are the verb's.
        var (host, said) = Listening();
        await TheStackStepIsTheTwoFilesItsVerbWritesAsync(folder, "chosen", chosen, host, said, new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance));

        var line = chosen.CommandLine.ToArray();
        line.ShouldContain("--strength");
        line.ShouldContain("--sharpen-luminance");
        line.ShouldContain("--colour-saturation");
    }

    // Heavy with the test above, whose comment has the measurements: the other two AUTO runs of a colour capture, and
    // two sharpenings of its master.
    [Fact(Timeout = 300_000)]
    [Trait("Category", "Heavy")]
    public async Task AStackLeftAsCapturedAndItsMasterSharpenedAgainAreTheirVerbsFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("planetary-as-captured-").FullName;
        var capture = ColourCapture(folder);
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("as-captured", capture, WorkbenchFixtures.Edit(PlanetaryStackOperation.Id, new JsonObject { ["colourBalance"] = false })),
            // The unbalanced master sharpened again, at a strength and by its luminance: only the sharpening runs.
            WorkbenchFixtures.Starting("resharpened", "as-captured", WorkbenchFixtures.Edit(PlanetarySharpenOperation.Id,
                new JsonObject { ["strength"] = 1.5, ["luminanceOnly"] = true }))), ct);
        var runner = new WorkbenchRunner(new OperationCatalog([new PlanetaryStackOperation(), new PlanetarySharpenOperation()]));
        var stepsSaid = new List<string>();

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions
        {
            Say = text =>
            {
                stepsSaid.Add(text);
                output.WriteLine(text);
            },
        }, ct);

        report.Succeeded.ShouldBeTrue(Messages(report));
        ImageReport Of(string name) => report.Images.Single(image => image.Name == name);
        var asCaptured = Of("as-captured").Steps[0];
        // With the balance off the step says so, in the stack's own words.
        stepsSaid.Any(static text => text.StartsWith("[as-captured ", StringComparison.Ordinal) && text.Contains("colours left as captured", StringComparison.Ordinal))
            .ShouldBeTrue(string.Join(Environment.NewLine, stepsSaid));

        // The stack step's command line through the real verb: the choice is on it, and both files are the verb's.
        var (host, said) = Listening();
        var renderer = new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance);
        await TheStackStepIsTheTwoFilesItsVerbWritesAsync(folder, "as-captured", asCaptured, host, said, renderer);
        asCaptured.CommandLine.ToArray().ShouldContain("--no-colour-balance");

        // The sharpen step's two choices through its verb.
        var sharpen = new PlanetarySharpenSubCommand(host, renderer, new FakeExternal(output, root: _folders.Create("memory-"))).Build();
        var again = Of("resharpened").Steps[0];
        var sharpenLine = again.CommandLine.ToArray();
        sharpenLine.ShouldContain("--strength");
        sharpenLine.ShouldContain("--colour");
        var written = Path.Combine(folder, "resharpened-verb.fits");
        sharpenLine[Array.IndexOf(sharpenLine, "--sharpened") + 1] = written;
        output.WriteLine(StepCommandLine.Display(sharpenLine));
        said.Clear();

        var sharpenExit = await sharpen.Parse(sharpenLine[3..]).InvokeAsync(cancellationToken: ct);

        sharpenExit.ShouldBe(0, string.Join(Environment.NewLine, said));
        ShouldBeTheSameFile(again.Output.ShouldNotBeNull(), written, "the master sharpened again at a strength");

        // A strength is another sharpening: the unbalanced stack's own sharpened master, at 1, is not the one made again
        // at 1.5. Equal files would make the comparisons above empty.
        var sharpenedOfStack = Path.Combine(folder, StepStore.FolderName, StepStore.FileNameOf(StepKey.OfResult(asCaptured.Key, PlanetaryStackOperation.Sharpened)));
        WorkbenchFixtures.Read(sharpenedOfStack).GetChannelSpan(0)
            .SequenceEqual(WorkbenchFixtures.Read(again.Output.ShouldNotBeNull()).GetChannelSpan(0)).ShouldBeFalse();
    }

    [Fact(Timeout = 120_000)]
    public async Task AMasterWithNoFileTakesNoNameOrFolderForItsOwn()
    {
        // A crop's output is not a file: its input has no path, and the identification reads no name, so the process's working
        // directory (whose folders may name a planet, a filter or a telescope) is never taken for the master's.
        // The master's own header names nothing either (the fixture's names a filter and an instrument), so only a file name could.
        var source = WorkbenchFixtures.Master(seed: 1);
        var master = new Image([source.GetChannelArray(0)], BitDepth.Float32, 1f, 0f, 0f,
            WorkbenchFixtures.Meta() with { Filter = Filter.None, Instrument = "", Telescope = "" });
        try
        {
            var identity = await PlanetaryIdentification.IdentifyMasterAsync(master, path: null, cancellationToken: TestContext.Current.CancellationToken);

            (identity.Planet, identity.PlanetFrom).ShouldBe(((CatalogIndex?)null, "nothing names one"));
            // The header names no filter, and there is no file name to read one from.
            (identity.FilterNm, identity.FilterFrom).ShouldBe(((double?)null, "none named"));
            // No aperture and no camera: nothing to remember a telescope for either.
            (identity.ApertureMm, identity.TelescopeFrom).ShouldBe(((int?)null, "none named"));
        }
        finally
        {
            master.Release();
            source.Release();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task ACropIntoPlanetarySharpenTakesNoPlanetFromTheFolderOfTheMasterBehindIt()
    {
        // The step after a crop reads a derived input: no path. A master filed in a folder named for a planet must not give the
        // cropped one that planet, whatever the step goes on to do with it (the line is said before the sharpening).
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("planetary-sharpen-crop-").FullName;
        var inSaturn = Directory.CreateDirectory(Path.Combine(folder, "Saturn")).FullName;
        var source = WorkbenchFixtures.Master(seed: 1);
        var bare = new Image([source.GetChannelArray(0)], BitDepth.Float32, 1f, 0f, 0f,
            WorkbenchFixtures.Meta() with { Filter = Filter.None, Instrument = "", Telescope = "" });
        var master = WorkbenchFixtures.WriteMaster(inSaturn, "master", bare);
        bare.Release();
        source.Release();
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("cut", master,
                WorkbenchFixtures.Edit(CropOperation.Id, new JsonObject { ["x"] = 8, ["y"] = 8, ["width"] = 96, ["height"] = 96 }),
                WorkbenchFixtures.Edit(PlanetarySharpenOperation.Id))), ct);
        var said = new List<string>();

        await new WorkbenchRunner(new OperationCatalog([new CropOperation(), new PlanetarySharpenOperation()]))
            .RunAsync(folder, new WorkbenchRunOptions { Say = said.Add }, ct);

        var line = said.FirstOrDefault(static text => text.Contains("AUTO:", StringComparison.Ordinal)).ShouldNotBeNull(string.Join(Environment.NewLine, said));
        line.ShouldContain("nothing names one");
        line.ShouldNotContain("folder", Case.Insensitive);
    }

    [Fact(Timeout = 120_000)]
    public async Task AFileThatIsNoCaptureIsARefusalOfItsStepAndNeverAThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("no-capture-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("m", master, WorkbenchFixtures.Edit(PlanetaryStackOperation.Id))), ct);

        var report = await new WorkbenchRunner(new OperationCatalog([new PlanetaryStackOperation()])).RunAsync(folder, cancellationToken: ct);

        report.Succeeded.ShouldBeFalse();
        report.Images[0].Steps[0].State.ShouldBe(StepState.Refused);
        report.Images[0].Steps[0].Message.ShouldNotBeNull().ShouldContain("could not be stacked as a planetary capture");
        var store = Path.Combine(folder, StepStore.FolderName);
        (Directory.Exists(store) ? Directory.GetFiles(store, "*.fits") : []).ShouldBeEmpty();
    }

    [Fact]
    public void TheSettingsSayTheCaptureAndRefuseWhatIsNoTelescopeOrPlanet()
    {
        var stack = new PlanetaryStackOperation();

        stack.Refusal(new PlanetaryStackSettings()).ShouldBeNull();
        stack.Refusal(new PlanetaryStackSettings { Planet = "saturn", FilterNm = 650, ApertureMm = 254, Design = OpticalDesign.Newtonian }).ShouldBeNull();
        stack.Refusal(new PlanetaryStackSettings { Planet = "nowhere" }).ShouldNotBeNull().ShouldContain("not a planet's name");
        stack.Refusal(new PlanetaryStackSettings { FilterNm = 0 }).ShouldNotBeNull().ShouldContain("filterNm");
        // The verb's --wavelength takes above 200 nm and below 2000 nm, both edges refused, so the step's line is one the verb takes.
        foreach (var nm in new[] { 200, 100, 2000, 5000, double.NaN, double.PositiveInfinity })
        {
            stack.Refusal(new PlanetaryStackSettings { FilterNm = nm }).ShouldNotBeNull().ShouldContain("filterNm");
        }
        stack.Refusal(new PlanetaryStackSettings { FilterNm = 200.5 }).ShouldBeNull();
        stack.Refusal(new PlanetaryStackSettings { FilterNm = 1999.5 }).ShouldBeNull();
        stack.Refusal(new PlanetaryStackSettings { ApertureMm = 0 }).ShouldNotBeNull().ShouldContain("apertureMm");
        stack.Refusal(new PlanetaryStackSettings { Design = OpticalDesign.SCT }).ShouldNotBeNull().ShouldContain("give its aperture too");

        stack.CommandLine(new PlanetaryStackSettings(), ["capture.ser"], PlanetaryStackOperation.Outputs("m.fits", "s.fits")).ToArray()
            .ShouldBe(
            [
                StepCommandLine.Program, "planetary", "stack", "capture.ser", "--auto", "--no-telescope-memory", "--no-png",
                "--master", "m.fits", "--sharpened", "s.fits",
            ]);
        stack.CommandLine(new PlanetaryStackSettings { Planet = "saturn", FilterNm = 650, ApertureMm = 254, Design = OpticalDesign.Newtonian },
                ["capture.ser"], PlanetaryStackOperation.Outputs("m.fits", "s.fits")).ToArray()[11..]
            .ShouldBe(["--planet", "saturn", "--wavelength", "650", "--aperture-mm", "254", "--design", "newtonian"]);

        var given = PlanetaryStackOperation.GivenOf(new PlanetaryStackSettings { Planet = "saturn", ApertureMm = 254, Design = OpticalDesign.Newtonian });
        given.Planet.ShouldNotBeNull();
        (given.ApertureMm, given.Design).ShouldBe(((int?)254, OpticalDesign.Newtonian));
        stack.Description.ExtraResults.Select(static result => result.Name).ShouldBe([PlanetaryStackOperation.Sharpened]);

        // AUTO's three choices: what they refuse, how the line says them, and what they come to.
        stack.Refusal(new PlanetaryStackSettings { Strength = 1.5, LuminanceOnly = true, ColourSaturation = 1.4 }).ShouldBeNull();
        stack.Refusal(new PlanetaryStackSettings { Strength = 0 }).ShouldNotBeNull().ShouldContain("strength is more than 0");
        stack.Refusal(new PlanetaryStackSettings { ColourSaturation = -1 }).ShouldNotBeNull().ShouldContain("colourSaturation is 0 or more");
        stack.Refusal(new PlanetaryStackSettings { ColourBalance = false, ColourSaturation = 1.4 }).ShouldNotBeNull().ShouldContain("nothing it saturates");
        var outputs = PlanetaryStackOperation.Outputs("m.fits", "s.fits");
        stack.CommandLine(new PlanetaryStackSettings { Strength = 1.5, LuminanceOnly = true, ColourSaturation = 1.4, Planet = "saturn" }, ["capture.ser"], outputs)
            .ToArray()[11..].ShouldBe(["--strength", "1.5", "--sharpen-luminance", "--colour-saturation", "1.4", "--planet", "saturn"]);
        stack.CommandLine(new PlanetaryStackSettings { ColourBalance = false }, ["capture.ser"], outputs).ToArray()[11..].ShouldBe(["--no-colour-balance"]);
        PlanetaryStackOperation.ChoicesOf(new PlanetaryStackSettings()).ShouldBe(PlanetaryAutoChoices.Defaults);
        PlanetaryStackOperation.ChoicesOf(new PlanetaryStackSettings { ColourBalance = false }).ColourSaturation.ShouldBeNull();
        PlanetaryStackOperation.ChoicesOf(new PlanetaryStackSettings { Strength = 2, LuminanceOnly = true, ColourSaturation = 1.4 })
            .ShouldBe(new PlanetaryAutoChoices { Strength = 2, LuminanceOnly = true, ColourSaturation = 1.4 });
    }

    [Theory(Timeout = 120_000)]
    [InlineData("--strength", "0", "--strength")]
    [InlineData("--strength", "NaN", "--strength")]
    [InlineData("--colour-saturation", "-1", "--colour-saturation is 0 or more")]
    public async Task AutoRefusesABadStrengthOrSaturationBeforeItStacks(string option, string value, string said)
        => await AutoRefusesAsync([option, value], said);

    [Fact(Timeout = 120_000)]
    public async Task AutoRefusesASaturationWithTheColourBalanceOff()
        => await AutoRefusesAsync(["--no-colour-balance", "--colour-saturation", "1.4"], "with --no-colour-balance");

    private async Task AutoRefusesAsync(string[] choice, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("planetary-auto-refuses-").FullName;
        var capture = ViewerBestStackTests.WriteCapture(Path.Combine(folder, "Jupiter_Red_2024-12-15T12_56_44_OTA1.ser"),
            telescope: "254 mm f/4.7 Newtonian, Test OTA");
        var told = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => told.Add(call.Arg<string>()));
        var renderer = new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance);
        var stack = new PlanetaryStackSubCommand(host, renderer, new FakeExternal(output, root: _folders.Create("memory-"))).Build();

        var exit = await stack.Parse([capture, "--auto", .. choice]).InvokeAsync(cancellationToken: ct);

        exit.ShouldBe(1, string.Join(Environment.NewLine, told));
        string.Join(Environment.NewLine, told).ShouldContain(expected);
        Directory.EnumerateFiles(folder, "*.fits").ShouldBeEmpty("a refusal comes before any stacking");
    }

    [Fact]
    public void AutoOptionsCarryTheThreeChoicesIntoTheBestStack()
    {
        var identity = new PlanetaryIdentity(null, "", null, null, "", null, OpticalDesign.Unknown, "", null, default);
        var chosen = PlanetaryAuto.OptionsFor(identity, new PlanetaryAutoChoices { Strength = 1.5, LuminanceOnly = true, ColourSaturation = 1.4 });
        (chosen.Strength, chosen.LuminanceOnly, chosen.ColourSaturation).ShouldBe((1.5, true, (double?)1.4));
        PlanetaryAuto.OptionsFor(identity, new PlanetaryAutoChoices { ColourSaturation = null }).ColourSaturation.ShouldBeNull();

        var (plain, defaults) = (PlanetaryAuto.OptionsFor(identity), PlanetaryAuto.OptionsFor(identity, PlanetaryAutoChoices.Defaults));
        (plain.Strength, plain.LuminanceOnly, plain.ColourSaturation).ShouldBe((defaults.Strength, defaults.LuminanceOnly, defaults.ColourSaturation));
    }

    [Fact]
    public void TheComposeSettingsAreItsVerbsOptionsAndRefuseHalfARingFeather()
    {
        var compose = new PlanetaryComposeOperation();

        compose.Refusal(new PlanetaryComposeSettings()).ShouldBeNull();
        compose.Refusal(new PlanetaryComposeSettings { Planet = "nowhere" }).ShouldNotBeNull().ShouldContain("not a planet's name");
        compose.Refusal(new PlanetaryComposeSettings { RingFeatherInner = 2 }).ShouldNotBeNull().ShouldContain("state both");
        compose.Refusal(new PlanetaryComposeSettings { RingFeatherInner = 4, RingFeatherOuter = 2 }).ShouldNotBeNull().ShouldContain("past it");
        compose.Refusal(new PlanetaryComposeSettings { RingFeatherInner = 2, RingFeatherOuter = 4 }).ShouldBeNull();

        var outputs = PlanetaryComposeOperation.Outputs("m.fits", "l.fits");
        compose.CommandLine(new PlanetaryComposeSettings(), ["a.fits", "b.fits"], outputs).ToArray()
            .ShouldBe(
            [
                StepCommandLine.Program, "planetary", "compose", "a.fits", "b.fits", "-o", "m.fits", "--luminance-output", "l.fits",
                "--relight", "blurred",
            ]);
        var everything = new PlanetaryComposeSettings
        {
            Planet = "saturn",
            Lrgb = true,
            ViewingWeights = PlanetaryViewingWeights.Limb,
            BandWeights = PlanetaryBandWeights.Wiener,
            Relight = PlanetaryTurnRelight.Minnaert,
            RingFeatherInner = 2,
            RingFeatherOuter = 4,
            ColourBalance = false,
        };
        compose.CommandLine(everything, ["a.fits"], outputs).ToArray()[8..]
            .ShouldBe(
            [
                "--planet", "saturn", "--lrgb", "--viewing-weights", "limb", "--band-weights", "wiener", "--relight", "minnaert",
                "--ring-feather", "2,4", "--no-colour-balance",
            ]);

        // An edit states them as the verb's options are spelt, and a setting the operation does not have is refused.
        compose.TryReadSettings(
            new JsonObject { ["viewingWeights"] = "limb", ["bandWeights"] = "wiener", ["relight"] = "minnaert", ["ringFeatherInner"] = 2, ["ringFeatherOuter"] = 4 }
                .ToJsonString(),
            out _, out var refusal).ShouldBeTrue(refusal);
        compose.TryReadSettings(new JsonObject { ["utc"] = "2024-12-15T12:56:44Z" }.ToJsonString(), out _, out _).ShouldBeFalse();

        compose.Description.AnyNumber.ShouldBeTrue();
        compose.Description.ExtraResults.Select(static result => (result.Name, result.Optional)).ShouldBe([(PlanetaryComposeOperation.Luminance, true)]);
    }

    [Fact]
    public void TheSharpenSettingsSayTheMasterAsTheStacksSayTheCapture()
    {
        var sharpen = new PlanetarySharpenOperation();

        sharpen.Refusal(new PlanetarySharpenSettings()).ShouldBeNull();
        sharpen.Refusal(new PlanetarySharpenSettings { Planet = "nowhere" }).ShouldNotBeNull().ShouldContain("not a planet's name");
        sharpen.Refusal(new PlanetarySharpenSettings { Design = OpticalDesign.SCT }).ShouldNotBeNull().ShouldContain("give its aperture too");

        sharpen.CommandLine(new PlanetarySharpenSettings(), ["master.fits"], "sharpened.fits").ToArray()
            .ShouldBe([StepCommandLine.Program, "planetary", "sharpen", "master.fits", "--auto", "--no-telescope-memory", "--sharpened", "sharpened.fits"]);
        sharpen.CommandLine(new PlanetarySharpenSettings { Planet = "saturn", FilterNm = 650, ApertureMm = 254, Design = OpticalDesign.Newtonian },
                ["master.fits"], "sharpened.fits").ToArray()[8..]
            .ShouldBe(["--planet", "saturn", "--wavelength", "650", "--aperture-mm", "254", "--design", "newtonian"]);

        // The two AUTO steps say a capture or a master one way, and each takes the choices that are its own.
        var stated = new JsonObject { ["planet"] = "saturn", ["apertureMm"] = 254, ["design"] = "newtonian", ["strength"] = 1.5, ["luminanceOnly"] = true }
            .ToJsonString();
        sharpen.TryReadSettings(stated, out _, out var refusal).ShouldBeTrue(refusal);
        new PlanetaryStackOperation().TryReadSettings(stated, out _, out refusal).ShouldBeTrue(refusal);
        // The colour balance is the stack's: a master read from a file is not balanced again.
        sharpen.TryReadSettings(new JsonObject { ["colourSaturation"] = 1.4 }.ToJsonString(), out _, out _).ShouldBeFalse();

        sharpen.Refusal(new PlanetarySharpenSettings { Strength = -1 }).ShouldNotBeNull().ShouldContain("strength is more than 0");
        sharpen.CommandLine(new PlanetarySharpenSettings { Strength = 1.5, LuminanceOnly = true }, ["master.fits"], "sharpened.fits").ToArray()[8..]
            .ShouldBe(["--strength", "1.5", "--colour", "luminance"]);
        PlanetarySharpenOperation.ChoicesOf(new PlanetarySharpenSettings { Strength = 2 }).ShouldBe(new PlanetaryAutoChoices { Strength = 2 });
    }

    [Fact(Timeout = 60_000)]
    public async Task TheListOfOperationsSaysWhatAStepTakesAndGivesBesideItsOwnImage()
    {
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        var catalog = new OperationCatalog([new PlanetaryStackOperation(), new PlanetaryComposeOperation()]);
        var workbench = new WorkbenchSubCommand(host, catalog, new WorkbenchRunner(catalog)).Build();

        var exit = await workbench.Parse(new[] { "operations" }).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        var text = string.Join(Environment.NewLine, said);
        exit.ShouldBe(0, text);
        text.ShouldContain("planetary-compose (version 1)");
        text.ShouldContain("takes any number of other images, as a set");
        text.ShouldContain("gives luminance, for some inputs only:");
        text.ShouldContain("gives sharpened:");
    }
}
