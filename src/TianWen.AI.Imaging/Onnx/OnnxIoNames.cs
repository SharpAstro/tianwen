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
    /// shape: an image in, an image out).
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
    /// The channel count the model declares on its NCHW image input, i.e. the C in
    /// <c>[N, C, H, W]</c>.
    /// <para>
    /// <b>Ask the model, never assume.</b> The AI4 family ships separate mono and colour weight
    /// bundles (<c>darkstar_mono</c> / <c>darkstar_color</c>, <c>deep_denoise_mono</c> /
    /// <c>deep_denoise_color</c>), and the obvious reading -- that a "mono" model therefore takes one
    /// channel -- is wrong: they share one 3-channel architecture and differ only in what they were
    /// trained on, so a mono source is fed by replicating its single channel across the three input
    /// slots. Two enhancers used to hardcode <c>modelChannels: sourceChannels</c> on that assumption
    /// and threw <c>Got: 1 Expected: 3</c> for every mono frame, which no test caught because every
    /// star-removal and denoise test fed colour. The declared dimension cannot drift from the file on
    /// disk, so read it.
    /// </para>
    /// </summary>
    /// <param name="fallback">Returned when the model leaves the channel dimension dynamic (ORT
    /// reports a non-positive dim). Such a model accepts any channel count, so the caller's own
    /// source channel count is the right answer.</param>
    public static int ImageInputChannels(InferenceSession session, string imageInputName, int fallback)
    {
        if (!session.InputMetadata.TryGetValue(imageInputName, out var meta))
        {
            throw new InvalidOperationException(
                $"OnnxIoNames.ImageInputChannels: no input named '{imageInputName}' (have: " +
                string.Join(", ", session.InputMetadata.Keys) + ").");
        }

        var dims = meta.Dimensions;
        if (dims.Length != 4)
        {
            throw new InvalidOperationException(
                $"OnnxIoNames.ImageInputChannels: '{imageInputName}' is not a rank-4 NCHW image input; " +
                $"got [{string.Join(",", dims)}].");
        }

        return dims[1] > 0 ? dims[1] : fallback;
    }

    /// <summary>
    /// The spatial tile size the model declares on its NCHW image input, i.e. the (H, W) in
    /// <c>[N, C, H, W]</c>, or <c>null</c> for either axis the model leaves dynamic.
    /// <para>
    /// Same rule as <see cref="ImageInputChannels"/>: ask the model. It matters more than usual for
    /// <see cref="N2nDenoiser"/>, whose graph is fixed at 256 on purpose -- it computes its own
    /// noise-conditioning plane from the darkest half of the tile it is handed, so the tile IS the
    /// estimator's support region and a different size would silently be a different statistic from
    /// the one it was trained against. A caller that picked its own chunk size would change the
    /// conditioning by changing how it chunks.
    /// </para>
    /// </summary>
    public static (int? Height, int? Width) ImageInputTileSize(InferenceSession session, string imageInputName)
    {
        if (!session.InputMetadata.TryGetValue(imageInputName, out var meta))
        {
            throw new InvalidOperationException(
                $"OnnxIoNames.ImageInputTileSize: no input named '{imageInputName}' (have: " +
                string.Join(", ", session.InputMetadata.Keys) + ").");
        }

        var dims = meta.Dimensions;
        if (dims.Length != 4)
        {
            throw new InvalidOperationException(
                $"OnnxIoNames.ImageInputTileSize: '{imageInputName}' is not a rank-4 NCHW image input; " +
                $"got [{string.Join(",", dims)}].");
        }

        return (dims[2] > 0 ? dims[2] : null, dims[3] > 0 ? dims[3] : null);
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

    /// <summary>
    /// Whether a graph takes an image and a per-pixel plane (two rank-4 inputs), the E16 conditioning, rather than an
    /// image and a scalar: the graph's inputs decide which path a runner takes, so a checkpoint cannot be fed the
    /// conditioning of the other kind.
    /// </summary>
    public static bool IsImagePlusPlane(InferenceSession session)
        => session.InputMetadata.Count == 2 && session.InputMetadata.Values.All(static m => m.Dimensions.Length == 4);

    /// <summary>
    /// The input and output names of an image-plus-plane graph (<c>n2n_export.py</c>'s <c>mapped</c> shape): the plane is
    /// the rank-4 input with ONE channel, the image the other.
    /// </summary>
    public static (string imageInput, string planeInput, string output) ImagePlusPlane(InferenceSession session)
    {
        if (!IsImagePlusPlane(session))
        {
            throw new InvalidOperationException(
                "OnnxIoNames.ImagePlusPlane: expected 2 rank-4 inputs (image + plane), got " +
                string.Join(", ", session.InputMetadata.Select(kv => $"{kv.Key}=[{string.Join(",", kv.Value.Dimensions)}]")));
        }
        var planes = session.InputMetadata.Where(static kv => kv.Value.Dimensions[1] == 1).Select(static kv => kv.Key).ToList();
        if (planes.Count != 1)
        {
            throw new InvalidOperationException(
                "OnnxIoNames.ImagePlusPlane: could not tell the plane (one channel) from the image; got " +
                string.Join(", ", session.InputMetadata.Select(kv => $"{kv.Key}=[{string.Join(",", kv.Value.Dimensions)}]")));
        }
        if (session.OutputMetadata.Count != 1)
        {
            throw new InvalidOperationException(
                $"OnnxIoNames.ImagePlusPlane: expected 1 output, got {session.OutputMetadata.Count}.");
        }
        var image = session.InputMetadata.Keys.First(k => k != planes[0]);
        return (image, planes[0], session.OutputMetadata.Keys.First());
    }
}
