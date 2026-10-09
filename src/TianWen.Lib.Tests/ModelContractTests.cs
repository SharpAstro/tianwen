using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The model contract (#824): a TianWen model ships <c>&lt;stem&gt;.contract.json</c> beside its weights and the loader
/// refuses a model whose contract is absent, unreadable, or at odds with the weights, the graph or what the runner feeds.
/// The denoiser ran about 100x below its training band for two weeks because nothing stated the band, and the parity
/// fixture could not see it (it runs the same bytes through the same graph in both languages).
/// <para>
/// Most of this runs without the weights: the refusals are judged against a graph described by hand and a stand-in model
/// file, since what they pin is the contract's comparison and not ONNX Runtime. The tests that need the real file are
/// gated like the rest of the denoiser's (an LFS pointer stub skips them).
/// </para>
/// </summary>
[Collection("Imaging")]
public class ModelContractTests : IDisposable
{
    private readonly TempFolders _temp = new();

    public void Dispose() => _temp.Dispose();

    private static ModelResolver CreateResolver() => new ModelResolver();

    private const string SkipWithoutWeights =
        $"{N2nDenoiser.ModelFileName} not found (or is an unmaterialized LFS pointer); run 'git lfs pull' to enable this test.";

    private static readonly byte[] StandInWeights = Encoding.ASCII.GetBytes("not really an onnx file, only bytes to hash");

    /// <summary>The graph the shipped model declares (<c>n2n_export.py</c>'s <c>mapped</c> shape; ORT reports the
    /// dynamic batch axis as -1), described by hand so the refusals need no session.</summary>
    private static ModelGraph PlaneGraph() => new(
        [new ModelGraphTensor("image", [-1, 3, 256, 256]), new ModelGraphTensor("plane", [-1, 1, 256, 256])],
        ["output"]);

    private static ModelContract ShippedContract()
    {
        CreateResolver().TryResolve(ModelContract.ContractFileName(N2nDenoiser.ModelFileName), out var path)
            .ShouldBeTrue("the shipped contract must be copied beside the binaries like the weights are");
        return ModelContract.Parse(File.ReadAllBytes(path!), N2nDenoiser.ModelFileName, path!);
    }

    private static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The shipped contract re-bound to <paramref name="weights"/>, so a test that changes one other thing
    /// is refused for that thing alone.</summary>
    private static ModelContract Bound(byte[] weights) => ShippedContract() with { OnnxSha256 = Sha256Of(weights) };

    private static ModelContract WithInputs(ModelContract contract, Func<ModelContractInput, ModelContractInput> change)
        => contract with { Inputs = [.. contract.Inputs.Select(change)] };

    /// <summary>A model file called <see cref="N2nDenoiser.ModelFileName"/> holding <paramref name="weights"/>, in a
    /// folder of its own, with <paramref name="contract"/> serialised beside it when one is given. Returns the model's path.</summary>
    private string Place(byte[] weights, ModelContract? contract)
    {
        var folder = _temp.Create("contract-");
        var modelPath = Path.Combine(folder.FullName, N2nDenoiser.ModelFileName);
        File.WriteAllBytes(modelPath, weights);
        if (contract is not null)
        {
            WriteContract(modelPath, contract);
        }
        return modelPath;
    }

    private static void WriteContract(string modelPath, ModelContract contract)
        => File.WriteAllText(ModelContract.PathBeside(modelPath),
            JsonSerializer.Serialize(contract, ModelContractJsonContext.Default.ModelContract));

    /// <summary>The problems the denoiser's plane-conditioned feed and the shipped graph's interface find in
    /// <paramref name="contract"/> once it is written beside a model file holding <paramref name="weights"/> and read back.</summary>
    private ImmutableArray<string> CheckAgainstPlaneGraph(ModelContract contract, byte[] weights)
    {
        var modelPath = Place(weights, contract);
        return ModelContract.LoadBeside(modelPath).Check(modelPath, N2nDenoiser.Feed(planeConditioned: true), PlaneGraph());
    }

    private static Image TinyColourFrame() => new(
        [new float[16, 16], new float[16, 16], new float[16, 16]],
        BitDepth.Float32, 1.0f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });

    [Fact]
    public void TheShippedContractStatesWhatTheDenoiserFeedsItsGraph()
    {
        var contract = ShippedContract();

        contract.ContractVersion.ShouldBe(ModelContract.SupportedVersion);
        contract.Model.ShouldBe(N2nDenoiser.ModelFileName);
        contract.OnnxSha256.Length.ShouldBe(64);
        contract.Domain.ShouldBe(ModelDomain.MtfStretched);
        contract.StretchMedianTarget.ShouldBe(AiNafnetInputs.TargetMedian);
        contract.Output.ShouldBe("output");
        contract.Inputs.Length.ShouldBe(2);

        var image = contract.InputFor(ModelRoles.Image).ShouldNotBeNull();
        image.Name.ShouldBe("image");
        image.Channels.ShouldBe(3);
        image.Height.ShouldBe(256);
        image.Width.ShouldBe(256);

        var plane = contract.InputFor(ModelRoles.Plane).ShouldNotBeNull();
        plane.Name.ShouldBe("plane");
        plane.Channels.ShouldBe(1);
        plane.Height.ShouldBe(256);
        plane.Width.ShouldBe(256);
        plane.Scale.ShouldBe(StretchedNoise.PlaneScale);
    }

    [Fact]
    public void TheShippedContractAgreesWithTheRunnerAndTheGraphEverywhereButTheStandInWeights()
    {
        // The stand-in file is not the model, so the SHA-256 is the one thing that must differ; every other aspect (the
        // domain, the stretch target, the plane's units, the channel counts, the tile, the names) must pass.
        var problems = CheckAgainstPlaneGraph(ShippedContract(), StandInWeights);

        var only = problems.ShouldHaveSingleItem();
        only.ShouldContain("SHA-256");
    }

    [Fact]
    public void TheShippedModelPassesItsContractThroughARealSession()
    {
        if (!CreateResolver().TryResolve(N2nDenoiser.ModelFileName, out var modelPath))
        {
            Assert.Skip(SkipWithoutWeights);
            return;
        }

        using var session = new InferenceSession(modelPath);
        var graph = ModelGraph.From(session);

        // The hand-built graph the refusals use IS the shipped one.
        var inputs = graph.Inputs.OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => $"{t.Name}:{string.Join(",", t.Dimensions)}");
        string.Join(";", inputs).ShouldBe("image:-1,3,256,256;plane:-1,1,256,256");
        graph.Outputs.ShouldBe(["output"]);

        OnnxIoNames.IsImagePlusPlane(session).ShouldBeTrue();
        var contract = ModelContract.LoadBeside(modelPath);
        contract.Check(modelPath, N2nDenoiser.Feed(OnnxIoNames.IsImagePlusPlane(session)), graph).ShouldBeEmpty();
    }

    [Fact]
    public void AnAbsentContractIsRefusedNamingTheModelAndWhereItShouldBe()
    {
        var modelPath = Place(StandInWeights, contract: null);

        var refusal = Should.Throw<ModelContractException>(() => ModelContract.LoadBeside(modelPath));

        refusal.ModelFileName.ShouldBe(N2nDenoiser.ModelFileName);
        refusal.Message.ShouldContain(N2nDenoiser.ModelFileName);
        refusal.Message.ShouldContain("has no contract");
        refusal.Message.ShouldContain(ModelContract.ContractFileName(N2nDenoiser.ModelFileName));
    }

    [Fact]
    public void TheContractIsReadBesideTheResolvedFileAndNowhereElse()
    {
        // The weights in one folder, a perfectly good contract in another: a resolver would find both by name, and
        // pairing them would check the weights against a contract written for something else.
        var weightsOnly = Place(StandInWeights, contract: null);
        Place(StandInWeights, Bound(StandInWeights));

        var refusal = Should.Throw<ModelContractException>(() => ModelContract.LoadBeside(weightsOnly));

        refusal.Message.ShouldContain("has no contract");
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("null")]
    [InlineData("[]")]
    public void AContractThatIsNotAContractIsRefused(string text)
    {
        var refusal = Should.Throw<ModelContractException>(() =>
            ModelContract.Parse(Encoding.UTF8.GetBytes(text), N2nDenoiser.ModelFileName, "x.contract.json"));

        refusal.Message.ShouldContain(N2nDenoiser.ModelFileName);
    }

    [Fact]
    public void AMisspeltPropertyIsRefusedRatherThanIgnored()
    {
        var json = JsonSerializer.Serialize(ShippedContract(), ModelContractJsonContext.Default.ModelContract);
        var misspelt = "{\"stretchMedianTarjet\": 0.25," + json[1..];

        var refusal = Should.Throw<ModelContractException>(() =>
            ModelContract.Parse(Encoding.UTF8.GetBytes(misspelt), N2nDenoiser.ModelFileName, "x.contract.json"));

        refusal.Message.ShouldContain("stretchMedianTarjet");
    }

    [Fact]
    public void AContractMissingARequiredPropertyIsRefused()
    {
        const string noOutput = """
            { "contractVersion": 1, "model": "m.onnx", "onnxSha256": "00", "domain": "mtfStretched", "inputs": [] }
            """;

        var refusal = Should.Throw<ModelContractException>(() =>
            ModelContract.Parse(Encoding.UTF8.GetBytes(noOutput), "m.onnx", "m.contract.json"));

        refusal.Message.ShouldContain("output");
    }

    [Fact]
    public void AWrongChannelCountIsRefusedNamingTheInputAndBothCounts()
    {
        var contract = WithInputs(Bound(StandInWeights), i => i.Role == ModelRoles.Image ? i with { Channels = 1 } : i);

        var problems = CheckAgainstPlaneGraph(contract, StandInWeights);

        problems.ShouldContain(p => p.Contains("'image'") && p.Contains("3 channel(s)") && p.Contains("1 channel(s)"),
            "the runner feeds 3 channels and the contract says 1");
        problems.ShouldContain(p => p.Contains("'image'") && p.Contains("graph declares [?,3,256,256]"),
            "the graph declares 3 and the contract says 1");
    }

    [Fact]
    public void AnInputNameTheGraphDoesNotHaveIsRefused()
    {
        var contract = WithInputs(Bound(StandInWeights), i => i.Role == ModelRoles.Image ? i with { Name = "img" } : i);

        var problems = CheckAgainstPlaneGraph(contract, StandInWeights);

        problems.ShouldContain(p => p.Contains("'img'") && p.Contains("'image'") && p.Contains("'plane'"),
            "it names an input the graph does not have, and says what the graph has");
        problems.ShouldContain(p => p.Contains("takes an input 'image'") && p.Contains("does not describe"),
            "and the graph's own input is left undescribed");
    }

    [Fact]
    public void AnOutputNameTheGraphDoesNotHaveIsRefused()
    {
        var problems = CheckAgainstPlaneGraph(Bound(StandInWeights) with { Output = "denoised" }, StandInWeights);

        var only = problems.ShouldHaveSingleItem();
        only.ShouldContain("'denoised'");
    }

    [Fact]
    public void AGraphInputTheContractLeavesOutIsRefused()
    {
        var contract = Bound(StandInWeights) with
        {
            Inputs = [.. ShippedContract().Inputs.Where(i => i.Role == ModelRoles.Image)],
        };

        var problems = CheckAgainstPlaneGraph(contract, StandInWeights);

        problems.ShouldContain(p => p.Contains("runner feeds a 'plane' input and the contract describes none"));
        problems.ShouldContain(p => p.Contains("graph takes an input 'plane'"));
    }

    [Fact]
    public void ADomainTheRunnerDoesNotFeedIsRefused()
    {
        // The skew of 2026-09-02: the runner fed linear pixels to a graph trained on stretched ones. Said the other way
        // round it is the same fact, and it must stop the load.
        var contract = Bound(StandInWeights) with { Domain = ModelDomain.Linear, StretchMedianTarget = null };

        var problems = CheckAgainstPlaneGraph(contract, StandInWeights);

        problems.ShouldContain(p => p.Contains("feeds the graph MTF-stretched pixels") && p.Contains("trained on linear pixels"));
    }

    [Fact]
    public void AStretchTargetOtherThanTheRunnersIsRefused()
    {
        var problems = CheckAgainstPlaneGraph(Bound(StandInWeights) with { StretchMedianTarget = 0.5 }, StandInWeights);

        var only = problems.ShouldHaveSingleItem();
        only.ShouldContain("median of 0.25");
        only.ShouldContain("trained on 0.5");
    }

    [Fact]
    public void AStretchTargetTheContractDoesNotStateIsRefusedRatherThanTrusted()
    {
        var problems = CheckAgainstPlaneGraph(Bound(StandInWeights) with { StretchMedianTarget = null }, StandInWeights);

        var only = problems.ShouldHaveSingleItem();
        only.ShouldContain("does not state the median");
    }

    [Fact]
    public void APlaneInOtherUnitsThanTheRunnerBuildsIsRefused()
    {
        var contract = WithInputs(Bound(StandInWeights), i => i.Role == ModelRoles.Plane ? i with { Scale = 100.0 } : i);

        var only = CheckAgainstPlaneGraph(contract, StandInWeights).ShouldHaveSingleItem();

        only.ShouldContain("'plane'");
        only.ShouldContain($"units of {StretchedNoise.PlaneScale}");
    }

    [Fact]
    public void APlaneScaleTheContractDoesNotStateIsRefusedRatherThanTrusted()
    {
        var contract = WithInputs(Bound(StandInWeights), i => i.Role == ModelRoles.Plane ? i with { Scale = null } : i);

        var only = CheckAgainstPlaneGraph(contract, StandInWeights).ShouldHaveSingleItem();

        only.ShouldContain("does not state the units");
    }

    [Fact]
    public void WeightsOtherThanTheOnesTheContractDescribesAreRefused()
    {
        // A retrain dropped under the old file name: the contract is for the earlier bytes.
        var retrained = Encoding.ASCII.GetBytes("a retrained checkpoint under the same name");

        var only = CheckAgainstPlaneGraph(Bound(StandInWeights), retrained).ShouldHaveSingleItem();

        only.ShouldContain(Sha256Of(retrained));
        only.ShouldContain(Sha256Of(StandInWeights));
    }

    [Fact]
    public void AContractCopiedBesideAnotherModelIsRefused()
    {
        var contract = Bound(StandInWeights) with { Model = "tianwen_denoise_osc_e2wide_s2.onnx" };

        var only = CheckAgainstPlaneGraph(contract, StandInWeights).ShouldHaveSingleItem();

        only.ShouldContain("tianwen_denoise_osc_e2wide_s2.onnx");
        only.ShouldContain(N2nDenoiser.ModelFileName);
    }

    [Fact]
    public void AContractVersionThisBuildDoesNotReadIsRefused()
    {
        var contract = Bound(StandInWeights) with { ContractVersion = ModelContract.SupportedVersion + 1 };

        var only = CheckAgainstPlaneGraph(contract, StandInWeights).ShouldHaveSingleItem();

        only.ShouldContain("contract version");
    }

    [Fact]
    public void EveryProblemIsInTheRefusalAndTheRefusalNamesTheModelAndTheFile()
    {
        var modelPath = Place(StandInWeights, Bound(StandInWeights) with { Domain = ModelDomain.Linear, Output = "denoised" });
        var problems = ModelContract.LoadBeside(modelPath).Check(modelPath, N2nDenoiser.Feed(planeConditioned: true), PlaneGraph());

        var refusal = ModelContractException.Refused(N2nDenoiser.ModelFileName, ModelContract.PathBeside(modelPath), problems);

        problems.Length.ShouldBeGreaterThan(1);
        refusal.ModelFileName.ShouldBe(N2nDenoiser.ModelFileName);
        refusal.Message.ShouldContain(N2nDenoiser.ModelFileName);
        refusal.Message.ShouldContain(ModelContract.PathBeside(modelPath));
        foreach (var problem in problems)
        {
            refusal.Message.ShouldContain(problem);
        }
    }

    [Fact]
    public void AScalarConditionedGraphHasItsOwnFeedAndItsScalarInputIsCheckedAsOne()
    {
        // Graphs before E16 take the tile's scalar sigma in-graph and a scalar strength: image + strength, no plane.
        var scalarGraph = new ModelGraph(
            [new ModelGraphTensor("image", [-1, 3, 256, 256]), new ModelGraphTensor("strength", [-1, 1])],
            ["output"]);
        var contract = Bound(StandInWeights) with
        {
            Inputs =
            [
                new ModelContractInput { Name = "image", Role = ModelRoles.Image, Channels = 3, Height = 256, Width = 256 },
                new ModelContractInput { Name = "strength", Role = ModelRoles.Strength },
            ],
        };
        var modelPath = Place(StandInWeights, contract);
        var read = ModelContract.LoadBeside(modelPath);

        read.Check(modelPath, N2nDenoiser.Feed(planeConditioned: false), scalarGraph).ShouldBeEmpty();

        // The same contract against the plane kind is wrong on both sides.
        read.Check(modelPath, N2nDenoiser.Feed(planeConditioned: true), PlaneGraph())
            .ShouldContain(p => p.Contains("'plane'"));
    }

    [Fact]
    public async Task TheDenoiserRefusesToLoadWithoutItsContract()
    {
        if (!CreateResolver().TryResolve(N2nDenoiser.ModelFileName, out var shippedPath))
        {
            Assert.Skip(SkipWithoutWeights);
            return;
        }

        var folder = _temp.Create("contract-");
        File.Copy(shippedPath, Path.Combine(folder.FullName, N2nDenoiser.ModelFileName));
        using var denoiser = new N2nDenoiser(new ModelResolver([folder.FullName]));

        // It says so before anything runs, so the canonical program leaves the denoise out rather than failing on it.
        ((IEnhancerAvailability)denoiser).CanServe(3, EnhanceOptions.Default).ShouldBeFalse();

        var refusal = await Should.ThrowAsync<ModelContractException>(
            async () => await denoiser.EnhanceAsync(TinyColourFrame(), 1.0f, TestContext.Current.CancellationToken));

        refusal.ModelFileName.ShouldBe(N2nDenoiser.ModelFileName);
        refusal.Message.ShouldContain("has no contract");
    }

    [Fact]
    public void TheShippedInstallServesAColourFrame()
    {
        if (!CreateResolver().TryResolve(N2nDenoiser.ModelFileName, out _))
        {
            Assert.Skip(SkipWithoutWeights);
            return;
        }

        using var denoiser = new N2nDenoiser(CreateResolver());

        ((IEnhancerAvailability)denoiser).CanServe(3, EnhanceOptions.Default).ShouldBeTrue();
    }

    [Fact]
    public async Task TheDenoiserRefusesAContractThatDisagreesWithItsGraphAndKeepsNoSession()
    {
        if (!CreateResolver().TryResolve(N2nDenoiser.ModelFileName, out var shippedPath))
        {
            Assert.Skip(SkipWithoutWeights);
            return;
        }

        // The right weights and a contract that is right about everything but the image's channel count.
        var folder = _temp.Create("contract-");
        var modelPath = Path.Combine(folder.FullName, N2nDenoiser.ModelFileName);
        File.Copy(shippedPath, modelPath);
        WriteContract(modelPath, WithInputs(ShippedContract(), i => i.Role == ModelRoles.Image ? i with { Channels = 1 } : i));
        using var denoiser = new N2nDenoiser(new ModelResolver([folder.FullName]));

        var first = await Should.ThrowAsync<ModelContractException>(
            async () => await denoiser.EnhanceAsync(TinyColourFrame(), 1.0f, TestContext.Current.CancellationToken));
        first.Message.ShouldContain(N2nDenoiser.ModelFileName);
        first.Message.ShouldContain("1 channel(s)");

        // A refused session is not cached as a loaded one: the next use asks again and is refused again.
        await Should.ThrowAsync<ModelContractException>(
            async () => await denoiser.EnhanceAsync(TinyColourFrame(), 1.0f, TestContext.Current.CancellationToken));
    }
}
