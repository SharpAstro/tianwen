using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.ML.OnnxRuntime;

namespace TianWen.AI.Imaging.Onnx;

/// <summary>
/// What a runner actually feeds a graph, stated by the runner from its own constants: the other half of the comparison
/// a <see cref="ModelContract"/> is checked against. It is never read back from the contract, or the check would compare
/// the contract with itself.
/// </summary>
/// <param name="Domain">The domain of the tensors the runner builds.</param>
/// <param name="StretchMedianTarget">For <see cref="ModelDomain.MtfStretched"/>, the per-channel median the runner
/// stretches a frame to; <c>null</c> where it does not stretch.</param>
/// <param name="Inputs">Every input the runner fills, by role.</param>
internal sealed record ModelFeed(ModelDomain Domain, double? StretchMedianTarget, ImmutableArray<ModelFeedInput> Inputs);

/// <summary>One input a runner fills.</summary>
/// <param name="Role">What the tensor is (<see cref="ModelRoles"/>).</param>
/// <param name="Channels">The channel count the runner builds; <c>null</c> for a scalar.</param>
/// <param name="Scale">The units of a conditioning tensor the runner builds (the noise plane's
/// <c>StretchedNoise.PlaneScale</c>); <c>null</c> for a tensor with no such scale.</param>
internal readonly record struct ModelFeedInput(string Role, int? Channels, double? Scale = null);

/// <summary>
/// The interface an ONNX file declares, as a plain value, so a contract can be checked against a graph without a
/// session (a test describes a graph by hand; a loader reads it off the <see cref="InferenceSession"/>).
/// </summary>
/// <param name="Inputs">Every input, by name, with the dimensions the graph declares.</param>
/// <param name="Outputs">Every output's name.</param>
internal sealed record ModelGraph(ImmutableArray<ModelGraphTensor> Inputs, ImmutableArray<string> Outputs)
{
    /// <summary>The interface <paramref name="session"/>'s graph declares.</summary>
    public static ModelGraph From(InferenceSession session) => new(
        [.. session.InputMetadata.Select(static kv => new ModelGraphTensor(kv.Key, [.. kv.Value.Dimensions]))],
        [.. session.OutputMetadata.Keys]);

    /// <summary>The input called <paramref name="name"/>, or <c>false</c> where the graph has none.</summary>
    public bool TryGetInput(string name, out ModelGraphTensor tensor)
    {
        foreach (var candidate in Inputs)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                tensor = candidate;
                return true;
            }
        }
        tensor = default;
        return false;
    }
}

/// <summary>One graph input: its name and the dimensions it declares, an open axis as a non-positive number (as ORT
/// reports it).</summary>
internal readonly record struct ModelGraphTensor(string Name, ImmutableArray<int> Dimensions)
{
    /// <summary>The shape as written in a refusal: <c>[?,3,256,256]</c>, an open axis as <c>?</c>.</summary>
    public string Describe()
        => $"[{string.Join(",", Dimensions.Select(static d => d > 0 ? d.ToString() : "?"))}]";
}
