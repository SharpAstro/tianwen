using System;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime.Tensors;
using TianWen.Lib.Imaging;

namespace TianWen.AI.Imaging;

/// <summary>
/// Converts between <see cref="Image"/> (TianWen's planar
/// <c>float[ch][height,width]</c> representation) and ORT's
/// <see cref="DenseTensor{T}"/> in NCHW layout (batch=1).
/// Every <c>IImageEnhancer</c> implementation goes through this helper, so
/// it's the single place that knows TianWen's pixel range conventions and
/// the row-major flattening order the ONNX models expect.
/// </summary>
/// <remarks>
/// Pixel range convention: TianWen images carry values in the source bit
/// depth's natural scale (e.g. [0, 65535] for Int16 FITS, [0, 1] post-
/// <c>ScaleFloatValuesToUnitInPlace</c>). Most NAFNet / Cosmic-Clarity-style
/// models were trained on [0, 1] inputs, so callers should normalise first
/// (typically via <c>Image.ScaleFloatValuesToUnitInPlace</c> on a copy, or
/// by dividing by <c>MaxValue</c> here). This converter does not normalise
/// implicitly -- it's a verbatim byte-shuffler.
/// </remarks>
public static class TensorImageConverter
{
    /// <summary>
    /// Flattens an <see cref="Image"/> into an NCHW tensor with batch
    /// dimension 1: shape <c>[1, ChannelCount, Height, Width]</c>. The
    /// returned tensor owns its buffer; the input image is not mutated.
    /// </summary>
    public static DenseTensor<float> ToNchwTensor(Image image)
    {
        var (channels, width, height) = image.Shape;
        var dims = new[] { 1, channels, height, width };
        var tensor = new DenseTensor<float>(dims);
        var dst = tensor.Buffer.Span;
        var planeStride = height * width;

        for (var c = 0; c < channels; c++)
        {
            // GetChannelSpan returns row-major HxW floats already; copying
            // it straight into the NCHW slice matches ONNX layout because
            // we treat batch=1 as just a leading dimension on top.
            var src = image.GetChannelSpan(c);
            src.CopyTo(dst.Slice(c * planeStride, planeStride));
        }

        return tensor;
    }

    /// <summary>
    /// Reconstructs an <see cref="Image"/> from an NCHW tensor produced by
    /// inference. Metadata is copied from <paramref name="reference"/>
    /// (most enhancers preserve WCS / instrument fields verbatim), and
    /// <see cref="Image.MaxValue"/> / <see cref="Image.MinValue"/> are
    /// recomputed from the tensor since the model may have rescaled.
    /// </summary>
    /// <param name="tensor">NCHW output, batch must be 1.</param>
    /// <param name="reference">Image used to source metadata + pedestal +
    /// bit depth. Its pixel buffer is untouched.</param>
    public static Image FromNchwTensor(DenseTensor<float> tensor, Image reference)
    {
        var dims = tensor.Dimensions;
        if (dims.Length != 4 || dims[0] != 1)
        {
            throw new ArgumentException(
                $"Expected NCHW tensor with batch=1, got [{string.Join(",", dims.ToArray())}].",
                nameof(tensor));
        }

        var channels = dims[1];
        var height = dims[2];
        var width = dims[3];
        var src = tensor.Buffer.Span;
        var planeStride = height * width;

        var data = new float[channels][,];
        float min = float.PositiveInfinity, max = float.NegativeInfinity;

        for (var c = 0; c < channels; c++)
        {
            var plane = new float[height, width];
            // MemoryMarshal flattens the row-major float[,] to a writable
            // span without an extra copy. Source layout already matches.
            var dstSpan = MemoryMarshal.CreateSpan(ref plane[0, 0], planeStride);
            src.Slice(c * planeStride, planeStride).CopyTo(dstSpan);

            foreach (var v in dstSpan)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
            data[c] = plane;
        }

        return new Image(
            data,
            reference.BitDepth,
            maxValue: max,
            minValue: min,
            pedestal: reference.Pedestal,
            imageMeta: reference.ImageMeta);
    }

    /// <summary>
    /// NHWC variant of <see cref="ToNchwTensor"/>: shape
    /// <c>[1, Height, Width, ChannelCount]</c>. TensorFlow-style layout,
    /// used by models exported via Keras / tf.saved_model (GraXpert's BGE
    /// background-extraction model is one such -- its <c>gen_input_image</c>
    /// input expects NHWC). The interleaved layout means a single row-major
    /// CopyTo isn't enough; we have to walk per-pixel and stripe the
    /// channels into adjacent positions.
    /// </summary>
    public static DenseTensor<float> ToNhwcTensor(Image image)
    {
        var (channels, width, height) = image.Shape;
        var dims = new[] { 1, height, width, channels };
        var tensor = new DenseTensor<float>(dims);
        var dst = tensor.Buffer.Span;
        for (var c = 0; c < channels; c++)
        {
            var src = image.GetChannelSpan(c);
            // dst[y, x, c] = src[y * w + x]. With channels innermost, we
            // stride by `channels` between adjacent pixels of the same plane.
            for (var i = 0; i < src.Length; i++)
            {
                dst[i * channels + c] = src[i];
            }
        }
        return tensor;
    }

    /// <summary>
    /// NHWC counterpart to <see cref="FromNchwTensor"/>. De-interleaves the
    /// channel-innermost tensor back into TianWen's planar
    /// <c>float[ch][height,width]</c> representation. Batch must be 1.
    /// </summary>
    public static Image FromNhwcTensor(DenseTensor<float> tensor, Image reference)
    {
        var dims = tensor.Dimensions;
        if (dims.Length != 4 || dims[0] != 1)
        {
            throw new ArgumentException(
                $"Expected NHWC tensor with batch=1, got [{string.Join(",", dims.ToArray())}].",
                nameof(tensor));
        }
        var height = dims[1];
        var width = dims[2];
        var channels = dims[3];
        var src = tensor.Buffer.Span;
        var planeStride = height * width;

        var data = new float[channels][,];
        float min = float.PositiveInfinity, max = float.NegativeInfinity;

        for (var c = 0; c < channels; c++)
        {
            var plane = new float[height, width];
            var dstSpan = MemoryMarshal.CreateSpan(ref plane[0, 0], planeStride);
            for (var i = 0; i < planeStride; i++)
            {
                var v = src[i * channels + c];
                dstSpan[i] = v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            data[c] = plane;
        }

        return new Image(
            data,
            reference.BitDepth,
            maxValue: max,
            minValue: min,
            pedestal: reference.Pedestal,
            imageMeta: reference.ImageMeta);
    }
}
