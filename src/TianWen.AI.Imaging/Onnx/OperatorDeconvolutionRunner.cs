using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TianWen.Lib;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.Onnx;

/// <summary>
/// Runs a deconvolution OPERATOR graph over a whole frame (E3.4d's: Richardson-Lucy with a learned prior, the kernel an
/// input; <c>docs/plans/deconvolver-training.md</c>, "The E3.4d graph, exported"), the way
/// <c>training/denoise/n2n_operator_master.py</c> ran it for every whole-master readout. The steps, in its order:
/// <list type="number">
/// <item>the stretch's minimum and balance, ONCE, on the native frame (<see cref="Image.MtfStretchParameters"/>, the
/// covered pixels only, as every runner here measures them);</item>
/// <item>the LINEAR frame resampled up by <see cref="DeconvolutionKernel.Resample"/> (<see cref="SplineZoom"/>, scipy's
/// cubic spline) and stretched with the native frame's parameters;</item>
/// <item>each channel's Moffat kernel at its FWHM times the same factor, the three padded about their centres to one
/// size;</item>
/// <item>the stretched frame in overlapping tiles (<see cref="TileFor"/>, <see cref="TileMargin"/> cut from every edge,
/// the frame's edge replicated past it), each through the graph, which runs every channel's own pass;</item>
/// <item>the stitched output resampled DOWN to the native size, in STRETCHED units, then unstretched, and the canvas ring
/// put back exactly as it came in.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>The resample is outside the graph on purpose</b>: scipy's order-3 zoom is a B-spline with a global
/// prefilter, which ONNX's Resize does not reproduce, and it acts on the whole frame before tiling and after stitching.
/// A different resampler would change what the prior sees and so its measured behaviour.</para>
///
/// <para><b>The stretch is never skipped</b>, unlike <see cref="ChunkedNafnetRunner"/>'s auto-detect: the graph
/// unstretches inside itself with the parameters it is handed, so a frame it receives unstretched with a minimum of 0
/// and a balance of 0.5 would be deconvolved in the wrong units.</para>
///
/// <para><b>Cost.</b> One tile is three passes of a 20-step operator: a 1312 px tile took 10 s on DirectML (a GTX 1070
/// that was also training) and 66 s on the CPU (2026-10-09, a 16-core box under load).</para>
/// </remarks>
internal static class OperatorDeconvolutionRunner
{
    /// <summary>The tile edge at native scale (<c>n2n_operator_master.py --tile</c>); the graph's tile is this times the
    /// resample factor, rounded to a multiple of <see cref="TileMultiple"/>.</summary>
    public const int NativeTile = 1024;

    /// <summary>The margin cut from every tile's edge (<c>--margin</c>): Richardson-Lucy pads each tile with its edge, and
    /// the seam that leaves sits inside this.</summary>
    public const int TileMargin = 96;

    /// <summary>The graph needs a multiple of 4 (the prior's two poolings); the readouts used 16.</summary>
    public const int TileMultiple = 16;

    /// <summary>The graph's tile edge at <paramref name="resample"/>: <c>round(1024 * resample / 16) * 16</c>, Python's
    /// round-half-to-even, as the master runner computes it (1312 at 1.28125).</summary>
    public static int TileFor(double resample)
        => (int)Math.Round(NativeTile * resample / TileMultiple, MidpointRounding.ToEven) * TileMultiple;

    /// <summary>
    /// Deconvolves <paramref name="input"/> (three channels in about <c>[0, 1]</c>) through <paramref name="session"/>.
    /// </summary>
    /// <param name="tile">The graph's tile edge, a multiple of 4 above <c>2 * </c><see cref="TileMargin"/>; <c>null</c>
    /// for <see cref="TileFor"/> at the kernel's factor. A test passes a small one; the output depends on it near the
    /// frame's edge, as the reference's does.</param>
    public static OperatorDeconvolutionResult Run(
        Image input,
        InferenceSession session,
        OperatorGraphNames names,
        DeconvolutionKernel kernel,
        int? tile = null,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (channels, width, height) = input.Shape;
        if (channels != 3)
        {
            throw new NotSupportedException($"The deconvolution operator takes three channels, got {channels}.");
        }
        var tileEdge = tile ?? TileFor(kernel.Resample);
        if (tileEdge % 4 != 0 || tileEdge <= 2 * TileMargin)
        {
            throw new ArgumentOutOfRangeException(nameof(tile), tileEdge, $"a tile must be a multiple of 4 above {2 * TileMargin} px");
        }

        var total = Stopwatch.StartNew();
        var phase = Stopwatch.StartNew();

        // 1. The stretch, once, on the native frame's covered pixels; the ring is put back at the end.
        var absent = input.AbsentPixels();
        var (stretchMin, balances) = input.MtfStretchParameters(AiNafnetInputs.TargetMedian, absent);

        // 2. Up in LINEAR, then stretched in place with the native parameters, exactly as Image.MtfStretchWith maps a
        //    pixel (the minimum off, clamped at zero, the MTF), without a second zoomed-size copy.
        var zoomHeight = SplineZoom.OutputSize(height, kernel.Resample);
        var zoomWidth = SplineZoom.OutputSize(width, kernel.Resample);
        var stretched = new float[channels][,];
        var native = new float[height, width];
        for (var c = 0; c < channels; c++)
        {
            input.GetChannelSpan(c).CopyTo(MemoryMarshal.CreateSpan(ref native[0, 0], width * height));
            var plane = SplineZoom.Zoom(native, zoomHeight, zoomWidth, cancellationToken);
            StretchInPlace(MemoryMarshal.CreateSpan(ref plane[0, 0], zoomWidth * zoomHeight), stretchMin[c], balances[c]);
            stretched[c] = plane;
        }
        var resampleUpMs = phase.ElapsedMilliseconds; phase.Restart();

        // 3. The kernels at the zoomed scale, one per channel, at one size.
        var kernelTensor = KernelTensor(kernel, kernel.Resample);
        var minTensor = new DenseTensor<float>(stretchMin.AsMemory(), [channels]);
        var balanceTensor = new DenseTensor<float>([channels]);
        for (var c = 0; c < channels; c++)
        {
            balanceTensor[c] = (float)balances[c];
        }

        // 4. Tiles over the stretched frame, the edge replicated past it, the margin cut from each output tile.
        var core = tileEdge - (2 * TileMargin);
        var tilesY = (zoomHeight + core - 1) / core;
        var tilesX = (zoomWidth + core - 1) / core;
        var output = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            output[c] = new float[zoomHeight, zoomWidth];
        }
        var planeStride = tileEdge * tileEdge;
        var tileBuffer = new float[channels * planeStride];
        for (var ty = 0; ty < tilesY; ty++)
        {
            for (var tx = 0; tx < tilesX; tx++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var y0 = ty * core;
                var x0 = tx * core;
                for (var c = 0; c < channels; c++)
                {
                    FillTile(stretched[c], y0 - TileMargin, x0 - TileMargin, tileEdge, tileBuffer.AsSpan(c * planeStride, planeStride));
                }

                var imageTensor = new DenseTensor<float>(tileBuffer.AsMemory(), [1, channels, tileEdge, tileEdge]);
                using var results = session.Run(
                [
                    NamedOnnxValue.CreateFromTensor(names.Image, imageTensor),
                    NamedOnnxValue.CreateFromTensor(names.Kernel, kernelTensor),
                    NamedOnnxValue.CreateFromTensor(names.StretchMin, minTensor),
                    NamedOnnxValue.CreateFromTensor(names.StretchBalance, balanceTensor),
                ], [names.Output]);
                var result = results[0].AsTensor<float>().ToDenseTensor().Buffer.Span;

                var rows = Math.Min(core, zoomHeight - y0);
                var cols = Math.Min(core, zoomWidth - x0);
                for (var c = 0; c < channels; c++)
                {
                    var plane = output[c];
                    for (var y = 0; y < rows; y++)
                    {
                        var from = result.Slice((c * planeStride) + ((TileMargin + y) * tileEdge) + TileMargin, cols);
                        from.CopyTo(MemoryMarshal.CreateSpan(ref plane[y0 + y, x0], cols));
                    }
                }
                progress?.Report(((ty * tilesX) + tx + 1) / (float)(tilesY * tilesX));
            }
        }
        var inferMs = phase.ElapsedMilliseconds; phase.Restart();

        // 5. Down in STRETCHED units to the native size, unstretched (the MTF clamps to [0, 1] first, as the reference
        //    clips), and the ring back as it came in.
        var back = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            back[c] = SplineZoom.Zoom(output[c], height, width, cancellationToken);
        }
        var deconvolved = new Image(back, BitDepth.Float32, 1.0f, 0f, 0f, input.ImageMeta).MtfUnstretch(stretchMin, balances);
        if (absent is { } ring)
        {
            var ringed = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var plane = new float[height, width];
                var dst = MemoryMarshal.CreateSpan(ref plane[0, 0], width * height);
                deconvolved.GetChannelSpan(c).CopyTo(dst);
                Image.CopyAbsent(input.GetChannelSpan(c), dst, width, ring);
                ringed[c] = plane;
            }
            deconvolved = new Image(ringed, BitDepth.Float32, deconvolved.MaxValue, deconvolved.MinValue, deconvolved.Pedestal, input.ImageMeta);
        }
        var resampleDownMs = phase.ElapsedMilliseconds;

        return new OperatorDeconvolutionResult(
            deconvolved, tilesY * tilesX, tileEdge, zoomWidth, zoomHeight, kernelTensor.Dimensions[1], stretchMin, balances,
            resampleUpMs, inferMs, resampleDownMs, total.ElapsedMilliseconds);
    }

    /// <summary>
    /// The graph's kernel input: per channel the Moffat <see cref="PsfKernel.Moffat"/> builds at that channel's FWHM times
    /// <paramref name="scale"/> (through float32 first, as the readouts carried it), or the delta at a FWHM of 0, each
    /// zero-padded about its centre to the widest one's size (<c>n2n_operator_export.channel_kernels</c>).
    /// </summary>
    internal static DenseTensor<float> KernelTensor(DeconvolutionKernel kernel, double scale)
    {
        Span<float> widths = stackalloc float[3];
        var radius = 1;
        var built = new PsfKernel?[3];
        for (var c = 0; c < 3; c++)
        {
            widths[c] = (float)(kernel.FwhmOf(c) * scale);
            if (widths[c] > 0)
            {
                built[c] = PsfKernel.Moffat(widths[c], kernel.Beta);
                radius = Math.Max(radius, built[c]?.Radius ?? 1);
            }
        }

        var size = (2 * radius) + 1;
        var tensor = new DenseTensor<float>([3, size, size]);
        for (var c = 0; c < 3; c++)
        {
            if (built[c] is not { } psf)
            {
                tensor[c, radius, radius] = 1f;
                continue;
            }
            var weights = psf.Weights;
            var offset = radius - psf.Radius;
            for (var y = 0; y < psf.Size; y++)
            {
                for (var x = 0; x < psf.Size; x++)
                {
                    tensor[c, offset + y, offset + x] = weights[(y * psf.Size) + x];
                }
            }
        }
        return tensor;
    }

    /// <summary><see cref="Image.MtfStretchWith"/>'s map of one pixel, on a plane this runner owns: NaN kept, the minimum
    /// off and clamped at zero, the MTF at <paramref name="balance"/>.</summary>
    private static void StretchInPlace(Span<float> plane, float min, double balance)
    {
        for (var i = 0; i < plane.Length; i++)
        {
            var v = plane[i];
            if (float.IsNaN(v))
            {
                continue;
            }
            var shifted = v - min;
            if (shifted < 0f) shifted = 0f;
            plane[i] = (float)Image.MidtonesTransferFunction(balance, shifted);
        }
    }

    /// <summary>One tile of <paramref name="plane"/> from (<paramref name="top"/>, <paramref name="left"/>), which may lie
    /// outside it: the plane's edge replicated (<c>np.pad(mode="edge")</c> in the reference).</summary>
    private static void FillTile(float[,] plane, int top, int left, int edge, Span<float> into)
    {
        var height = plane.GetLength(0);
        var width = plane.GetLength(1);
        for (var y = 0; y < edge; y++)
        {
            var sy = Math.Clamp(top + y, 0, height - 1);
            var row = MemoryMarshal.CreateReadOnlySpan(ref plane[sy, 0], width);
            var dst = into.Slice(y * edge, edge);
            var x = 0;
            for (; x < edge && left + x < 0; x++)
            {
                dst[x] = row[0];
            }
            var inside = Math.Min(edge, width - left) - x;
            if (inside > 0)
            {
                row.Slice(left + x, inside).CopyTo(dst.Slice(x, inside));
                x += inside;
            }
            for (; x < edge; x++)
            {
                dst[x] = row[width - 1];
            }
        }
    }
}

/// <summary>The graph's tensors by name, as its contract names them by role.</summary>
internal sealed record OperatorGraphNames(string Image, string Kernel, string StretchMin, string StretchBalance, string Output);

/// <summary>What <see cref="OperatorDeconvolutionRunner.Run"/> produced, with what it ran at and how long each phase took.</summary>
internal sealed record OperatorDeconvolutionResult(
    Image Output,
    int Tiles,
    int TileEdge,
    int ZoomedWidth,
    int ZoomedHeight,
    int KernelSize,
    float[] StretchMin,
    double[] Balances,
    long ResampleUpMs,
    long InferMs,
    long ResampleDownMs,
    long TotalMs);
