using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TianWen.AI.Imaging.Onnx;

/// <summary>
/// The domain the tensors a graph is FED are in: what a runner has to put into them, which is not necessarily the
/// frame the host hands the runner (the denoiser takes a linear frame and feeds its graph the MTF-stretched one).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelDomain>))]
internal enum ModelDomain
{
    /// <summary>Linear, unit-scaled pixels as the host holds them.</summary>
    [JsonStringEnumMemberName("linear")]
    Linear,

    /// <summary>The exporter's whole-frame, per-channel MTF stretch to a median (<see cref="ModelContract.StretchMedianTarget"/>),
    /// the domain every training tile was stored in (<c>ChunkedNafnetRunner.ApplyInputStretch</c>).</summary>
    [JsonStringEnumMemberName("mtfStretched")]
    MtfStretched,
}

/// <summary>The roles a graph input can play, so a runner finds a tensor by what it is and never by a guessed name.</summary>
internal static class ModelRoles
{
    /// <summary>The picture (NCHW).</summary>
    public const string Image = "image";

    /// <summary>A per-pixel noise plane the picture is conditioned on, one channel (the E16 denoiser's).</summary>
    public const string Plane = "plane";

    /// <summary>A scalar strength dial (every denoiser before E16).</summary>
    public const string Strength = "strength";
}

/// <summary>One graph input as a contract describes it.</summary>
internal sealed record ModelContractInput
{
    /// <summary>The tensor's name in the ONNX graph.</summary>
    public required string Name { get; init; }

    /// <summary>What the tensor is (<see cref="ModelRoles"/>); the runner finds it by this.</summary>
    public required string Role { get; init; }

    /// <summary>The channel count of an NCHW input; absent for a scalar.</summary>
    public int? Channels { get; init; }

    /// <summary>The tile height the graph was trained on; absent where the graph leaves it open.</summary>
    public int? Height { get; init; }

    /// <summary>The tile width the graph was trained on; absent where the graph leaves it open.</summary>
    public int? Width { get; init; }

    /// <summary>The units of a conditioning tensor, as a multiple of the quantity it carries (the noise plane's
    /// <c>StretchedNoise.PlaneScale</c>); absent for a tensor with no such scale.</summary>
    public double? Scale { get; init; }
}

/// <summary>
/// What one TianWen ONNX model was trained on, stated in a sidecar beside it (<c>&lt;model stem&gt;.contract.json</c>) and
/// CHECKED when the model is loaded (#824): a model is fed only what its contract says it saw. The denoiser once ran
/// about 100x below its training band for two weeks because the runner handed it linear pixels where it had trained on
/// stretched ones, and no metric caught it; this is the check that does, at the one place the runner and the weights
/// meet.
/// </summary>
/// <remarks>
/// <para><b>The contract binds three things and each is checked.</b> The WEIGHTS (<see cref="OnnxSha256"/>, so a
/// retrained file under an old name, or a sidecar copied beside the wrong file, is refused); the GRAPH (every input and
/// the output named, with the channel count and tile the graph declares, so a re-export with another interface is
/// refused); and the RUNNER (<see cref="Domain"/>, <see cref="StretchMedianTarget"/> and each conditioning tensor's
/// <see cref="ModelContractInput.Scale"/>, compared with what the runner actually feeds, <see cref="ModelFeed"/>, so
/// editing a runner constant without re-exporting the model, or the reverse, fails at load rather than producing a
/// plausible picture).</para>
///
/// <para><b>A field the runner relies on must be stated.</b> Where the runner feeds a stretch target or a plane scale
/// and the contract is silent, that is a refusal, not a pass: a silent contract cannot say the model was trained on it.
/// A field the contract states and the runner does not use is not checked.</para>
///
/// <para><b>An absent sidecar is a refusal</b> (<see cref="LoadBeside"/>), never an unchecked pass: "every model is
/// checked" is the rule, and a model with no contract is exactly the unstated input a contract exists to rule out. The
/// sidecar is read from the directory of the RESOLVED model file, not from wherever the resolver would find a file of
/// that name, so weights from one directory are never paired with a contract from another.</para>
///
/// <para>The file is read with a source-generated context (the apps are AOT), strictly: an unknown or misspelt
/// property is a refusal, since a field that is silently ignored is a check that silently did not run.</para>
/// </remarks>
internal sealed record ModelContract
{
    /// <summary>The contract format this build reads.</summary>
    public const int SupportedVersion = 1;

    /// <summary>The sidecar's suffix: <c>tianwen_denoise_osc_convmapb_s2.onnx</c> has
    /// <c>tianwen_denoise_osc_convmapb_s2.contract.json</c> beside it.</summary>
    public const string FileSuffix = ".contract.json";

    /// <summary>The format of this file; a version this build does not read is refused.</summary>
    public required int ContractVersion { get; init; }

    /// <summary>The model file this is the contract of, with its extension. A retrain gets a new file name and a new
    /// contract, so a sidecar copied beside another file names the wrong one.</summary>
    public required string Model { get; init; }

    /// <summary>The SHA-256 of the model file, as lower-case hex: the weights this contract describes.</summary>
    public required string OnnxSha256 { get; init; }

    /// <summary>The domain the graph's input tensors are in.</summary>
    public required ModelDomain Domain { get; init; }

    /// <summary>For <see cref="ModelDomain.MtfStretched"/>, the per-channel median every training tile was stretched to
    /// (<c>AiNafnetInputs.TargetMedian</c>); the runner must stretch to the same one.</summary>
    public double? StretchMedianTarget { get; init; }

    /// <summary>Every input of the graph, each by name and role.</summary>
    public required ImmutableArray<ModelContractInput> Inputs { get; init; }

    /// <summary>The name of the graph's output tensor.</summary>
    public required string Output { get; init; }

    /// <summary>The input playing <paramref name="role"/>, or <c>null</c> where the contract describes none.</summary>
    public ModelContractInput? InputFor(string role)
        => Inputs.IsDefault ? null : Inputs.FirstOrDefault(i => string.Equals(i.Role, role, StringComparison.Ordinal));

    /// <summary>The sidecar's file name for <paramref name="modelFileName"/>.</summary>
    public static string ContractFileName(string modelFileName)
        => Path.GetFileNameWithoutExtension(modelFileName) + FileSuffix;

    /// <summary>The path of the sidecar that belongs beside the model file at <paramref name="modelPath"/>.</summary>
    public static string PathBeside(string modelPath)
        => Path.Combine(Path.GetDirectoryName(modelPath) ?? string.Empty, ContractFileName(Path.GetFileName(modelPath)));

    /// <summary>
    /// Reads the contract that sits beside the model file at <paramref name="modelPath"/>.
    /// </summary>
    /// <exception cref="ModelContractException">There is no sidecar, or it is not a contract this build can read.</exception>
    public static ModelContract LoadBeside(string modelPath)
    {
        var modelFile = Path.GetFileName(modelPath);
        var contractPath = PathBeside(modelPath);
        if (!File.Exists(contractPath))
        {
            throw ModelContractException.Absent(modelFile, contractPath);
        }
        return Parse(File.ReadAllBytes(contractPath), modelFile, contractPath);
    }

    /// <summary>Parses a contract from its UTF-8 bytes. <paramref name="modelFile"/> and <paramref name="contractPath"/>
    /// only name the model in the refusal.</summary>
    /// <exception cref="ModelContractException">The bytes are not a contract: not JSON, a required property missing, a
    /// property this build does not know, a value out of its type.</exception>
    public static ModelContract Parse(ReadOnlySpan<byte> utf8, string modelFile, string contractPath)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8, ModelContractJsonContext.Default.ModelContract)
                ?? throw ModelContractException.Refused(modelFile, contractPath, ["the file holds a JSON null, not a contract"]);
        }
        catch (JsonException e)
        {
            throw ModelContractException.Refused(modelFile, contractPath, [$"it cannot be read as a contract: {e.Message}"]);
        }
    }

    /// <summary>
    /// Every way this contract disagrees with the model file at <paramref name="modelPath"/>, what the runner feeds
    /// (<paramref name="feed"/>) and the graph the file declares (<paramref name="graph"/>); empty when it agrees. All
    /// the problems are returned, not the first, so one refusal says everything that is wrong.
    /// </summary>
    public ImmutableArray<string> Check(string modelPath, ModelFeed feed, ModelGraph graph)
    {
        var problems = ImmutableArray.CreateBuilder<string>();
        var modelFile = Path.GetFileName(modelPath);

        if (ContractVersion != SupportedVersion)
        {
            problems.Add($"its contract version is {ContractVersion} and this build reads {SupportedVersion}");
        }
        if (!string.Equals(Model, modelFile, StringComparison.Ordinal))
        {
            problems.Add($"it is the contract of '{Model}', not of '{modelFile}' (a retrain gets a new file name and a new contract)");
        }

        CheckWeights(modelPath, problems);
        CheckRunner(feed, problems);
        CheckGraph(graph, problems);
        return problems.ToImmutable();
    }

    private void CheckWeights(string modelPath, ImmutableArray<string>.Builder problems)
    {
        string actual;
        using (var stream = File.OpenRead(modelPath))
        {
            actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        if (!string.Equals(OnnxSha256, actual, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"the weights are not the ones it describes: the file's SHA-256 is {actual}, the contract says {OnnxSha256}");
        }
    }

    private void CheckRunner(ModelFeed feed, ImmutableArray<string>.Builder problems)
    {
        if (Domain != feed.Domain)
        {
            problems.Add($"the runner feeds the graph {DescribeDomain(feed.Domain)} but the graph was trained on {DescribeDomain(Domain)}");
        }
        if (feed.StretchMedianTarget is { } fedMedian)
        {
            if (StretchMedianTarget is not { } trainedMedian)
            {
                problems.Add($"the runner stretches to a per-channel median of {fedMedian} and the contract does not state the median the graph was trained on");
            }
            else if (!Near(fedMedian, trainedMedian))
            {
                problems.Add($"the runner stretches to a per-channel median of {fedMedian} and the graph was trained on {trainedMedian}");
            }
        }

        var described = Inputs.IsDefault ? [] : Inputs;
        foreach (var role in described.GroupBy(i => i.Role, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
        {
            problems.Add($"it describes more than one '{role}' input");
        }
        foreach (var fed in feed.Inputs)
        {
            if (InputFor(fed.Role) is not { } input)
            {
                problems.Add($"the runner feeds a '{fed.Role}' input and the contract describes none");
                continue;
            }
            if (fed.Channels != input.Channels)
            {
                problems.Add($"the runner feeds '{fed.Role}' with {DescribeChannels(fed.Channels)} and the contract says {DescribeChannels(input.Channels)}");
            }
            if (fed.Scale is { } fedScale)
            {
                if (input.Scale is not { } trainedScale)
                {
                    problems.Add($"the runner feeds '{fed.Role}' in units of {fedScale} and the contract does not state the units the graph was trained on");
                }
                else if (!Near(fedScale, trainedScale))
                {
                    problems.Add($"the runner feeds '{fed.Role}' in units of {fedScale} and the graph was trained on {trainedScale}");
                }
            }
        }
        foreach (var input in described.Where(i => !feed.Inputs.Any(f => string.Equals(f.Role, i.Role, StringComparison.Ordinal))))
        {
            problems.Add($"it describes a '{input.Role}' input ('{input.Name}') that the runner does not feed");
        }
    }

    private void CheckGraph(ModelGraph graph, ImmutableArray<string>.Builder problems)
    {
        var described = Inputs.IsDefault ? [] : Inputs;
        foreach (var input in described)
        {
            if (!graph.TryGetInput(input.Name, out var tensor))
            {
                problems.Add($"it names an input '{input.Name}' ('{input.Role}') and the graph has {DescribeNames(graph.Inputs.Select(t => t.Name))}");
                continue;
            }
            CheckDimensions(input, tensor, problems);
        }
        foreach (var tensor in graph.Inputs.Where(t => !described.Any(i => string.Equals(i.Name, t.Name, StringComparison.Ordinal))))
        {
            problems.Add($"the graph takes an input '{tensor.Name}' that the contract does not describe");
        }
        if (!graph.Outputs.Contains(Output, StringComparer.Ordinal))
        {
            problems.Add($"it names an output '{Output}' and the graph has {DescribeNames(graph.Outputs)}");
        }
    }

    private static void CheckDimensions(ModelContractInput input, ModelGraphTensor tensor, ImmutableArray<string>.Builder problems)
    {
        var dims = tensor.Dimensions;
        if (input.Channels is not { } channels)
        {
            if (dims.Length > 2)
            {
                problems.Add($"'{input.Name}' is a scalar input in the contract and the graph declares {tensor.Describe()}");
            }
            return;
        }
        if (dims.Length != 4)
        {
            problems.Add($"'{input.Name}' is an NCHW input in the contract and the graph declares {tensor.Describe()}");
            return;
        }
        // A fixed graph dimension must be the stated one; an open one (ORT reports it as non-positive) is the runner's to
        // fill and is accepted, since the contract states what the model was trained on, not what its graph forbids.
        CheckDimension(input, tensor, "channels", dims[1], channels, problems);
        if (input.Height is { } height)
        {
            CheckDimension(input, tensor, "height", dims[2], height, problems);
        }
        if (input.Width is { } width)
        {
            CheckDimension(input, tensor, "width", dims[3], width, problems);
        }
    }

    private static void CheckDimension(ModelContractInput input, ModelGraphTensor tensor, string what, int declared, int stated, ImmutableArray<string>.Builder problems)
    {
        if (declared > 0 && declared != stated)
        {
            problems.Add($"'{input.Name}' has {stated} {what} in the contract and the graph declares {tensor.Describe()}");
        }
    }

    private static string DescribeDomain(ModelDomain domain) => domain switch
    {
        ModelDomain.Linear => "linear pixels",
        ModelDomain.MtfStretched => "MTF-stretched pixels",
        _ => domain.ToString(),
    };

    private static string DescribeChannels(int? channels) => channels is { } n ? $"{n} channel(s)" : "no channel axis (a scalar)";

    private static string DescribeNames(IEnumerable<string> names)
        => names.ToArray() is { Length: > 0 } list ? string.Join(", ", list.Select(n => $"'{n}'")) : "none";

    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1.0, Math.Abs(b));
}

/// <summary>
/// A model that was refused because its contract is missing, unreadable or does not agree with the weights, the graph
/// or the runner. The message names the model and every mismatch.
/// </summary>
public sealed class ModelContractException : InvalidOperationException
{
    private ModelContractException(string modelFileName, string message)
        : base(message)
    {
        ModelFileName = modelFileName;
    }

    /// <summary>The model file that was refused.</summary>
    public string ModelFileName { get; }

    internal static ModelContractException Absent(string modelFile, string contractPath)
        => new(modelFile,
            $"Model '{modelFile}' has no contract: '{contractPath}' does not exist. Every TianWen model ships a "
            + $"'{ModelContract.ContractFileName(modelFile)}' beside its .onnx stating the domain and tensors it was trained on, and a "
            + "model that cannot be checked against one is refused, because an unstated input is how the denoiser once ran about "
            + "100x below its training band with nothing to notice. In a checkout the contract is under "
            + "src/TianWen.AI.Imaging/models/; a copy of the .onnx elsewhere needs its contract copied with it.");

    internal static ModelContractException Refused(string modelFile, string contractPath, ImmutableArray<string> problems)
        => new(modelFile,
            $"Model '{modelFile}' was refused by its contract '{contractPath}':{Environment.NewLine}"
            + string.Join(Environment.NewLine, problems.Select(p => "  - " + p))
            + $"{Environment.NewLine}A model is fed only what its contract says it was trained on. Fix the runner, or re-export "
            + "the model with a contract that states what it really was trained on; never edit a contract to make this pass.");
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(ModelContract))]
internal sealed partial class ModelContractJsonContext : JsonSerializerContext
{
}
