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
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The planetary steps of a project (#2468), the AUTO stack first: a capture is an image's original, its master is the
/// step's result and its sharpened master a result beside it, and both are the files
/// <c>tianwen planetary stack --auto</c> writes.
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
    }
}
