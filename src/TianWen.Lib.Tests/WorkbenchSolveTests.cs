using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>A plate solver that answers what it was told to, and keeps what it was asked.</summary>
internal sealed class FixedPlateSolver(WCS? solution) : IPlateSolverFactory
{
    /// <summary>What it answers; null for "no solution".</summary>
    public WCS? Solution { get; set; } = solution;

    /// <summary>When set, a solve throws a <see cref="PlateSolverException"/> saying this.</summary>
    public string? Throws { get; set; }

    /// <summary>Every solve it was asked for, in order.</summary>
    public List<(string File, ImageDim? Dim, float Range, WCS? Origin, double? Radius)> Asked { get; } = [];

    public int Solves => Asked.Count;

    public string Name => "Test/Solver";

    public float Priority => 1f;

    public IPlateSolver? SelectedPlateSolver => this;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

    public Task<PlateSolveResult> SolveFileAsync(string fitsFile, ImageDim? imageDim = default, float range = 0.03f, WCS? searchOrigin = default,
        double? searchRadius = default, CancellationToken cancellationToken = default)
    {
        Asked.Add((fitsFile, imageDim, range, searchOrigin, searchRadius));
        if (Throws is { } why)
        {
            throw new PlateSolverException(why);
        }

        return Task.FromResult(new PlateSolveResult(Solution, TimeSpan.Zero) { MatchedStars = 9, DetectedStars = 12, CatalogStars = 40 });
    }
}

/// <summary>An operation that says it writes no pixels and has no finding: a catalog refuses it.</summary>
internal sealed class SaysFindingOperation : Operation<AddSettings>
{
    public override OperationDescription Description { get; } = new(
        "says-finding", Version: 1, Title: "Says", Verb: "none", OperationOutputKind.Finding, ExtraInputs: [], Settings: []);

    protected override JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override ImmutableArray<string> CommandLine(AddSettings settings, IReadOnlyList<string> inputs, string output) => [Description.Id];

    public override ValueTask<OperationOutcome> RunAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(OperationOutcome.Done);
}

/// <summary>
/// An operation that writes no pixels (#2469), with the plate solve as the first: its solution is kept in the step
/// store and the pixels are not, the step after it takes the solution, and the step is the file
/// <c>tianwen solve -o</c> writes. A stand-in solver answers a fixed solution, the same one for the step and the verb.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchSolveTests(ITestOutputHelper output) : IDisposable
{
    private const float Tolerance = 1e-6f;

    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    // Not the solution the fixture masters come with, and with numbers that take every digit a card holds.
    private static WCS Solution() => new WCS(83.822083 / 15.0, -5.391111)
    {
        CRPix1 = 127.3125,
        CRPix2 = 95.6875,
        CD1_1 = -0.00052731234567891234,
        CD1_2 = 0.000013456789012345678,
        CD2_1 = 0.000013987654321098765,
        CD2_2 = 0.00052698765432109876,
    };

    private static StepState[] States(WorkbenchReport report, int image = 0) => [.. report.Images[image].Steps.Select(static step => step.State)];

    private static string Messages(WorkbenchReport report)
        => string.Join("; ", report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message));

    private static string[] StoreFiles(string folder)
    {
        var store = Path.Combine(folder, StepStore.FolderName);
        return Directory.Exists(store) ? Directory.GetFiles(store, "*.fits") : [];
    }

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

    [Fact(Timeout = 120_000)]
    public async Task ASolveStepKeepsItsSolutionAndNoPixelsAndTheStepAfterItTakesTheSolution()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("solve-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", master,
            WorkbenchFixtures.Edit(SolveOperation.Id), WorkbenchFixtures.Edit("add", new JsonObject { ["amount"] = 1 }))), ct);
        var solver = new FixedPlateSolver(Solution());
        var add = new AddOperation("add");
        var runner = new WorkbenchRunner(new OperationCatalog([new SolveOperation(solver), add]));

        var status = await runner.StatusAsync(folder, cancellationToken: ct);
        var first = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        States(status).ShouldBe([StepState.WillRun, StepState.WillRun], "a status says what a run will do");
        solver.Solves.ShouldBe(1, "a status solves nothing");
        first.Succeeded.ShouldBeTrue(Messages(first));
        States(first).ShouldBe([StepState.Ran, StepState.Ran]);
        solver.Asked[0].File.ShouldBe(master, "an image that is a file is solved by its path");

        // What the store holds of the solve is the solution: a header and no data.
        var finding = first.Images[0].Steps[0].Output.ShouldNotBeNull();
        new FileInfo(finding).Length.ShouldBeLessThanOrEqualTo(2 * 2880L, "the solution's cards, and no pixels");
        var kept = PlateSolutionFile.Read(finding);
        kept.ShouldNotBeNull();
        kept.Value.CenterRA.ShouldBe(Solution().CenterRA, 1e-12);
        kept.Value.CenterDec.ShouldBe(Solution().CenterDec, 1e-12);
        kept.Value.CRPix1.ShouldBe(Solution().CRPix1, 1e-9);
        kept.Value.CD1_2.ShouldBe(Solution().CD1_2, 1e-12);
        StoreFiles(folder).Length.ShouldBe(2, "the solution and the step after it, and no solved image");

        // The step after it read the master's pixels and the solve's solution, not the one the master came with.
        Image.TryReadFitsFile(first.Images[0].Steps[1].Output.ShouldNotBeNull(), out var result, out var handed).ShouldBeTrue();
        result.ShouldNotBeNull()[0, 40, 50].ShouldBe(WorkbenchFixtures.Read(master)[0, 40, 50] + 1f, Tolerance);
        handed.ShouldNotBeNull();
        handed.Value.CenterDec.ShouldBe(Solution().CenterDec, 1e-9);
        handed.Value.CenterDec.ShouldNotBe(WorkbenchFixtures.Wcs().CenterDec);

        var second = await runner.RunAsync(folder, cancellationToken: ct);

        States(second).ShouldBe([StepState.Kept, StepState.Kept]);
        solver.Solves.ShouldBe(1, "a solve whose solution is in the store is not run again");
        add.Runs.ShouldBe(1);
        second.Images[0].Steps[0].Output.ShouldBe(finding);

        // A changed setting of the solve is another solve, and another step after it.
        var read = await WorkbenchProjectFile.ReadAsync(folder, ct);
        var project = read.Project.ShouldNotBeNull(read.Refusal);
        var picture = project.Images[0];
        await WorkbenchProjectFile.WriteAsync(
            folder,
            project with { Images = [picture with { Edits = picture.Edits.SetItem(0, picture.Edits[0] with { Settings = new JsonObject { ["range"] = 0.05 } }) }] },
            ct);

        var third = await runner.RunAsync(folder, cancellationToken: ct);

        States(third).ShouldBe([StepState.Ran, StepState.Ran]);
        solver.Solves.ShouldBe(2);
        solver.Asked[1].Range.ShouldBe(0.05f);
        third.Images[0].Steps[0].ChangedSinceLastRun.ShouldBeTrue();
        third.Images[0].Steps[1].Key.ShouldNotBe(first.Images[0].Steps[1].Key, "the step after a solve is keyed on the solve");

        // The solution is kept on its own: with the step after it gone from the store, only that step runs.
        File.Delete(third.Images[0].Steps[1].Output.ShouldNotBeNull());

        var fourth = await runner.RunAsync(folder, cancellationToken: ct);

        States(fourth).ShouldBe([StepState.Kept, StepState.Ran]);
        solver.Solves.ShouldBe(2);
    }

    [Fact(Timeout = 120_000)]
    public async Task ASolveThatFindsNothingIsARefusalThatKeepsNothingAndIsTriedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("unsolved-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", master,
            WorkbenchFixtures.Edit(SolveOperation.Id), WorkbenchFixtures.Edit("add"))), ct);
        var solver = new FixedPlateSolver(null);
        var runner = new WorkbenchRunner(new OperationCatalog([new SolveOperation(solver), new AddOperation("add")]));

        var none = await runner.RunAsync(folder, cancellationToken: ct);

        none.Succeeded.ShouldBeFalse();
        States(none).ShouldBe([StepState.Refused, StepState.Blocked]);
        none.Images[0].Steps[0].Message.ShouldNotBeNull().ShouldContain("no plate solution was found");
        none.Images[0].Steps[0].Output.ShouldBeNull();
        StoreFiles(folder).ShouldBeEmpty("a solve that found nothing keeps nothing");

        solver.Throws = "the index is missing";
        var failed = await runner.RunAsync(folder, cancellationToken: ct);

        States(failed).ShouldBe([StepState.Refused, StepState.Blocked]);
        failed.Images[0].Steps[0].Message.ShouldNotBeNull().ShouldContain("[solve] failed: the index is missing");
        solver.Solves.ShouldBe(2, "a solve that gave nothing is tried again");

        solver.Throws = null;
        solver.Solution = Solution();
        var solved = await runner.RunAsync(folder, cancellationToken: ct);

        solved.Succeeded.ShouldBeTrue(Messages(solved));
        States(solved).ShouldBe([StepState.Ran, StepState.Ran]);

        // A host with no plate solver has the operation, and its step says so when it runs.
        var other = _folders.Create("unserved-").FullName;
        var copy = WorkbenchFixtures.WriteMaster(other, "master", WorkbenchFixtures.Master(seed: 1));
        await WorkbenchProjectFile.WriteAsync(other, WorkbenchFixtures.Project(WorkbenchFixtures.Picture("m", copy, WorkbenchFixtures.Edit(SolveOperation.Id))), ct);

        var unserved = await new WorkbenchRunner(new OperationCatalog([new SolveOperation(null)])).RunAsync(other, cancellationToken: ct);

        States(unserved).ShouldBe([StepState.Refused]);
        unserved.Images[0].Steps[0].Message.ShouldNotBeNull().ShouldContain("no plate solver");
    }

    [Fact]
    public void TheSettingsRefuseWhatIsNotASolveAndTheCommandLineSaysThemAll()
    {
        var solve = new SolveOperation(null);

        solve.Refusal(new SolveSettings()).ShouldBeNull();
        solve.Refusal(new SolveSettings { Scale = 0 }).ShouldNotBeNull().ShouldContain("scale");
        solve.Refusal(new SolveSettings { Range = 0f }).ShouldNotBeNull().ShouldContain("range");
        solve.Refusal(new SolveSettings { SearchRa = 5.5 }).ShouldNotBeNull().ShouldContain("searchRa and searchDec");
        solve.Refusal(new SolveSettings { SearchRa = 25, SearchDec = 0 }).ShouldNotBeNull().ShouldContain("0 to 24");
        solve.Refusal(new SolveSettings { SearchRa = 5, SearchDec = -91 }).ShouldNotBeNull().ShouldContain("-90 to 90");
        solve.Refusal(new SolveSettings { SearchRadius = 0 }).ShouldNotBeNull().ShouldContain("searchRadius");

        solve.CommandLine(new SolveSettings(), ["in.fits"], "out.fits").ToArray()
            .ShouldBe([StepCommandLine.Program, "solve", "in.fits", "-o", "out.fits", "--range", "0.03"]);
        solve.CommandLine(new SolveSettings { Scale = 1.9, Range = 0.05f, SearchRa = 5.588, SearchDec = -5.39, SearchRadius = 3 }, ["in.fits"], "out.fits").ToArray()
            .ShouldBe(
            [
                StepCommandLine.Program, "solve", "in.fits", "-o", "out.fits", "--scale", "1.9", "--range", "0.05",
                "--search-origin", "5.588,-5.39", "--search-radius", "3",
            ]);

        solve.Description.Output.ShouldBe(OperationOutputKind.Finding);
        IOperation asOperation = solve;
        asOperation.ServedBy(solve.DefaultSettings).ShouldBeEmpty("which solver answers is not part of a solve's key");

        // The catalog holds an operation's kind to its type, for this kind as for a derived one.
        Should.Throw<ArgumentException>(() => new OperationCatalog([new SaysFindingOperation()])).Message.ShouldContain("'says-finding'");
        new OperationCatalog([solve]).TryGet(SolveOperation.Id, out var found).ShouldBeTrue();
        found.ShouldBeAssignableTo<IFindingOperation>();
    }

    [Fact]
    public void ASolutionKeptAsAFileIsWhatAnImageWrittenWithItGivesItsReader()
    {
        var folder = _folders.Create("solution-").FullName;
        var path = Path.Combine(folder, "solution.fits");
        var sip = new double[3, 3];
        sip[2, 0] = 1.25e-7;
        sip[1, 1] = -3.5e-8;
        sip[0, 2] = 2.125e-7;
        var solution = Solution() with { SipOrder = 2, SipA = sip, SipB = sip };

        PlateSolutionFile.Write(path, solution);
        var read = PlateSolutionFile.Read(path);

        read.ShouldNotBeNull();
        read.Value.HasSip.ShouldBeTrue("the distortion terms are kept with the solution");
        // The same cards through the same reader: the numbers are the image's, to the digit.
        var image = Path.Combine(folder, "image.fits");
        WorkbenchFixtures.Master(seed: 1).WriteToFitsFile(image, solution);
        Image.TryReadFitsFile(image, out _, out var fromImage).ShouldBeTrue();
        fromImage.ShouldNotBeNull();
        read.Value.CenterRA.ShouldBe(fromImage.Value.CenterRA);
        read.Value.CenterDec.ShouldBe(fromImage.Value.CenterDec);
        read.Value.CRPix1.ShouldBe(fromImage.Value.CRPix1);
        read.Value.CRPix2.ShouldBe(fromImage.Value.CRPix2);
        read.Value.CD1_1.ShouldBe(fromImage.Value.CD1_1);
        read.Value.CD1_2.ShouldBe(fromImage.Value.CD1_2);
        read.Value.CD2_1.ShouldBe(fromImage.Value.CD2_1);
        read.Value.CD2_2.ShouldBe(fromImage.Value.CD2_2);
        read.Value.PixelToSky(10.0, 20.0).ShouldBe(fromImage.Value.PixelToSky(10.0, 20.0));

        // Held once, a solution is the one the file gives, and holding it again changes nothing: what is written from
        // it is written the same way ever after.
        var held = PlateSolutionFile.AsHeld(solution);
        held.CenterRA.ShouldBe(read.Value.CenterRA);
        held.CRPix1.ShouldBe(read.Value.CRPix1);
        held.CD1_2.ShouldBe(read.Value.CD1_2);
        held.CD2_1.ShouldBe(read.Value.CD2_1);
        var heldAgain = PlateSolutionFile.AsHeld(held);
        (heldAgain.CenterRA, heldAgain.CenterDec, heldAgain.CRPix1, heldAgain.CRPix2, heldAgain.CD1_1, heldAgain.CD1_2, heldAgain.CD2_1, heldAgain.CD2_2)
            .ShouldBe((held.CenterRA, held.CenterDec, held.CRPix1, held.CRPix2, held.CD1_1, held.CD1_2, held.CD2_1, held.CD2_2));
        held.CD2_1.ShouldBe(solution.CD2_1, 1e-16, "no digit a card keeps is changed");

        PlateSolutionFile.Read(Path.Combine(folder, "absent.fits")).ShouldBeNull("a file that is not there holds no solution");
    }

    /// <summary>
    /// The plan's rule 3 for a step that keeps no image: its command line, run through the real <c>tianwen solve</c>,
    /// writes the file the step's export writes, to the byte; and the step after it, which takes the solution from the
    /// store, is to the byte the file its own verb writes from the solve verb's file.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheSolveStepIsTheFileSolveWritesAndTheStepAfterItIsItsVerbsFileFromThat()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("parity-solve-").FullName;
        var master = WorkbenchFixtures.WriteMaster(folder, "master", WorkbenchFixtures.Master(seed: 1));
        var hint = new JsonObject { ["scale"] = 1.9, ["range"] = 0.05, ["searchRa"] = 5.588, ["searchDec"] = -5.39, ["searchRadius"] = 3 };
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("blind", master, WorkbenchFixtures.Edit(SolveOperation.Id)),
            WorkbenchFixtures.Picture("hinted", master, WorkbenchFixtures.Edit(SolveOperation.Id, hint)),
            WorkbenchFixtures.Picture("flat", master,
                WorkbenchFixtures.Edit(SolveOperation.Id, new JsonObject { ["range"] = 0.04 }),
                WorkbenchFixtures.Edit(GradientOperation.Id, new JsonObject { ["backend"] = "classical" }))), ct);
        var solver = new FixedPlateSolver(Solution());
        var runner = new WorkbenchRunner(new OperationCatalog(
        [
            new SolveOperation(solver),
            new GradientOperation(new ClassicalBackgroundExtractor(), Substitute.For<IGradientCorrector>()),
        ]));

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        report.Succeeded.ShouldBeTrue(Messages(report));
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        var solve = new SolveSubCommand(host, solver, Substitute.For<ICelestialObjectDB>()).Build();
        var imageCommand = new ImageSubCommand(
            host,
            new SharpenPipeline(),
            Substitute.For<IStarRemover>(),
            Substitute.For<IGradientCorrector>(),
            new ClassicalBackgroundExtractor(),
            new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance)).Build();

        // Every solve step's command line through the real verb, each into a file of its own.
        var verbs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var each in report.Images)
        {
            var step = each.Steps[0];
            var line = step.CommandLine.ToArray();
            line[..2].ShouldBe([StepCommandLine.Program, "solve"]);
            var at = Array.IndexOf(line, "-o") + 1;
            var named = line[at];
            Path.GetFileName(named).ShouldBe(StepStore.FileNameOf(step.Key), "the line names where the solved image would be in the store");
            File.Exists(named).ShouldBeFalse("and the store keeps no solved image");
            var written = Path.Combine(folder, each.Name + "-solve-verb.fits");
            line[at] = written;
            output.WriteLine(StepCommandLine.Display(line));
            said.Clear();

            var exit = await solve.Parse(line[2..]).InvokeAsync(cancellationToken: ct);

            exit.ShouldBe(0, string.Join(Environment.NewLine, said));
            verbs[named] = written;
        }

        // Each verb asked its solver what its step asked.
        solver.Asked.Count.ShouldBe(6, "three steps and three verbs");
        solver.Asked.Skip(3).ShouldBe(solver.Asked.Take(3));
        solver.Asked[0].Origin.ShouldBeNull("a solve with no place to look is blind");
        solver.Asked[0].Radius.ShouldBeNull();
        solver.Asked[1].Dim.ShouldBe((ImageDim?)new ImageDim(1.9, WorkbenchFixtures.W, WorkbenchFixtures.H));
        solver.Asked[1].Range.ShouldBe(0.05f);
        solver.Asked[1].Radius.ShouldBe((double?)3.0);

        // A solve keeps no image. Written when asked for, from the solution the run kept, it is the verb's file.
        foreach (var name in new[] { "blind", "hinted" })
        {
            var exported = await runner.RunAsync(folder, new WorkbenchRunOptions { Image = name, ExportPath = Path.Combine(folder, name + "-exported.fits") }, ct);
            var step = exported.Images.Single(each => each.Name == name).Steps[0];
            step.State.ShouldBe(StepState.Kept);
            ShouldBeTheSameFile(
                exported.Exported.ShouldNotBeNull(exported.Refusal),
                verbs[step.CommandLine[step.CommandLine.IndexOf("-o") + 1]],
                $"the exported solve of {name}");
        }

        solver.Asked.Count.ShouldBe(6, "an export solves nothing again");

        // The step after a solve took the solution from the store; its verb takes it off the image the solve verb wrote.
        var flat = report.Images.Single(static each => each.Name == "flat").Steps[1];
        var flatLine = flat.CommandLine.ToArray();
        flatLine[..3].ShouldBe([StepCommandLine.Program, "image", "flatten"]);
        var taken = 0;
        for (var i = 3; i < flatLine.Length; i++)
        {
            if (verbs.TryGetValue(flatLine[i], out var solvedByVerb))
            {
                flatLine[i] = solvedByVerb;
                taken++;
            }
        }

        taken.ShouldBe(1, "the line takes the solved image by the place the solve's line names");
        var flatWritten = Path.Combine(folder, "flat-verb.fits");
        flatLine[Array.IndexOf(flatLine, "-o") + 1] = flatWritten;
        output.WriteLine(StepCommandLine.Display(flatLine));
        said.Clear();

        var flatExit = await imageCommand.Parse(flatLine[2..]).InvokeAsync(cancellationToken: ct);

        flatExit.ShouldBe(0, string.Join(Environment.NewLine, said));
        ShouldBeTheSameFile(flat.Output.ShouldNotBeNull(), flatWritten, "the gradient fit after a solve");
        Image.TryReadFitsFile(flatWritten, out _, out var carried).ShouldBeTrue();
        carried.ShouldNotBeNull();
        carried.Value.CenterDec.ShouldBe(Solution().CenterDec, 1e-9, "the solution went on with the pixels");
    }
}
