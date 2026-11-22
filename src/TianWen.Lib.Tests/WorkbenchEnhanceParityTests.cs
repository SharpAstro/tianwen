using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The enhance step of a project and <c>tianwen image sharpen</c> are one routine (#2466): the step's printed command
/// line, run through the real verb on the same pipeline, writes the step's file to the byte. Stand-in enhancers serve
/// the star removal and the denoise; the gradient is the classical fit.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchEnhanceParityTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    /// <summary>An enhancer that scales what it is handed: the same answer for the same input.</summary>
    private sealed class Scaling(string name, float factor) : IStarRemover, IDenoiseEnhancer
    {
        public string Name => name;

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            var (channels, width, height) = input.Shape;
            var planes = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var source = input.GetChannelSpan(c);
                var plane = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        plane[y, x] = source[(y * width) + x] * factor;
                    }
                }

                planes[c] = plane;
            }

            return Task.FromResult(new Image(planes, BitDepth.Float32, 1f, 0f, 0f, input.ImageMeta));
        }
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

    [Fact(Timeout = 600_000)]
    public async Task TheEnhanceStepIsTheFileImageSharpenWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("parity-enhance-").FullName;
        var master = Path.Combine(folder, "master.fits");
        new Image([SyntheticStarFieldRenderer.Render(256, 256, defocusSteps: 0, exposureSeconds: 10, skyBackground: 100, readNoise: 5,
            starCount: 80, seed: 5, noiseSeed: 1)], BitDepth.Float32, 65535f, 0f, 0f, new ImageMeta()).WriteToFitsFile(master);
        // One pipeline for the step and for the verb: a result is only the same file when the same enhancers made it.
        var pipeline = new SharpenPipeline(
            starRemover: new Scaling("Test/Remover", 0.5f), denoiser: new Scaling("Test/Denoiser", 0.8f), gradientCorrector: new ClassicalBackgroundExtractor());
        await WorkbenchProjectFile.WriteAsync(folder, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("plain", master, WorkbenchFixtures.Edit(EnhanceOperation.Id)),
            WorkbenchFixtures.Picture("pared", master, WorkbenchFixtures.Edit(EnhanceOperation.Id,
                new JsonObject { ["gradient"] = false, ["denoise"] = false, ["mode"] = "screen" })),
            WorkbenchFixtures.Picture("tuned", master, WorkbenchFixtures.Edit(EnhanceOperation.Id,
                new JsonObject { ["gradientBackend"] = "classical", ["denoiseBlend"] = 0.5, ["denoiseStrength"] = 0.3, ["denoiseIterations"] = 1 }))), ct);
        var runner = new WorkbenchRunner(new OperationCatalog([new EnhanceOperation(pipeline)]));

        var report = await runner.RunAsync(folder, new WorkbenchRunOptions { Say = output.WriteLine }, ct);

        report.Succeeded.ShouldBeTrue(string.Join("; ", report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));
        report.Images.Select(static image => image.Steps[0].Key).Distinct().Count().ShouldBe(3, "three settings are three steps");
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        var image = new ImageSubCommand(
            host,
            pipeline,
            Substitute.For<IStarRemover>(),
            Substitute.For<IGradientCorrector>(),
            Substitute.For<IBackgroundExtractor>(),
            new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance)).Build();
        foreach (var each in report.Images)
        {
            var step = each.Steps[0];
            var line = step.CommandLine.ToArray();
            line[..3].ShouldBe([StepCommandLine.Program, "image", "sharpen"]);
            var written = Path.Combine(folder, each.Name + "-verb.fits");
            line[Array.IndexOf(line, "-o") + 1] = written;
            output.WriteLine(StepCommandLine.Display(line));
            said.Clear();

            var exit = await image.Parse(line[2..]).InvokeAsync(cancellationToken: ct);

            exit.ShouldBe(0, string.Join(Environment.NewLine, said));
            ShouldBeTheSameFile(step.Output.ShouldNotBeNull(), written, each.Name);
        }

        // And the three are three images: a setting that changes nothing would make the comparison above empty.
        var plain = WorkbenchFixtures.Read(report.Images[0].Steps[0].Output.ShouldNotBeNull());
        var pared = WorkbenchFixtures.Read(report.Images[1].Steps[0].Output.ShouldNotBeNull());
        var tuned = WorkbenchFixtures.Read(report.Images[2].Steps[0].Output.ShouldNotBeNull());
        plain[0, 100, 100].ShouldNotBe(pared[0, 100, 100]);
        plain[0, 100, 100].ShouldNotBe(tuned[0, 100, 100]);
    }

    [Fact]
    public void TheSettingsMakeTheOptionsTheVerbMakesAndRefuseWhatTheVerbRefuses()
    {
        var enhance = new EnhanceOperation(new SharpenPipeline());

        EnhanceOperation.TryOptions(new EnhanceSettings(), out var plain, out _).ShouldBeTrue();
        plain.ShouldBe(EnhanceOptions.Default, "no override at all is no tuning, as EnhanceOptions.TryParse has it");
        EnhanceOperation.TryOptions(new EnhanceSettings { DenoiseStrength = 0.4f, GradientBackend = GradientBackend.Classical }, out var tuned, out _).ShouldBeTrue();
        tuned.Tuning.ShouldNotBeNull().DenoiseStrength.ShouldBe(0.4f);
        tuned.Gradient.ShouldBe(GradientBackend.Classical);

        enhance.Refusal(new EnhanceSettings()).ShouldBeNull();
        enhance.Refusal(new EnhanceSettings { Gradient = false, GradientBackend = GradientBackend.Classical }).ShouldNotBeNull().ShouldContain("one or the other");
        enhance.Refusal(new EnhanceSettings { WhiteBalance = [1f, 1f] }).ShouldNotBeNull().ShouldContain("three positive numbers");
        enhance.Refusal(new EnhanceSettings { WhiteBalance = [1f, 0f, 1f] }).ShouldNotBeNull().ShouldContain("three positive numbers");
        enhance.Refusal(new EnhanceSettings { DenoiseBlend = 1.5f }).ShouldNotBeNull().ShouldContain("0 to 1");
        enhance.Refusal(new EnhanceSettings { DeconvBeta = 2.5 }).ShouldNotBeNull("a beta with no kernel is the kernel parser's to refuse");

        // What serves is in the key: the pipeline names the enhancer of every role, and "none" where there is none.
        enhance.ServedBy(new EnhanceSettings()).ShouldBe("deblur none; gradient none; stars none; stellar none; deconvolve none; denoise none");
        new EnhanceOperation(new SharpenPipeline(denoiser: new Scaling("Test/Denoiser", 1f))).ServedBy(new EnhanceSettings()).ShouldContain("denoise Test/Denoiser");
    }
}
