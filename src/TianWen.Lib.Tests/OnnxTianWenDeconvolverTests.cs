using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// TianWen's own deconvolver (E3.4d, #844) through its whole-frame runner: the kernel it builds, the graph it binds by
/// name, the runtime against the Python reference (<c>training/denoise/n2n_operator_runtime.py</c>) on the fixture graph
/// and on the shipped weights, and the rule that it serves only <c>--ai-backend tianwen</c> with a stated kernel (the
/// owner's call, 2026-10-09; Auto waits on #741).
/// </summary>
[Collection("Imaging")]
public class OnnxTianWenDeconvolverTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolders _temp = new();

    public void Dispose() => _temp.Dispose();

    private const string FixtureGraph = "tianwen_deconv_operator_fixture.onnx";

    private static readonly OperatorGraphNames GraphNames = new("image", "kernel", "stretch_min", "stretch_balance", "output");

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Data", "Deconv");

    private static readonly DeconvolutionKernel AKernel = new(0.77, 0.91, 0.98);

    private static EnhanceOptions Asking(DeconvolutionKernel? kernel, EnhanceBackend backend = EnhanceBackend.TianWen)
        => new EnhanceOptions(backend, new EnhanceTuning(Deconvolution: kernel));

    /// <summary>The fixture graph and its contract copied into a folder of their own, so a resolver over it finds only
    /// them; <c>false</c> where the graph is an unmaterialised LFS pointer.</summary>
    private bool TryFixtureFolder(out string folder)
    {
        folder = _temp.Create("deconv-").FullName;
        File.Copy(Path.Combine(FixtureDir, FixtureGraph), Path.Combine(folder, FixtureGraph));
        File.Copy(Path.Combine(FixtureDir, ModelContract.ContractFileName(FixtureGraph)), Path.Combine(folder, ModelContract.ContractFileName(FixtureGraph)));
        return new ModelResolver([folder]).TryResolve(FixtureGraph, out _);
    }

    private static JsonElement ReadFixture(string name)
    {
        using var gz = new GZipStream(File.OpenRead(Path.Combine(FixtureDir, name)), CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(gz);
        return doc.RootElement.Clone();
    }

    private static float[] Floats(JsonElement element)
        => element.ValueKind == JsonValueKind.String
            ? MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(element.GetString() ?? "")).ToArray()
            : [.. element.EnumerateArray().Select(static v => v.GetSingle())];

    /// <summary>A [3, H, W] row-major array as a three-channel image.</summary>
    private static Image ImageOf(float[] chw, int height, int width)
    {
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[height, width];
            chw.AsSpan(c * height * width, height * width).CopyTo(MemoryMarshal.CreateSpan(ref planes[c][0, 0], height * width));
        }
        return new Image(planes, BitDepth.Float32, 1.0f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });
    }

    private static float MaxAbsDifference(Image image, float[] chw)
    {
        var (_, width, height) = image.Shape;
        var worst = 0f;
        for (var c = 0; c < 3; c++)
        {
            var plane = image.GetChannelSpan(c);
            var expected = chw.AsSpan(c * height * width, height * width);
            for (var i = 0; i < plane.Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(plane[i] - expected[i]));
            }
        }
        return worst;
    }

    [Fact]
    public void ItsKernelIsTheExportersKernel()
    {
        // n2n_operator_export.channel_kernels: per channel PsfKernel.Moffat's weights at the widest one's support.
        var io = ReadFixture("tianwen_deconv_operator_fixture_io.json.gz");
        var fwhm = io.GetProperty("kernel_fwhm_px").EnumerateArray().Select(static v => v.GetDouble()).ToArray();
        var shape = io.GetProperty("kernel_shape").EnumerateArray().Select(static v => v.GetInt32()).ToArray();
        var expected = Floats(io.GetProperty("kernel"));

        var tensor = OperatorDeconvolutionRunner.KernelTensor(
            new DeconvolutionKernel(fwhm[0], fwhm[1], fwhm[2], io.GetProperty("kernel_beta").GetDouble()), scale: 1.0);

        tensor.Dimensions.ToArray().ShouldBe(shape);
        var worst = tensor.Buffer.ToArray().Zip(expected, static (a, b) => Math.Abs(a - b)).Max();
        output.WriteLine($"kernel {string.Join("x", shape)}: max |C# - Python| {worst:E2}");
        worst.ShouldBeLessThan(1e-7f);
    }

    [Fact]
    public void AZeroWidthIsTheDeltaAtTheOthersSize()
    {
        var tensor = OperatorDeconvolutionRunner.KernelTensor(new DeconvolutionKernel(0, 1.2, 0), scale: 1.0);

        var size = tensor.Dimensions[1];
        size.ShouldBeGreaterThan(1);
        var centre = size / 2;
        for (var c = 0; c < 3; c += 2)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    tensor[c, y, x].ShouldBe(y == centre && x == centre ? 1f : 0f);
                }
            }
        }
    }

    [Fact]
    public void TheGraphTakesItsInputsByNameAndMatchesTorch()
    {
        if (!TryFixtureFolder(out var folder))
        {
            Assert.Skip($"{FixtureGraph} is an unmaterialised LFS pointer; run 'git lfs pull'.");
            return;
        }
        var io = ReadFixture("tianwen_deconv_operator_fixture_io.json.gz");
        var imageShape = io.GetProperty("image_shape").EnumerateArray().Select(static v => v.GetInt32()).ToArray();
        var kernelShape = io.GetProperty("kernel_shape").EnumerateArray().Select(static v => v.GetInt32()).ToArray();

        using var session = new InferenceSession(Path.Combine(folder, FixtureGraph));
        using var results = session.Run(
        [
            NamedOnnxValue.CreateFromTensor("image", new DenseTensor<float>(Floats(io.GetProperty("image")), imageShape)),
            NamedOnnxValue.CreateFromTensor("kernel", new DenseTensor<float>(Floats(io.GetProperty("kernel")), kernelShape)),
            NamedOnnxValue.CreateFromTensor("stretch_min", new DenseTensor<float>(Floats(io.GetProperty("stretch_min")), [3])),
            NamedOnnxValue.CreateFromTensor("stretch_balance", new DenseTensor<float>(Floats(io.GetProperty("stretch_balance")), [3])),
        ]);
        var got = results[0].AsTensor<float>().ToArray();
        var expected = Floats(io.GetProperty("expected"));

        var worst = got.Zip(expected, static (a, b) => Math.Abs(a - b)).Max();
        output.WriteLine($"fixture graph against torch: max |diff| {worst:E2}");
        worst.ShouldBeLessThan(2e-6f);
    }

    /// <summary>
    /// The SHIPPED graph against torch's own readout call (#1401): the exporter's 64 px sample of the Statue master, which
    /// until now lived only beside the export, so CI compared the shipped weights with the Python runtime and never with
    /// torch. The exporter measured 2.4e-7 on it.
    /// </summary>
    [Fact]
    public void TheShippedGraphMatchesTorch()
    {
        if (!new ModelResolver().TryResolve(OnnxTianWenDeconvolver.ModelFileName, out var modelPath))
        {
            Assert.Skip($"{OnnxTianWenDeconvolver.ModelFileName} not found (or is an unmaterialised LFS pointer); run 'git lfs pull'.");
            return;
        }
        var io = ReadFixture("tianwen_deconv_operator_e34d_s0_io.json.gz");
        var imageShape = io.GetProperty("image_shape").EnumerateArray().Select(static v => v.GetInt32()).ToArray();
        var kernelShape = io.GetProperty("kernel_shape").EnumerateArray().Select(static v => v.GetInt32()).ToArray();

        using var session = new InferenceSession(modelPath);
        using var results = session.Run(
        [
            NamedOnnxValue.CreateFromTensor("image", new DenseTensor<float>(Floats(io.GetProperty("image")), imageShape)),
            NamedOnnxValue.CreateFromTensor("kernel", new DenseTensor<float>(Floats(io.GetProperty("kernel")), kernelShape)),
            NamedOnnxValue.CreateFromTensor("stretch_min", new DenseTensor<float>(Floats(io.GetProperty("stretch_min")), [3])),
            NamedOnnxValue.CreateFromTensor("stretch_balance", new DenseTensor<float>(Floats(io.GetProperty("stretch_balance")), [3])),
        ]);
        var got = results[0].AsTensor<float>().ToArray();
        var expected = Floats(io.GetProperty("expected"));

        got.Length.ShouldBe(expected.Length);
        var worst = got.Zip(expected, static (a, b) => Math.Abs(a - b)).Max();
        output.WriteLine($"shipped graph against torch: max |diff| {worst:E2} (the exporter read {io.GetProperty("onnx_max_abs").GetDouble():E2})");
        worst.ShouldBeLessThan(2e-6f);
    }

    [Fact]
    public void TheRunnerIsThePythonRuntimeOverSeveralTiles()
    {
        if (!TryFixtureFolder(out var folder))
        {
            Assert.Skip($"{FixtureGraph} is an unmaterialised LFS pointer; run 'git lfs pull'.");
            return;
        }
        var fixture = ReadFixture("operator_runtime_fixture.json.gz");
        var (height, width) = (fixture.GetProperty("height").GetInt32(), fixture.GetProperty("width").GetInt32());
        var fwhm = fixture.GetProperty("fwhm").EnumerateArray().Select(static v => v.GetDouble()).ToArray();
        var kernel = new DeconvolutionKernel(fwhm[0], fwhm[1], fwhm[2], fixture.GetProperty("beta").GetDouble(), fixture.GetProperty("resample").GetDouble());
        var frame = ImageOf(Floats(fixture.GetProperty("frame")), height, width);

        using var session = new InferenceSession(Path.Combine(folder, FixtureGraph));
        var result = OperatorDeconvolutionRunner.Run(frame, session, GraphNames, kernel, fixture.GetProperty("tile").GetInt32(),
            cancellationToken: TestContext.Current.CancellationToken);

        result.Tiles.ShouldBeGreaterThan(1, "the fixture is sized so the stitch is exercised");
        (result.ZoomedHeight, result.ZoomedWidth).ShouldBe((fixture.GetProperty("zoomed")[0].GetInt32(), fixture.GetProperty("zoomed")[1].GetInt32()));
        result.KernelSize.ShouldBe(fixture.GetProperty("kernel_size").GetInt32());
        result.StretchMin.ShouldBe(Floats(fixture.GetProperty("stretch_min")));
        var balances = fixture.GetProperty("stretch_balance").EnumerateArray().Select(static v => v.GetDouble()).ToArray();
        for (var c = 0; c < 3; c++)
        {
            result.Balances[c].ShouldBe(balances[c], 1e-12);
        }
        var worst = MaxAbsDifference(result.Output, Floats(fixture.GetProperty("expected")));
        output.WriteLine($"fixture graph, {result.Tiles} tiles of {result.TileEdge} px: max |C# - Python| {worst:E2} (the output moves up to {fixture.GetProperty("output_moved_max").GetDouble():F3})");
        worst.ShouldBeLessThan(1e-5f);
    }

    [Fact]
    public async Task TheShippedDeconvolverIsThePythonRuntime()
    {
        if (!new ModelResolver().TryResolve(OnnxTianWenDeconvolver.ModelFileName, out _))
        {
            Assert.Skip($"{OnnxTianWenDeconvolver.ModelFileName} not found (or is an unmaterialised LFS pointer); run 'git lfs pull'.");
            return;
        }
        var fixture = ReadFixture("operator_runtime_e34d.json.gz");
        var (height, width) = (fixture.GetProperty("height").GetInt32(), fixture.GetProperty("width").GetInt32());
        var fwhm = fixture.GetProperty("fwhm").EnumerateArray().Select(static v => v.GetDouble()).ToArray();
        var kernel = new DeconvolutionKernel(fwhm[0], fwhm[1], fwhm[2], fixture.GetProperty("beta").GetDouble(), fixture.GetProperty("resample").GetDouble());
        var frame = ImageOf(Floats(fixture.GetProperty("frame")), height, width);

        // Through the enhancer itself: the resolver, the contract and the binding by role, then the runner.
        using var deconvolver = new OnnxTianWenDeconvolver(new ModelResolver(), NullLogger<OnnxTianWenDeconvolver>.Instance, fixture.GetProperty("tile").GetInt32());
        var deconvolved = await deconvolver.EnhanceAsync(frame, Asking(kernel), null, TestContext.Current.CancellationToken);

        deconvolved.ShouldNotBeSameAs(frame);
        var worst = MaxAbsDifference(deconvolved, Floats(fixture.GetProperty("expected")));
        output.WriteLine($"shipped weights: max |C# - Python| {worst:E2} (the output moves up to {fixture.GetProperty("output_moved_max").GetDouble():F3})");
        worst.ShouldBeLessThan(1e-5f);
    }

    [Fact]
    public void TheShippedContractPassesThroughARealSession()
    {
        if (!new ModelResolver().TryResolve(OnnxTianWenDeconvolver.ModelFileName, out var modelPath))
        {
            Assert.Skip($"{OnnxTianWenDeconvolver.ModelFileName} not found (or is an unmaterialised LFS pointer); run 'git lfs pull'.");
            return;
        }

        using var session = new InferenceSession(modelPath);
        var contract = ModelContract.LoadBeside(modelPath);

        contract.Check(modelPath, OnnxTianWenDeconvolver.Feed, ModelGraph.From(session)).ShouldBeEmpty();
        contract.InputFor(ModelRoles.Kernel).ShouldNotBeNull().Rank.ShouldBe(3);
        contract.InputFor(ModelRoles.StretchMin).ShouldNotBeNull().Rank.ShouldBe(1);
        contract.InputFor(ModelRoles.StretchBalance).ShouldNotBeNull().Rank.ShouldBe(1);
    }

    /// <summary>The whole rule, in one place: three channels, <see cref="EnhanceBackend.TianWen"/>, a kernel, and weights
    /// with their contract beside them. Probes files only, so a stand-in is enough.</summary>
    [Theory]
    [InlineData(EnhanceBackend.TianWen, true, 3, true, true)]
    [InlineData(EnhanceBackend.Auto, true, 3, true, false)]          // Auto passes it over until #741
    [InlineData(EnhanceBackend.ForceRcAstro, true, 3, true, false)]
    [InlineData(EnhanceBackend.TianWen, false, 3, true, false)]      // no kernel stated: nothing to remove
    [InlineData(EnhanceBackend.TianWen, true, 1, true, false)]       // one-shot-colour only
    [InlineData(EnhanceBackend.TianWen, true, 3, false, false)]      // weights with no contract are refused at load
    public void ItServesOnlyARunThatNamesItAndStatesItsKernel(EnhanceBackend backend, bool kernelStated, int channels, bool contractOnDisk, bool expected)
    {
        var folder = _temp.Create("deconv-serve-").FullName;
        File.WriteAllText(Path.Combine(folder, OnnxTianWenDeconvolver.ModelFileName), "weights");
        if (contractOnDisk)
        {
            File.WriteAllText(Path.Combine(folder, ModelContract.ContractFileName(OnnxTianWenDeconvolver.ModelFileName)), "{}");
        }
        using var deconvolver = new OnnxTianWenDeconvolver(new ModelResolver([folder]));

        deconvolver.CanServe(channels, Asking(kernelStated ? AKernel : null, backend)).ShouldBe(expected);
    }

    [Fact]
    public async Task UnaskedItPassesTheFrameThroughAsTheSkippedDeblurItIs()
    {
        // No weights anywhere: a decline never loads anything.
        using var deconvolver = new OnnxTianWenDeconvolver(new ModelResolver([_temp.Create("deconv-none-").FullName]));
        var frame = ImageOf(new float[3 * 8 * 8], 8, 8);

        (await deconvolver.EnhanceAsync(frame, Asking(AKernel, EnhanceBackend.Auto), null, TestContext.Current.CancellationToken)).ShouldBeSameAs(frame);
        (await deconvolver.EnhanceAsync(frame, Asking(null), null, TestContext.Current.CancellationToken)).ShouldBeSameAs(frame);
        (await deconvolver.EnhanceAsync(frame, TestContext.Current.CancellationToken)).ShouldBeSameAs(frame);
    }

    /// <summary>
    /// A NaN never reaches the zoom (#1401). SplineZoom's prefilter is recursive along each row and then each column, so one
    /// NaN made the whole zoomed frame NaN; the graph's clip then turned it into a finite frame unrelated to the input, so
    /// a finiteness check alone passes against the bug. A frame with a NaN canvas ring and one interior NaN must come back
    /// as the same frame with a zero ring does (the ring both ways is absent), away from the ring and the hole; the ring
    /// comes back as it went in, and the hole, filled from its neighbours as the pipeline fills it, is finite.
    /// </summary>
    [Fact]
    public void ANanRingAndAnInteriorHoleDeconvolveAsAZeroRingDoes()
    {
        if (!TryFixtureFolder(out var folder))
        {
            Assert.Skip($"{FixtureGraph} is an unmaterialised LFS pointer; run 'git lfs pull'.");
            return;
        }
        var fixture = ReadFixture("operator_runtime_fixture.json.gz");
        var (height, width) = (fixture.GetProperty("height").GetInt32(), fixture.GetProperty("width").GetInt32());
        const int ring = 6;
        const int clear = 12;
        var (holeY, holeX) = (height / 2, width / 3);
        bool InRing(int y, int x) => y < ring || x < ring || y >= height - ring || x >= width - ring;
        float[] Ringed(float fill, bool withHole)
        {
            var chw = Floats(fixture.GetProperty("frame"));
            for (var c = 0; c < 3; c++)
            {
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        if (InRing(y, x))
                        {
                            chw[(c * height * width) + (y * width) + x] = fill;
                        }
                    }
                }
                if (withHole)
                {
                    chw[(c * height * width) + (holeY * width) + holeX] = float.NaN;
                }
            }
            return chw;
        }

        // Through the runner on a CPU session, as the rule lives there (DirectML's arithmetic on this graph moves an output by
        // up to 1.3e-3 for a one-ulp change of its input, which would be read here as the rule's).
        using var session = new InferenceSession(Path.Combine(folder, FixtureGraph));
        var tile = fixture.GetProperty("tile").GetInt32();
        var nanRinged = OperatorDeconvolutionRunner.Run(ImageOf(Ringed(float.NaN, withHole: true), height, width), session, GraphNames, AKernel, tile).Output;
        var zeroRinged = OperatorDeconvolutionRunner.Run(ImageOf(Ringed(0f, withHole: false), height, width), session, GraphNames, AKernel, tile).Output;

        var worst = 0f;
        for (var c = 0; c < 3; c++)
        {
            var nan = nanRinged.GetChannelSpan(c);
            var zero = zeroRinged.GetChannelSpan(c);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width) + x;
                    if (InRing(y, x))
                    {
                        float.IsNaN(nan[i]).ShouldBeTrue($"the ring comes back as it went in, channel {c} at ({x}, {y})");
                        continue;
                    }
                    float.IsFinite(nan[i]).ShouldBeTrue($"channel {c} at ({x}, {y})");
                    var nearRing = y < ring + clear || x < ring + clear || y >= height - ring - clear || x >= width - ring - clear;
                    var nearHole = Math.Abs(y - holeY) <= clear / 2 && Math.Abs(x - holeX) <= clear / 2;
                    if (!nearRing && !nearHole)
                    {
                        worst = Math.Max(worst, Math.Abs(nan[i] - zero[i]));
                    }
                }
            }
        }
        output.WriteLine($"largest interior difference from the zero ring: {worst}");
        worst.ShouldBeLessThan(0.05f);
    }

    /// <summary>
    /// A frame past 1 runs scaled under it, as training unit-scaled every frame by its own peak, and is scaled back after
    /// (#1401): before, the stretch clipped those pixels at 1 and they came back at 1 plus the stretch minimum. So a frame
    /// and the same frame times 1.4 come back the same but for that factor (the Python mirror reads 7.5e-7 between them;
    /// clipped, 0.40). Through the runner on a CPU session: DirectML on this graph's random prior moves an output by up to
    /// 1.3e-3 for a one-ulp change of its input, which is the GPU's arithmetic, not this rule.
    /// </summary>
    [Fact]
    public void AFramePastOneIsDeconvolvedAsTheSameFrameUnderIt()
    {
        if (!TryFixtureFolder(out var folder))
        {
            Assert.Skip($"{FixtureGraph} is an unmaterialised LFS pointer; run 'git lfs pull'.");
            return;
        }
        var fixture = ReadFixture("operator_runtime_fixture.json.gz");
        var (height, width) = (fixture.GetProperty("height").GetInt32(), fixture.GetProperty("width").GetInt32());
        var chw = Floats(fixture.GetProperty("frame"));
        var peak = chw.Max();
        for (var i = 0; i < chw.Length; i++)
        {
            chw[i] /= peak;
        }
        var underOne = ImageOf(chw, height, width);
        const float factor = 1.4f;
        var pastOne = underOne.Affine(factor, 0);
        pastOne.MaxValue.ShouldBe(factor);

        using var session = new InferenceSession(Path.Combine(folder, FixtureGraph));
        var tile = fixture.GetProperty("tile").GetInt32();
        var fromUnder = OperatorDeconvolutionRunner.Run(underOne, session, GraphNames, AKernel, tile).Output;
        var fromPast = OperatorDeconvolutionRunner.Run(pastOne, session, GraphNames, AKernel, tile).Output;

        var worst = 0f;
        for (var c = 0; c < 3; c++)
        {
            var under = fromUnder.GetChannelSpan(c);
            var past = fromPast.GetChannelSpan(c);
            for (var i = 0; i < under.Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(past[i] - (factor * under[i])));
            }
        }
        output.WriteLine($"largest difference from {factor} times the frame under 1: {worst}");
        worst.ShouldBeLessThan(1e-5f);
    }

    [Fact]
    public async Task TheCanvasRingComesBackExactlyAsItWentIn()
    {
        if (!TryFixtureFolder(out var folder))
        {
            Assert.Skip($"{FixtureGraph} is an unmaterialised LFS pointer; run 'git lfs pull'.");
            return;
        }
        var fixture = ReadFixture("operator_runtime_fixture.json.gz");
        var (height, width) = (fixture.GetProperty("height").GetInt32(), fixture.GetProperty("width").GetInt32());
        var chw = Floats(fixture.GetProperty("frame"));
        // A six-pixel ring of exact zero where no frame covered the canvas.
        const int ring = 6;
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (y < ring || x < ring || y >= height - ring || x >= width - ring)
                    {
                        chw[(c * height * width) + (y * width) + x] = 0f;
                    }
                }
            }
        }
        var frame = ImageOf(chw, height, width);

        using var deconvolver = new OnnxTianWenDeconvolver(FixtureGraph, new ModelResolver([folder]), fixture.GetProperty("tile").GetInt32());
        var deconvolved = await deconvolver.EnhanceAsync(frame, Asking(AKernel), null, TestContext.Current.CancellationToken);

        for (var c = 0; c < 3; c++)
        {
            var plane = deconvolved.GetChannelSpan(c);
            plane[0].ShouldBe(0f);
            plane[(ring - 1) * width + (width / 2)].ShouldBe(0f);
            plane[(height - 1) * width + width - 1].ShouldBe(0f);
            plane[(height / 2) * width + (width / 2)].ShouldNotBe(chw[(c * height * width) + (height / 2) * width + (width / 2)]);
        }
    }

    [Fact]
    public void TheTianWenAiLaneRegistersItAsTheDeblurAndAutoLeavesItOut()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddTianWenAi();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IImageDeblurrer>().ShouldBeOfType<OnnxTianWenDeconvolver>();
        var pipeline = provider.GetRequiredService<SharpenPipeline>();
        var frame = ImageOf(new float[3 * 8 * 8], 8, 8);

        pipeline.CapabilitiesFor(frame, EnhanceOptions.Default).Deblur.ShouldBeFalse();
        pipeline.CapabilitiesFor(frame, Asking(AKernel, EnhanceBackend.Auto)).Deblur.ShouldBeFalse();
        // Asked for, it serves wherever its weights are; a checkout without them leaves it out the same way.
        pipeline.CapabilitiesFor(frame, Asking(AKernel)).Deblur
            .ShouldBe(new ModelResolver().TryResolve(OnnxTianWenDeconvolver.ModelFileName, out _));
    }

    [Fact]
    public void TheGraphsTileFollowsTheResampleAsTheMasterRunnersDid()
    {
        OperatorDeconvolutionRunner.TileFor(DeconvolutionKernel.DefaultResample).ShouldBe(1312);
        OperatorDeconvolutionRunner.TileFor(1.0).ShouldBe(1024);
        (OperatorDeconvolutionRunner.TileFor(1.42) % OperatorDeconvolutionRunner.TileMultiple).ShouldBe(0);
    }
}
