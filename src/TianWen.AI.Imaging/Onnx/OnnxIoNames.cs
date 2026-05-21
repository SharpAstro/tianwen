using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;

namespace TianWen.AI.Imaging.Onnx;

/// <summary>
/// Helpers that resolve the bound input + output tensor names on an
/// <see cref="InferenceSession"/>. Each enhancer needs to know what to pass
/// as the dict keys in <c>session.Run</c>; the model files don't standardise
/// the names so we introspect the session metadata.
/// </summary>
internal static class OnnxIoNames
{
    /// <summary>
    /// Single-input + single-output classification (the canonical 1-IO NAFNet
    /// shape used by <see cref="OnnxStarRemover"/> and
    /// <see cref="OnnxStellarSharpener"/>).
    /// </summary>
    public static (string imageInput, string output) SingleInput(InferenceSession session)
    {
        var inputs = session.InputMetadata;
        if (inputs.Count != 1)
        {
            throw new InvalidOperationException(
                $"OnnxIoNames.SingleInput: expected 1 input, got {inputs.Count} ({string.Join(", ", inputs.Keys)}).");
        }
        if (session.OutputMetadata.Count != 1)
        {
            throw new InvalidOperationException(
                $"OnnxIoNames.SingleInput: expected 1 output, got {session.OutputMetadata.Count}.");
        }
        return (inputs.Keys.First(), session.OutputMetadata.Keys.First());
    }

    /// <summary>
    /// Two-input image + scalar classification. Used by
    /// <see cref="OnnxNonStellarDeconvolver"/>. The image input has rank 4
    /// (NCHW); the scalar input has rank &lt;= 2 (e.g. <c>[1, 1]</c>). Same
    /// heuristic as SAS Pro's <c>_ort_pick_io_names</c>.
    /// </summary>
    public static (string imageInput, string scalarInput, string output) ImagePlusScalar(InferenceSession session)
    {
        var inputs = session.InputMetadata;
        if (inputs.Count != 2)
        {
            throw new InvalidOperationException(
                $"OnnxIoNames.ImagePlusScalar: expected 2 inputs (image + scalar), got {inputs.Count}: " +
                string.Join(", ", inputs.Keys));
        }

        string? imageName = null;
        string? scalarName = null;
        foreach (var (name, meta) in inputs)
        {
            if (meta.Dimensions.Length <= 2)
                scalarName = name;
            else
                imageName = name;
        }
        if (imageName is null || scalarName is null)
        {
            throw new InvalidOperationException(
                "OnnxIoNames.ImagePlusScalar: could not classify inputs by rank; got: " +
                string.Join(", ", inputs.Select(kv => $"{kv.Key}=[{string.Join(",", kv.Value.Dimensions)}]")));
        }

        if (session.OutputMetadata.Count != 1)
        {
            throw new InvalidOperationException(
                $"OnnxIoNames.ImagePlusScalar: expected 1 output, got {session.OutputMetadata.Count}.");
        }
        return (imageName, scalarName, session.OutputMetadata.Keys.First());
    }
}
