using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NSubstitute;
using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Workbench;
using Xunit;
using Scaling = TianWen.Lib.Tests.WorkbenchEnhanceParityTests.Scaling;

namespace TianWen.Lib.Tests;

/// <summary>
/// The enhance laid out as a chain of the single steps (<see cref="EnhanceChain"/>) gives the one-step enhance's picture:
/// both run through one project, on one set of stand-in enhancers, and the chain's last result is the enhance's, pixel
/// for pixel. This is the enhance retrofit's test (the owner's choices, 2026-11-26): one routine per step.
/// </summary>
[Collection("Imaging")]
public sealed class EnhanceChainTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    [Fact(Timeout = 600_000)]
    public async Task TheChainOfSingleStepsIsTheOneStepEnhance()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("enhance-chain-").FullName;
        var masterPath = Path.Combine(folder, "master.fits");
        var master = WorkbenchFixtures.Master(seed: 1);
        master.WriteToFitsFile(masterPath);
        // One set of enhancers for the enhance and for the steps: the deblur, the star removal and the denoise are then one
        // routine run on one model each, and so is the gradient (the classical fit serving as the corrector).
        var (deblurrer, remover, denoiser) = (new Scaling("Test/Deblurrer", 0.95f), new Scaling("Test/Remover", 0.5f), new Scaling("Test/Denoiser", 0.8f));
        var gradient = new ClassicalBackgroundExtractor();
        var pipeline = new SharpenPipeline(starRemover: remover, denoiser: denoiser, gradientCorrector: gradient, deblurrer: deblurrer);
        var enhance = new EnhanceOperation(pipeline);
        var settings = new EnhanceSettings();
        EnhanceOperation.TryOptions(settings, out var options, out var refusal).ShouldBeTrue(refusal);
        var program = await enhance.ProgramAsync(enhance.Start(master, options), settings, stretchedInPipeline: false, recombine: true, output.WriteLine, ct);

        var (chain, why) = EnhanceChain.ImagesFor(program, settings, master, "chain", new ProjectImage { Path = masterPath });

        why.ShouldBeNull();
        chain.Select(static image => image.Name).ShouldBe(["chain-starless", "chain-stars", "chain"]);
        chain[0].Edits.Select(static edit => edit.Operation).ShouldBe(
            [DeblurOperation.Id, GradientOperation.Id, RemoveStarsOperation.Id, DenoiseOperation.Id],
            "the enhance's plate steps, read off its program, each the single step that runs the same routine");
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            [WorkbenchFixtures.Picture("enhanced", masterPath, WorkbenchFixtures.Edit(EnhanceOperation.Id)), .. chain]), ct);
        var runner = new WorkbenchRunner(new OperationCatalog(
        [
            enhance,
            new DeblurOperation(deblurrer),
            new GradientOperation(gradient, gradient),
            new RemoveStarsOperation(remover),
            new DenoiseOperation(denoiser),
            new ScnrOperation(),
            new WhiteBalanceOperation(),
            new AddStarsOperation(),
        ]));

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        report.Succeeded.ShouldBeTrue(string.Join("; ", report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));
        Image.TryReadFitsFile(Path.Combine(folder, ResultOf(report, "enhanced")), out var oneStep).ShouldBeTrue();
        Image.TryReadFitsFile(Path.Combine(folder, ResultOf(report, "chain")), out var chained).ShouldBeTrue();
        oneStep.ShouldNotBeNull();
        chained.ShouldNotBeNull();
        chained.Shape.ShouldBe(oneStep.Shape);
        var a = oneStep.GetChannelSpan(0);
        var b = chained.GetChannelSpan(0);
        var worst = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            worst = MathF.Max(worst, MathF.Abs(a[i] - b[i]));
        }

        output.WriteLine($"largest difference {worst:E2} on a peak of {oneStep.MaxValue:F4}");
        worst.ShouldBeLessThanOrEqualTo(1e-6f * oneStep.MaxValue, "the chain of single steps is the one-step enhance, pixel for pixel");
    }

    /// <summary>
    /// The colour half: a ringed OSC master that states a broadband SPCC balance not yet in its pixels, so the program
    /// puts the balance in first, levels the sky after the gradient and runs SCNR on the balanced stars. The chain's
    /// white-balance, gradient (with its sky level) and SCNR steps are then held to the enhance, away from the ring, and the
    /// ring is absence in both.
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task TheColourChainWithItsBalanceSkyLevelAndScnrIsTheOneStepEnhance()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("enhance-chain-colour-").FullName;
        var masterPath = Path.Combine(folder, "master.fits");
        const int Ring = 6;
        var master = RingedColourStatingABalance(Ring);
        master.WriteToFitsFile(masterPath);
        var (deblurrer, remover, denoiser) = (new Scaling("Test/Deblurrer", 0.95f), new Scaling("Test/Remover", 0.5f), new Scaling("Test/Denoiser", 0.8f));
        var gradient = new ClassicalBackgroundExtractor();
        var enhance = new EnhanceOperation(new SharpenPipeline(starRemover: remover, denoiser: denoiser, gradientCorrector: gradient, deblurrer: deblurrer));
        var settings = new EnhanceSettings();
        EnhanceOperation.TryOptions(settings, out var options, out var refusal).ShouldBeTrue(refusal);
        var program = await enhance.ProgramAsync(enhance.Start(master, options), settings, stretchedInPipeline: false, recombine: true, output.WriteLine, ct);

        var (chain, why) = EnhanceChain.ImagesFor(program, settings, master, "chain", new ProjectImage { Path = masterPath });

        why.ShouldBeNull();
        chain[0].Edits.Select(static edit => edit.Operation).ShouldBe(
            [WhiteBalanceOperation.Id, DeblurOperation.Id, GradientOperation.Id, RemoveStarsOperation.Id, DenoiseOperation.Id],
            "the balance the master states goes in first, as the enhance puts it in");
        chain[1].Edits.Select(static edit => edit.Operation).ShouldBe([ScnrOperation.Id], "SCNR on the stars, which the balance made neutral");
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            [WorkbenchFixtures.Picture("enhanced", masterPath, WorkbenchFixtures.Edit(EnhanceOperation.Id)), .. chain]), ct);
        var runner = new WorkbenchRunner(new OperationCatalog(
        [
            enhance, new WhiteBalanceOperation(), new DeblurOperation(deblurrer), new GradientOperation(gradient, gradient),
            new RemoveStarsOperation(remover), new DenoiseOperation(denoiser), new ScnrOperation(), new AddStarsOperation(),
        ]));

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        report.Succeeded.ShouldBeTrue(string.Join("; ", report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));
        Image.TryReadFitsFile(Path.Combine(folder, ResultOf(report, "enhanced")), out var oneStep).ShouldBeTrue();
        Image.TryReadFitsFile(Path.Combine(folder, ResultOf(report, "chain")), out var chained).ShouldBeTrue();
        oneStep.ShouldNotBeNull();
        chained.ShouldNotBeNull();
        chained.Shape.ShouldBe(oneStep.Shape);
        var (channels, width, height) = oneStep.Shape;
        var worst = 0f;
        var ringPresent = 0;
        for (var c = 0; c < channels; c++)
        {
            var a = oneStep.GetChannelSpan(c);
            var b = chained.GetChannelSpan(c);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var at = (y * width) + x;
                    if (x < Ring || y < Ring || x >= width - Ring || y >= height - Ring)
                    {
                        // The ring is absence in both: zero in the enhance's, NaN or zero in the chain's, never a level.
                        ringPresent += (a[at] is 0f && (b[at] is 0f || float.IsNaN(b[at]))) ? 0 : 1;
                        continue;
                    }

                    worst = MathF.Max(worst, MathF.Abs(a[at] - b[at]));
                }
            }
        }

        output.WriteLine($"largest difference {worst:E2} on a peak of {oneStep.MaxValue:F4}; ring pixels not absent: {ringPresent}");
        ringPresent.ShouldBe(0, "the canvas ring stays absence through every step of the chain, as through the enhance");
        worst.ShouldBeLessThanOrEqualTo(1e-5f * oneStep.MaxValue, "the colour chain is the one-step enhance, pixel for pixel, away from the ring");
    }

    // Three planes of the workbench master as one OSC colour master with a ring, through a broadband filter the curves know,
    // stating an SPCC balance that is not in its pixels yet, as a stacked master states its own.
    private static Image RingedColourStatingABalance(int ring)
    {
        var (red, green, blue) = (WorkbenchFixtures.Master(seed: 1, ring: ring), WorkbenchFixtures.Master(seed: 2, gain: 1.4f, ring: ring),
            WorkbenchFixtures.Master(seed: 3, gain: 0.8f, ring: ring));
        return new Image([red.GetChannelArray(0), green.GetChannelArray(0), blue.GetChannelArray(0)], BitDepth.Float32,
            MathF.Max(red.MaxValue, MathF.Max(green.MaxValue, blue.MaxValue)), 0f, 0f,
            new ImageMeta
            {
                Instrument = "SVBONY SV605CC",
                SensorType = SensorType.Color,
                Filter = Filter.FromName("Optolong L-Quad Enhance"),
                ColourCalibration = new ColourCalibration(1.52f, 1f, 1.78f, ColourCalibrationSource.Spcc),
            });
    }

    [Fact]
    public void AProgramStepNoSingleStepRunsIsRefusedInWords()
    {
        var program = LinearEnhanceProgram.For(EnhanceCapabilities.AllRoles(deblur: false)) with { StellarSharpen = true };

        var (images, refusal) = EnhanceChain.ImagesFor(program, new EnhanceSettings(), WorkbenchFixtures.Master(seed: 1), "chain",
            new ProjectImage { Path = "master.fits" });

        images.ShouldBeEmpty();
        refusal.ShouldNotBeNull().ShouldContain(nameof(SharpenStarsStep));
        EnhanceChain.ImagesFor(LinearEnhanceProgram.For(EnhanceCapabilities.AllRoles(deblur: false)), new EnhanceSettings { DenoiseBlend = 0.5f },
            WorkbenchFixtures.Master(seed: 1), "chain", new ProjectImage { Path = "master.fits" }).Refusal.ShouldNotBeNull().ShouldContain("denoiseBlend");
    }

    /// <summary>
    /// <c>tianwen workbench chain</c> lays out an image whose one edit is an enhance, from the file its edits start from, and
    /// refuses in words an image it cannot lay out and a name the project already has.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheChainVerbAddsTheChainBesideTheImageAndRefusesWhatItCannotLayOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("enhance-chain-verb-").FullName;
        var masterPath = Path.Combine(folder, "master.fits");
        WorkbenchFixtures.Master(seed: 1).WriteToFitsFile(masterPath);
        var remover = new Scaling("Test/Remover", 0.5f);
        var catalog = new OperationCatalog(
        [
            new EnhanceOperation(new SharpenPipeline(starRemover: remover, denoiser: new Scaling("Test/Denoiser", 0.8f))),
            new RemoveStarsOperation(remover), new DenoiseOperation(new Scaling("Test/Denoiser", 0.8f)), new AddStarsOperation(),
            new CropOperation(),
        ]);
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("enhanced", masterPath, WorkbenchFixtures.Edit(EnhanceOperation.Id)),
            WorkbenchFixtures.Picture("cropped", masterPath, WorkbenchFixtures.Edit(CropOperation.Id, new JsonObject { ["width"] = 64, ["height"] = 64 }))), ct);
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        var command = new WorkbenchSubCommand(host, catalog, new WorkbenchRunner(catalog)).Build();

        (await command.Parse(["chain", folder, "enhanced"]).InvokeAsync(cancellationToken: ct)).ShouldBe(0, string.Join("\n", said));
        var laidOut = (await WorkbenchProjectFile.ReadAsync(folder, ct)).Project.ShouldNotBeNull();
        laidOut.Images.Select(static image => image.Name).ShouldBe(["enhanced", "cropped", "enhanced-chain-starless", "enhanced-chain-stars", "enhanced-chain"]);

        said.Clear();
        (await command.Parse(["chain", folder, "enhanced"]).InvokeAsync(cancellationToken: ct)).ShouldBe(1);
        said.ShouldContain(line => line.Contains("already has an image named"), "laid out twice under one name, the second is refused");
        said.Clear();
        (await command.Parse(["chain", folder, "cropped"]).InvokeAsync(cancellationToken: ct)).ShouldBe(1);
        said.ShouldContain(line => line.Contains("one edit is an enhance"), "an image whose edit is not an enhance is not laid out");
        said.Clear();
        (await command.Parse(["chain", folder, "nothing"]).InvokeAsync(cancellationToken: ct)).ShouldBe(1);
        said.ShouldContain(line => line.Contains("has no image named nothing"));
    }

    // The file the last step of the named image wrote, relative to the project's folder.
    private static string ResultOf(WorkbenchReport report, string image)
        => report.Images.Single(each => each.Name == image).Steps[^1].Output.ShouldNotBeNull($"{image} has a stored result");
}
