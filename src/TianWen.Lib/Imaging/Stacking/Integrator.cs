using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Options for the stack <see cref="Integrator"/>: rejector + combiner +
/// normalization controls. v1 in-memory integrator: caller pre-warps each
/// frame to a common reference grid via <see cref="Image.WarpToReferenceGridAsync"/>.
/// </summary>
/// <param name="Rejector">Per-pixel-column outlier rejector. <c>null</c>
/// skips rejection; every frame contributes to the combine.</param>
/// <param name="Combiner">How to combine the kept entries.
/// <c>null</c> defaults to <see cref="MeanCombiner"/>.</param>
/// <param name="ApplyNormalization">When true (default), each frame is
/// normalized to a common median (controlled by
/// <paramref name="NormalizationTarget"/>) before stacking. Disable only
/// when callers have already normalized the frames.</param>
/// <param name="NormalizationTarget">Target median for per-frame
/// normalization. 0.5 (half of [0, 1]) gives output stretched into the
/// middle of the dynamic range; <c>~0.25</c> is typical for unstretched
/// linear data.</param>
public sealed record IntegrationOptions(
    IPixelRejector? Rejector = null,
    IPixelCombiner? Combiner = null,
    bool ApplyNormalization = true,
    float NormalizationTarget = 0.5f)
{
    /// <summary>
    /// For the two Bayer drizzle strategies with <see cref="ApplyNormalization"/> OFF: the per-CFA-colour
    /// sky every frame is SHIFTED onto before deposit (<see cref="Normalizer.OffsetCfaToReference"/>).
    /// Ignored when normalisation is on, which already equalises the sky, and by every strategy that
    /// debayers before combining, which has no Bayer phases to bias.
    ///
    /// <para><b>Why drizzle needs it even unnormalised.</b> Registration and dither spread drizzle weight
    /// unevenly over the four CFA phases, so each phase averages a different mix of frames; a sky that
    /// drifts through the session then settles each phase at a different level, a fixed 2x2 pattern in
    /// the master. The dataset bake integrates unnormalised to keep its master on the subs' linear
    /// scale, and its drizzled masters measured a median 0.28 sigma of it, 8.9 at worst. A shift keeps
    /// that scale and every star's flux; a rescale would not. Set ONE reference for a master and the
    /// halves built beside it, or the halves land on different sky levels.</para>
    ///
    /// <para><b>LEAVING THIS NULL WITH <see cref="ApplyNormalization"/> OFF REPRODUCES #292.</b> That
    /// combination deposits every frame at its own sky, which is exactly the state the Bayer-phase
    /// level pattern was found in, and it is silent: the master is written, looks plausible, and
    /// carries a fixed 2x2 pattern that only a phase measurement finds. It is unreachable today
    /// because the one caller that integrates unnormalised -- the dataset bake, via
    /// <c>SessionRegistrar</c> -- always sets a reference, so the null arm is reached only by a
    /// caller yet to be written. It is deliberately NOT guarded: normalisation-on legitimately
    /// leaves this null, and there is no way to tell "unnormalised on purpose, no reference needed"
    /// from "forgot" at this level. A new unnormalised drizzle caller owes a reference, and owes a
    /// phase measurement of its first master to prove it.</para>
    /// </summary>
    public Normalizer.CfaNormalizationStats? DrizzleSkyReference { get; init; }
}

/// <summary>
/// Result of a stack integration: the master image, a per-pixel rejection
/// fraction map, and aggregate stats. The rejection map is a single-channel
/// image whose pixel values are <c>rejected / total</c> for that output
/// position averaged across input channels.
/// </summary>
/// <param name="RejectionMapIsCoverage">
/// True when <paramref name="RejectionMap"/> is not a rejection fraction at all but the accumulated
/// per-pixel WEIGHT -- what the drizzle strategies put there, since drizzle has no kappa-sigma
/// rejection to report and the weight is the natural mask. The two are opposite in sense (high is bad
/// in one and good in the other) and different in range (a fraction in [0, 1] against a frame count),
/// so a consumer that guesses gets it exactly wrong. <see cref="Image.LargestCoveredRectangle(Image,
/// double, int)"/> needs the coverage kind, and <see cref="IntegrationFitsWriter"/> stamps this into
/// the sidecar's <c>MAPKIND</c> card so a reader can tell them apart later.
/// </param>
public sealed record IntegrationResult(
    Image Master,
    Image RejectionMap,
    int FrameCount,
    long TotalRejections,
    double MeanRejectionRate,
    bool RejectionMapIsCoverage = false)
{
    /// <summary>
    /// How many frames put a FINITE sample on each output pixel, averaged over the channels, for a
    /// strategy whose <see cref="RejectionMap"/> is a rejection fraction. Null where the rejection map
    /// already IS coverage (drizzle) and on a result built before this existed.
    /// </summary>
    /// <remarks>
    /// <para>The exact crop tier (<see cref="Image.LargestCoveredRectangle(Image, double, int)"/>)
    /// needs coverage, and for every strategy but drizzle it had none: the one sidecar carried the
    /// rejection fraction, which nothing reads, and every consumer of a staged master fell to
    /// <see cref="CoverageEdgeWalk"/>, which ESTIMATES the border and REFUSES an edge whose band never
    /// settles. On the V1045 Ori master a dither strip 350 px wide and 2.7x the noise ran down the
    /// left; the walk declined, the fallback trimmed 264 px, and the strip reached the gallery.</para>
    /// <para>An init property, not a positional parameter: a defaulted positional parameter on this
    /// record is a binary break for anything compiled against the old shape.</para>
    /// </remarks>
    public Image? Coverage { get; init; }
}

/// <summary>
/// Stack integrator. Combines N pre-aligned light frames into a single master
/// via optional per-pixel rejection + combine. v1 holds all frames in memory
/// (~1 GB for 30x 3008^2 RGB); Phase 10 will add the
/// <c>MemoryMappedFitsSink</c> path for big mosaic stacks that exceed RAM.
/// </summary>
/// <remarks>
/// Pre-condition: every input <see cref="Image"/> shares shape (width, height,
/// channel count) AND is warped to the same reference grid so pixel index N
/// maps to the same sky position across frames. The CLI orchestrator (Phase
/// 13) handles the calibrate + register + warp pipeline before calling here.
/// </remarks>
public static class Integrator
{
    /// <summary>
    /// Integrates <paramref name="alignedFrames"/> into a master image.
    /// Parallelized over output rows; each worker rents per-row scratch
    /// buffers from <see cref="ArrayPool{T}"/>.
    /// </summary>
    /// <param name="alignedFrames">Pre-warped frames sharing shape + reference grid.</param>
    /// <param name="options">Rejector / combiner / normalisation knobs.</param>
    /// <param name="masterSink">Optional canvas backing for the master. Null
    /// (default) allocates an <see cref="ArraySink"/> with the same shape as
    /// the input frames -- today's behaviour. Caller-supplied sinks (e.g.
    /// <see cref="MemoryMappedFitsSink"/> for big-canvas Phase 10 stacks) must
    /// match the input frame shape; ownership transfers to this method, which
    /// disposes on exit.</param>
    /// <param name="rejectSink">Optional canvas backing for the single-channel
    /// rejection-fraction map. Null (default) allocates an
    /// <see cref="ArraySink"/>. Caller-supplied sinks must be 1-channel and
    /// match the master shape.</param>
    /// <exception cref="ArgumentException">Empty frame list or shape mismatch
    /// across frames / sinks.</exception>
    public static IntegrationResult Integrate(
        IReadOnlyList<Image> alignedFrames,
        IntegrationOptions? options = null,
        IIntegrationSink? masterSink = null,
        IIntegrationSink? rejectSink = null,
        IntermediateFrameWriter? intermediates = null)
    {
        options ??= new IntegrationOptions();
        var combiner = options.Combiner ?? new MeanCombiner();
        var rejector = options.Rejector;

        ValidateInput(alignedFrames);

        var n = alignedFrames.Count;
        var (channelCount, width, height) = alignedFrames[0].Shape;

        // Snapshot the input channel arrays so the hot loop is plain indexing.
        // [channel][frame] -> float[h, w]
        var inputChannels = SnapshotChannelArrays(alignedFrames, channelCount, n);

        // Per-frame normalization scalars per channel: out = (in - min) * scale.
        // Computed once per frame; reused for all output pixels.
        var (frameMin, frameScale) = options.ApplyNormalization
            ? ComputeNormalizationScalars(alignedFrames, options.NormalizationTarget, n, channelCount)
            : (null, null);

        // --save-normalized. Dumped HERE rather than in a strategy because this is where the
        // scalars exist: the combine applies them inline per output pixel and never materialises
        // a normalized frame, so the only way to hand one to a human is to build it on purpose.
        // One frame at a time -- the whole set at once would double an already large working set.
        if (intermediates is { WantsNormalized: true } && frameMin is not null && frameScale is not null)
        {
            for (var f = 0; f < n; f++)
            {
                var planes = new float[channelCount][,];
                for (var c = 0; c < channelCount; c++)
                {
                    var src = inputChannels[c][f];
                    var lo = frameMin[c][f];
                    var scale = frameScale[c][f];
                    var plane = new float[height, width];
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            plane[y, x] = (src[y, x] - lo) * scale;
                        }
                    }
                    planes[c] = plane;
                }
                var normalized = new Image(planes, alignedFrames[f].BitDepth, 1f, 0f, 0f, alignedFrames[f].ImageMeta);
                intermediates.SaveNormalizedByIndex(normalized, f);
            }
        }

        // Caller-supplied sinks must agree with the input shape; null falls
        // back to today's heap-backed ArraySink so this overload stays a
        // no-behaviour-change drop-in for the existing call sites.
        ValidateSinkShape(masterSink, channelCount, width, height, nameof(masterSink));
        ValidateSinkShape(rejectSink, 1, width, height, nameof(rejectSink));
        using IIntegrationSink masterSinkInUse = masterSink ?? new ArraySink(channelCount, width, height);
        using IIntegrationSink rejectSinkInUse = rejectSink ?? new ArraySink(1, width, height);
        // Coverage: how many frames put a finite sample on each pixel, counted in the one loop that
        // already reads every sample. Without it this strategy's only sidecar is a rejection
        // fraction, which the exact crop tier cannot use, so every consumer of its masters falls to
        // CoverageEdgeWalk and an edge whose band never settles is declined. #315 gave the streaming
        // path this and left the in-RAM path without it, which is what a re-bake of V1045 Ori at HEAD
        // exposed: it chose InRamAllFrames and wrote no coverage plane at all.
        using IIntegrationSink coverageSinkInUse = new ArraySink(1, width, height);

        long totalRejections = 0;

        // One channel at a time. Parallel.For over rows of the current channel.
        for (var ch = 0; ch < channelCount; ch++)
        {
            var channelIdx = ch;
            var channelInputs = inputChannels[channelIdx];
            var minForCh = frameMin?[channelIdx];
            var scaleForCh = frameScale?[channelIdx];

            Parallel.For(0, height,
                localInit: () => new RowState
                {
                    Column = ArrayPool<float>.Shared.Rent(n),
                    KeepMask = ArrayPool<float>.Shared.Rent(n),
                    Rejections = 0,
                },
                body: (row, _, state) =>
                {
                    var column = state.Column;
                    var keepMask = state.KeepMask;
                    var columnSpan = column.AsSpan(0, n);
                    var maskSpan = keepMask.AsSpan(0, n);
                    // Sink row spans fetched once per row (sink-internal pointer
                    // arithmetic, not a per-pixel cost). Master span is written;
                    // reject span is read-modify-write to accumulate across
                    // channels (final /= channelCount happens below the channel loop).
                    var masterRow = masterSinkInUse.GetRow(channelIdx, row);
                    var rejectRow = rejectSinkInUse.GetRow(0, row);
                    var coverageRow = coverageSinkInUse.GetRow(0, row);

                    for (var col = 0; col < width; col++)
                    {
                        // Fill column with normalized values (or raw, if normalization disabled).
                        var finite = 0;
                        for (var f = 0; f < n; f++)
                        {
                            var v = channelInputs[f][row, col];
                            if (!float.IsNaN(v))
                            {
                                finite++;
                                if (minForCh is not null)
                                {
                                    v = (v - minForCh[f]) * scaleForCh![f];
                                }
                            }
                            column[f] = v;
                        }
                        coverageRow[col] += finite;

                        int kept;
                        if (rejector is not null)
                        {
                            kept = rejector.Reject(columnSpan, maskSpan);
                            state.Rejections += n - kept;
                        }
                        else
                        {
                            maskSpan.Fill(1f);
                            kept = n;
                        }

                        masterRow[col] = combiner.Combine(columnSpan, maskSpan);

                        // Accumulate per-pixel rejection rate across channels.
                        // Final divide by channelCount happens after the loop.
                        if (rejector is not null)
                        {
                            rejectRow[col] += (n - kept) / (float)n;
                        }
                    }
                    return state;
                },
                localFinally: state =>
                {
                    ArrayPool<float>.Shared.Return(state.Column);
                    ArrayPool<float>.Shared.Return(state.KeepMask);
                    Interlocked.Add(ref totalRejections, state.Rejections);
                });
        }

        // Average across channels. The rejection fraction only needs it when a rejector ran; the
        // coverage count always does, since every channel added its own tally.
        if (channelCount > 1)
        {
            var inv = 1f / channelCount;
            for (var y = 0; y < height; y++)
            {
                var rejectRow = rejectSinkInUse.GetRow(0, y);
                var coverageRow = coverageSinkInUse.GetRow(0, y);
                for (var x = 0; x < width; x++)
                {
                    if (rejector is not null)
                    {
                        rejectRow[x] *= inv;
                    }

                    coverageRow[x] *= inv;
                }
            }
        }

        var meanRate = n > 0
            ? (double)totalRejections / ((double)n * width * height * channelCount)
            : 0.0;

        var firstMeta = alignedFrames[0].ImageMeta;
        var masterImage = IntegratedMaster.Labelled(masterSinkInUse.FinaliseAsImage(
            BitDepth.Float32,
            maxValue: alignedFrames[0].MaxValue,
            minValue: 0f,
            pedestal: alignedFrames[0].Pedestal,
            meta: firstMeta), normalised: frameMin is not null);
        var rejectMapImage = rejectSinkInUse.FinaliseAsImage(
            BitDepth.Float32,
            maxValue: 1f,
            minValue: 0f,
            pedestal: 0f,
            meta: firstMeta);

        var coverageImage = CoveragePlane.Finalise(coverageSinkInUse, n, firstMeta);

        return new IntegrationResult(masterImage, rejectMapImage, n, totalRejections, meanRate)
        {
            Coverage = coverageImage,
        };
    }

    private struct RowState
    {
        public float[] Column;
        public float[] KeepMask;
        public long Rejections;
    }

    private static void ValidateInput(IReadOnlyList<Image> frames)
    {
        if (frames is null || frames.Count == 0)
        {
            throw new ArgumentException("Integrator needs at least one frame.", nameof(frames));
        }
        var first = frames[0];
        var (c, w, h) = first.Shape;
        for (var i = 1; i < frames.Count; i++)
        {
            var s = frames[i].Shape;
            if (s.ChannelCount != c || s.Width != w || s.Height != h)
            {
                throw new ArgumentException(
                    $"Frame {i} shape mismatch: expected {c}x{h}x{w}, got {s.ChannelCount}x{s.Height}x{s.Width}.",
                    nameof(frames));
            }
        }
    }

    private static void ValidateSinkShape(IIntegrationSink? sink, int channels, int width, int height, string paramName)
    {
        if (sink is null) return;
        var s = sink.Shape;
        if (s.ChannelCount != channels || s.Width != width || s.Height != height)
        {
            throw new ArgumentException(
                $"Sink shape mismatch: expected {channels}x{height}x{width}, got {s.ChannelCount}x{s.Height}x{s.Width}.",
                paramName);
        }
    }

    private static float[][][,] SnapshotChannelArrays(IReadOnlyList<Image> frames, int channelCount, int frameCount)
    {
        // [channel][frame] -> float[height, width]
        var snapshot = new float[channelCount][][,];
        for (var ch = 0; ch < channelCount; ch++)
        {
            snapshot[ch] = new float[frameCount][,];
            for (var f = 0; f < frameCount; f++)
            {
                snapshot[ch][f] = frames[f].GetChannelArray(ch);
            }
        }
        return snapshot;
    }

    private static (float[][] Min, float[][] Scale) ComputeNormalizationScalars(
        IReadOnlyList<Image> frames, float target, int frameCount, int channelCount)
    {
        // Shape: [channel][frame] -> scalar.
        var min = new float[channelCount][];
        var scale = new float[channelCount][];
        for (var ch = 0; ch < channelCount; ch++)
        {
            min[ch] = new float[frameCount];
            scale[ch] = new float[frameCount];
        }

        for (var f = 0; f < frameCount; f++)
        {
            var stats = Normalizer.ComputeStats(frames[f]);
            for (var ch = 0; ch < channelCount; ch++)
            {
                var floor = stats.PerChannelFloor[ch];
                min[ch][f] = floor;
                scale[ch][f] = Normalizer.ComputeScale(stats.PerChannelMedian[ch], floor, target);
            }
        }
        return (min, scale);
    }
}
