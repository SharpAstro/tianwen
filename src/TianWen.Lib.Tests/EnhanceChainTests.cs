using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Shouldly;
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

    // The file the last step of the named image wrote, relative to the project's folder.
    private static string ResultOf(WorkbenchReport report, string image)
        => report.Images.Single(each => each.Name == image).Steps[^1].Output.ShouldNotBeNull($"{image} has a stored result");
}
