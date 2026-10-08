using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Geometry;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Orchestrates a planetary lucky-imaging stack: grade -> select the sharpest N% -> align each to the
/// best frame -> integrate -> (for a Bayer source) merge the stacked CFA sub-planes and demosaic once.
/// <see cref="StackGlobalAsync"/> uses whole-disk translation only (the cheap path, also the live path);
/// <see cref="StackAsync"/> adds feature-driven alignment points + a per-AP mesh warp + per-AP "best-of"
/// weighting (the full lucky-imaging path). Phase 7 adds wavelet sharpening on top of the master.
/// </summary>
public sealed class LuckyImagingStacker
{
    /// <summary>
    /// Stacks with whole-disk global alignment and a quality-weighted mean (no alignment points). The
    /// cheap path, and the one the live rolling-window stacker reuses.
    /// </summary>
    public async Task<PlanetaryStackResult> StackGlobalAsync(IPlanetaryFrameStream stream, PlanetaryStackOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PlanetaryStackOptions();
        var ctx = await PrepareAsync(stream, options, includeAlignmentPoints: false, cancellationToken).ConfigureAwait(false);
        var channelAccum = Image.CreateChannelData(ctx.Channels, ctx.Height, ctx.Width);
        var weightAccum = new float[ctx.Height, ctx.Width];
        var coverage = options.CropToCoverage ? new PlanetaryCoverage(ctx.Width, ctx.Height) : null;

        var used = ctx.Derotator is { } derotator
            ? await AccumulateDerotatedAsync(stream, ctx.Selected, derotator, index => ctx.ScoreByIndex[index], channelAccum, weightAccum, coverage, options, cancellationToken).ConfigureAwait(false)
            : await AccumulateGlobalAsync(stream, ctx.Selected, ctx.Aligner, index => ctx.ScoreByIndex[index], channelAccum, weightAccum, coverage, options.Interpolation, cancellationToken).ConfigureAwait(false);

        var stacked = Normalize(channelAccum, weightAccum, ctx);
        var covered = coverage is { } reached ? PlanetaryMaster.CoveredRectangle(reached.Plane()) : PixelRect.Empty;
        var (master, alignment, cropped) = await FinalizeAsync(stacked, stream.Layout, options, ctx.Derotator?.Epoch.Utc, covered, cancellationToken).ConfigureAwait(false);
        return new PlanetaryStackResult(master, ctx.ReferenceIndex, used, ctx.Grades.Length)
        {
            Epoch = ctx.Derotator?.Epoch.Utc, North = ctx.North, NorthUnread = ctx.NorthUnread, TurnPx = ctx.TurnPx, ChannelAlignment = alignment, FramesCut = FramesLeftOutAsCut(ctx.Grades), FramesCutKept = FramesKeptThoughCut(ctx.Grades), FramesSmeared = FramesLeftOutAsSmeared(ctx.Grades), FramesDim = FramesLeftOutAsDim(ctx.Grades), Cropped = cropped,
            AlignmentPoints = ctx.Matcher?.AlignmentPoints.Length ?? 0,
            AlignmentPointCandidates = ctx.AlignmentPointCandidates,
        };
    }

    /// <summary>
    /// Stacks exactly <paramref name="frames"/>, equally weighted and globally aligned to frame <paramref name="referenceIndex"/>'s
    /// disk, into the stream's OWN planes: a split Bayer stack stays its four CFA sub-planes, never demosaiced. It is what the
    /// split-half test compares (docs/plans/planetary-restoration.md, T2), two such stacks from disjoint frames against one
    /// reference, and a demosaic would put the same interpolated detail into both halves.
    /// </summary>
    public async Task<Image> StackPlanesAsync(IPlanetaryFrameStream stream, ImmutableArray<int> frames, int referenceIndex, CancellationToken cancellationToken = default)
        => await StackPlanesAsync(stream, frames, referenceIndex, whiten: true, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// <see cref="StackPlanesAsync(IPlanetaryFrameStream, ImmutableArray{int}, int, CancellationToken)"/>, aligned by phase
    /// correlation or, not <paramref name="whiten"/>ed, by a plain cross-correlation (<see cref="PlanetaryStackOptions.WhitenedCorrelation"/>).
    /// </summary>
    public async Task<Image> StackPlanesAsync(IPlanetaryFrameStream stream, ImmutableArray<int> frames, int referenceIndex, bool whiten, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reference = await stream.LoadAsync(referenceIndex, cancellationToken).ConfigureAwait(false);
        GlobalAligner aligner;
        int width, height, channels;
        ImageMeta meta;
        try
        {
            aligner = AlignerFor(reference, PlanetaryDisk.BoundingBox(reference), alignTileSize: 0, whiten);
            (width, height, channels, meta) = (reference.Width, reference.Height, reference.ChannelCount, reference.ImageMeta);
        }
        finally
        {
            reference.Release();
        }

        var channelAccum = Image.CreateChannelData(channels, height, width);
        var weightAccum = new float[height, width];
        await AccumulateGlobalAsync(stream, frames, aligner, _ => 1f, channelAccum, weightAccum, null, WarpInterpolation.Bilinear, cancellationToken).ConfigureAwait(false);
        return PlanetaryMaster.NormalizeInPlace(channelAccum, weightAccum, meta);
    }

    // The global path's integration, shared: each frame aligned to the reference's disk and added with its weight (a frame
    // weighted zero or less is skipped). Returns how many were added. The shifts are estimated a batch of frames side by side,
    // each slot on an aligner twin of its own, and the frames added in the order given (PlanetaryFrameBatches).
    private static async Task<int> AccumulateGlobalAsync(IPlanetaryFrameStream stream, ImmutableArray<int> frames, GlobalAligner aligner, Func<int, float> weightOf,
        float[][,] channelAccum, float[,] weightAccum, PlanetaryCoverage? coverage, WarpInterpolation interpolation, CancellationToken cancellationToken)
    {
        var weighted = Weighted(frames, weightOf);
        var aligners = new GlobalAligner?[PlanetaryFrameBatches.MaxSlots];
        await PlanetaryFrameBatches.RunAsync(stream, weighted,
            (frame, _, slot) => (aligners[slot] ??= aligner.Twin()).Estimate(frame, PlanetaryDisk.BoundingBox(frame)),
            (frame, index, shift) =>
            {
                frame.AccumulateTranslatedInto(channelAccum, weightAccum, (float)shift.Dx, (float)shift.Dy, weightOf(index), interpolation);
                coverage?.Add(shift.Dx, shift.Dy, frame.Width, frame.Height, weightOf(index));
            },
            cancellationToken).ConfigureAwait(false);
        return weighted.Length;
    }

    // The frames a stack adds, in the order given: those weighted above zero.
    private static ImmutableArray<int> Weighted(ImmutableArray<int> frames, Func<int, float> weightOf)
    {
        var weighted = ImmutableArray.CreateBuilder<int>(frames.Length);
        foreach (var index in frames)
        {
            if (weightOf(index) > 0f)
            {
                weighted.Add(index);
            }
        }
        return weighted.ToImmutable();
    }

    // The global path's integration for frames carried to one epoch (R6 part 2): each frame registered onto the stack's disk
    // against the reference turned to its instant, and resampled through its de-rotation beneath that shift, a mesh with no
    // points, relit as it lands. In capture order, so one turned reference serves a run of frames. Returns how many were added.
    private static async Task<int> AccumulateDerotatedAsync(IPlanetaryFrameStream stream, ImmutableArray<int> frames, FrameDerotator derotator, Func<int, float> weightOf,
        float[][,] channelAccum, float[,] weightAccum, PlanetaryCoverage? coverage, PlanetaryStackOptions options, CancellationToken cancellationToken)
    {
        var (height, width) = (weightAccum.GetLength(0), weightAccum.GetLength(1));
        var used = 0;
        foreach (var index in InCaptureOrder(frames))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var weight = weightOf(index);
            if (weight <= 0f)
            {
                continue;
            }

            var frame = await stream.LoadAsync(index, cancellationToken).ConfigureAwait(false);
            try
            {
                var shift = derotator.Shift(frame, index);
                var mesh = DisplacementMesh.Build(width, height, (float)shift.Dx, (float)shift.Dy, [], derotator.FieldFor(index), options.MeshNodeSpacing, options.MeshInfluence);
                frame.AccumulateByMeshInto(channelAccum, weightAccum, mesh, weight, options.Interpolation);
                coverage?.Add(shift.Dx, shift.Dy, frame.Width, frame.Height, weight);
                used++;
            }
            finally
            {
                frame.Release();
            }
        }
        return used;
    }

    // The frames a de-rotated stack visits, in capture order: the order its turned references are made in.
    private static ImmutableArray<int> InCaptureOrder(ImmutableArray<int> frames)
    {
        var sorted = frames.ToBuilder();
        sorted.Sort();
        return sorted.ToImmutable();
    }

    // The global aligner on a reference's disk, its tile auto-sized to the disk (clamped to [64, 512]) unless one is set.
    internal static GlobalAligner AlignerFor(Image reference, PixelRect refRegion, int alignTileSize, bool whiten = true)
    {
        var tileSize = alignTileSize > 0
            ? NextPowerOfTwo(alignTileSize)
            : Math.Clamp(NextPowerOfTwo(Math.Max(refRegion.Width, refRegion.Height)), 64, 512);
        return GlobalAligner.FromReference(reference, refRegion, tileSize, whiten);
    }

    /// <summary>
    /// Stacks with feature-driven alignment points: each frame is globally pre-aligned, its per-AP
    /// displacement mesh built and applied to every channel, and folded in with per-AP "best-of"
    /// weighting (each pixel drawn more from frames locally sharp there) when
    /// <see cref="PlanetaryStackOptions.PerPointQualityWeighting"/> is set. The full lucky-imaging path.
    /// </summary>
    public async Task<PlanetaryStackResult> StackAsync(IPlanetaryFrameStream stream, PlanetaryStackOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PlanetaryStackOptions();
        if (options.Derotation is not null && (options.WarpPoolFrames > 0 || options.MedianGeometry))
        {
            throw new InvalidOperationException(
                "A de-rotated stack matches each frame's points where the planet's rotation put them; pooled points and the median geometry read every frame's points unrotated, so they do not combine with it.");
        }
        var ctx = await PrepareAsync(stream, options, includeAlignmentPoints: true, cancellationToken).ConfigureAwait(false);
        var channelAccum = Image.CreateChannelData(ctx.Channels, ctx.Height, ctx.Width);
        var weightAccum = new float[ctx.Height, ctx.Width];
        var coverage = options.CropToCoverage ? new PlanetaryCoverage(ctx.Width, ctx.Height) : null;
        // PrepareAsync with includeAlignmentPoints always produces one; said as a check rather
        // than an assertion, so a future change to that contract fails here by name.
        var matcher = ctx.Matcher
            ?? throw new InvalidOperationException(
                "PrepareAsync(includeAlignmentPoints: true) must produce an alignment-point matcher.");

        // Pooled or on the median geometry, every frame's points are read first, in capture order.
        var tracks = options.WarpPoolFrames > 0 || options.MedianGeometry
            ? await AlignmentPointTracks.MeasureAsync(stream, ctx.Aligner, matcher, cancellationToken).ConfigureAwait(false)
            : null;
        var points = new AlignmentPointShift[matcher.AlignmentPoints.Length];

        // The master's two halves (#1313), folded beside it when asked for.
        var halves = options.Halves ? new HalfStacks(ctx) : null;

        // Each point keeping its own best frames (#1350): every frame the global keep selected is a candidate.
        PlanetaryPointKeep? pointKeep = null;
        if (options.PointKeep is { } share)
        {
            if (tracks is not null)
            {
                throw new InvalidOperationException(
                    "A point keep scores each frame through its own mesh as it is folded; pooled points and the median geometry read every frame's points first, so they do not combine with it.");
            }
            pointKeep = new PlanetaryPointKeep(matcher.AlignmentPoints, ctx.PointReferences, Weighted(ctx.Selected, index => ctx.ScoreByIndex[index]),
                share, ctx.Width, ctx.Height, options.AlignmentPointSpacing);
        }

        // A frame folded through its mesh, best-of weighted by its own sharpness map when one was made, into the master and into its half.
        void Fold(Image frame, int index, DisplacementMesh mesh, float[,]? quality, float weight)
        {
            var pixelShare = pointKeep?.WeightFor(index);
            FoldInto(channelAccum, weightAccum);
            if (halves is not null)
            {
                var (channels, weights) = halves.For(index);
                FoldInto(channels, weights);
            }
            coverage?.Add(mesh, frame.Width, frame.Height, weight);

            void FoldInto(float[][,] channels, float[,] weights)
            {
                if (quality is not null)
                {
                    frame.AccumulateByMeshWeightedInto(channels, weights, mesh, quality, weight, ctx.SignalConfidence, options.Interpolation, pixelShare);
                }
                else
                {
                    frame.AccumulateByMeshInto(channels, weights, mesh, weight, options.Interpolation, pixelShare);
                }
            }
        }

        // The walk a de-rotated or a pooled stack keeps, a frame at a time in its own order: the de-rotator turns its reference
        // along a run of frames in capture order, and the pooled points were read for every frame up front.
        async Task<int> WalkAsync(ImmutableArray<int> order, Func<Image, int, DisplacementMesh> meshOf)
        {
            var walked = 0;
            foreach (var index in order)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var weight = ctx.ScoreByIndex[index];
                if (weight <= 0f)
                {
                    continue;
                }

                var frame = await stream.LoadAsync(index, cancellationToken).ConfigureAwait(false);
                try
                {
                    Fold(frame, index, meshOf(frame, index), options.PerPointQualityWeighting ? FrameSharpnessMap.Build(frame) : null, weight);
                    walked++;
                }
                finally
                {
                    frame.Release();
                }
            }
            return walked;
        }

        int used;
        if (ctx.Derotator is { } derotator)
        {
            // Each point matched where the rotation and the shift put it, over the frame's de-rotation (R6 part 2).
            DisplacementMesh DerotatedMesh(Image frame, int index)
            {
                var shift = derotator.Shift(frame, index);
                return matcher.BuildMesh(frame, (float)shift.Dx, (float)shift.Dy, derotator.FieldFor(index), options.MeshNodeSpacing, options.MeshInfluence, options.MeshGain);
            }
            var order = InCaptureOrder(ctx.Selected);
            if (pointKeep is not null)
            {
                // Every candidate scored at every point through its own mesh, a frame at a time in capture order as the fold walks.
                foreach (var index in order)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ctx.ScoreByIndex[index] <= 0f)
                    {
                        continue;
                    }
                    var frame = await stream.LoadAsync(index, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        pointKeep.Record(index, pointKeep.Score(frame, DerotatedMesh(frame, index)));
                    }
                    finally
                    {
                        frame.Release();
                    }
                }
                pointKeep.Decide();
                order = [.. order.Where(index => ctx.ScoreByIndex[index] > 0f && pointKeep.Folds(index))];
            }
            used = await WalkAsync(order, DerotatedMesh).ConfigureAwait(false);
        }
        else if (tracks is not null)
        {
            used = await WalkAsync(ctx.Selected, (_, index) =>
            {
                var (gx, gy) = tracks.GlobalShift(index);
                tracks.Points(index, options.WarpPoolFrames, options.MedianGeometry, points);
                return matcher.BuildMesh((float)gx, (float)gy, points, options.MeshNodeSpacing, options.MeshInfluence, options.MeshGain);
            }).ConfigureAwait(false);
        }
        else
        {
            // The common path, a batch of frames side by side (PlanetaryFrameBatches): each frame's shift, mesh and sharpness map
            // made in a slot of its own, on aligner and matcher twins, then folded in selection order, so the master is the one a
            // frame-by-frame walk makes, bit for bit.
            var weighted = Weighted(ctx.Selected, index => ctx.ScoreByIndex[index]);
            var aligners = new GlobalAligner?[PlanetaryFrameBatches.MaxSlots];
            var matchers = new AlignmentPointMatcher?[PlanetaryFrameBatches.MaxSlots];
            if (pointKeep is not null)
            {
                // Every candidate scored at every point through the mesh the fold will build for it, side by side as the fold runs.
                await PlanetaryFrameBatches.RunAsync(stream, weighted,
                    (frame, _, slot) =>
                    {
                        var shift = (aligners[slot] ??= ctx.Aligner.Twin()).Estimate(frame, PlanetaryDisk.BoundingBox(frame));
                        var mesh = (matchers[slot] ??= matcher.Twin()).BuildMesh(frame, (float)shift.Dx, (float)shift.Dy, options.MeshNodeSpacing, options.MeshInfluence, options.MeshGain);
                        return pointKeep.Score(frame, mesh);
                    },
                    (_, index, scores) => pointKeep.Record(index, scores),
                    cancellationToken).ConfigureAwait(false);
                pointKeep.Decide();
                weighted = [.. weighted.Where(pointKeep.Folds)];
            }
            await PlanetaryFrameBatches.RunAsync(stream, weighted,
                (frame, _, slot) =>
                {
                    var shift = (aligners[slot] ??= ctx.Aligner.Twin()).Estimate(frame, PlanetaryDisk.BoundingBox(frame));
                    var mesh = (matchers[slot] ??= matcher.Twin()).BuildMesh(frame, (float)shift.Dx, (float)shift.Dy, options.MeshNodeSpacing, options.MeshInfluence, options.MeshGain);
                    return (Mesh: mesh, Quality: options.PerPointQualityWeighting ? FrameSharpnessMap.Build(frame) : null);
                },
                (frame, index, prepared) => Fold(frame, index, prepared.Mesh, prepared.Quality, ctx.ScoreByIndex[index]),
                cancellationToken).ConfigureAwait(false);
            used = weighted.Length;
        }

        var stacked = Normalize(channelAccum, weightAccum, ctx);
        var covered = coverage is { } reached ? PlanetaryMaster.CoveredRectangle(reached.Plane()) : PixelRect.Empty;
        var (master, alignment, cropped) = await FinalizeAsync(stacked, stream.Layout, options, ctx.Derotator?.Epoch.Utc, covered, cancellationToken).ConfigureAwait(false);
        var finishedHalves = halves is null ? null : await halves.FinishAsync(ctx, stream.Layout, alignment, cropped, cancellationToken).ConfigureAwait(false);
        return new PlanetaryStackResult(master, ctx.ReferenceIndex, used, ctx.Grades.Length)
        {
            Halves = finishedHalves,
            Epoch = ctx.Derotator?.Epoch.Utc, North = ctx.North, NorthUnread = ctx.NorthUnread, TurnPx = ctx.TurnPx, ChannelAlignment = alignment, FramesCut = FramesLeftOutAsCut(ctx.Grades), FramesCutKept = FramesKeptThoughCut(ctx.Grades), FramesSmeared = FramesLeftOutAsSmeared(ctx.Grades), FramesDim = FramesLeftOutAsDim(ctx.Grades), Cropped = cropped,
            AlignmentPoints = ctx.Matcher?.AlignmentPoints.Length ?? 0,
            AlignmentPointCandidates = ctx.AlignmentPointCandidates,
            PointKeepCandidates = pointKeep?.Candidates ?? 0,
            PointKeptEach = pointKeep?.KeptEach ?? 0,
        };
    }

    /// <summary>
    /// Stacks a Bayer (split-CFA) source by <b>Bayer drizzle</b> (Phase 6): each selected frame is
    /// whole-disk globally aligned, its raw CFA mosaic forward-scattered onto an upscaled output grid via
    /// the shared <see cref="DrizzleKernel"/> (each sample lands only in its own R/G/B channel -- no
    /// interpolation, no demosaic), and the per-channel flux divided by coverage. Avoids the bilinear-warp
    /// softening that caps the mesh path and recovers sub-Bayer resolution when <see cref="PlanetaryDrizzleOptions.Scale"/>
    /// &gt; 1. By default (<see cref="PlanetaryDrizzleOptions.AlignmentPointMesh"/>) each raw sample is
    /// forward-scattered through the per-AP displacement mesh, so drizzle gets the same local seeing de-warp
    /// as the mesh integrator on top of its sub-Bayer resolution; with the mesh off it falls back to a
    /// whole-disk global translation (drizzle's per-frame sub-pixel diversity still fills each colour grid).
    /// </summary>
    public async Task<PlanetaryStackResult> StackDrizzleAsync(IPlanetaryFrameStream stream, PlanetaryStackOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PlanetaryStackOptions();
        var drizzle = options.Drizzle ?? new PlanetaryDrizzleOptions();
        if (stream.Layout != PlanetaryFrameLayout.SplitCfa)
        {
            throw new InvalidOperationException($"Bayer drizzle requires a Bayer (split-CFA) source; got layout {stream.Layout}.");
        }

        var ctx = await PrepareAsync(stream, options, includeAlignmentPoints: drizzle.AlignmentPointMesh, cancellationToken).ConfigureAwait(false);

        // ctx.Width/Height are the half-res CFA sub-plane dims; the mosaic (and thus the drizzle canvas) is
        // twice that, scaled by the requested output scale.
        var scale = drizzle.Scale;
        var mosaicW = ctx.Width * 2;
        var mosaicH = ctx.Height * 2;
        var canvasW = Math.Max(1, (int)MathF.Round(mosaicW * scale));
        var canvasH = Math.Max(1, (int)MathF.Round(mosaicH * scale));

        var flux = Image.CreateChannelData(3, canvasH, canvasW);
        var weight = Image.CreateChannelData(3, canvasH, canvasW);
        var coverage = options.CropToCoverage ? new PlanetaryCoverage(canvasW, canvasH) : null;
        // Drop half-extent in OUTPUT pixels. The drop is pixfrac of an INPUT pixel, which is `scale` output
        // pixels wide -- so the output-space half-extent is pixfrac*scale/2. (The deep-sky DrizzleKernel
        // hardcodes pixfrac/2 because it only runs at scale=1; at scale>1 that under-sizes the drop and
        // leaves periodic gaps between drops -- a visible dot-grid pattern.)
        var halfP = drizzle.Pixfrac * scale * 0.5f;
        var pattern = ctx.MasterMeta.SensorType.GetBayerPatternMatrix(ctx.MasterMeta.BayerOffsetX, ctx.MasterMeta.BayerOffsetY);

        var used = 0;
        // One mosaic plane for the whole stack, merged into per frame (every sample is overwritten): a new
        // full-size plane per frame was garbage the drizzle read once. Returned when the stack is done.
        using var mosaicPlane = Array2DPool<float>.RentScoped(mosaicH, mosaicW);
        foreach (var index in ctx.Derotator is null ? ctx.Selected : InCaptureOrder(ctx.Selected))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ctx.ScoreByIndex[index] <= 0f)
            {
                continue;
            }

            var frame = await stream.LoadAsync(index, cancellationToken).ConfigureAwait(false);
            try
            {
                var shift = ctx.Derotator?.Shift(frame, index) ?? ctx.Aligner.Estimate(frame, PlanetaryDisk.BoundingBox(frame));
                // The mosaic lands at (mosaic - 2 x the sub-plane shift) x scale on the canvas, its mesh bending it only near the points.
                coverage?.Add(-2.0 * shift.Dx * scale, -2.0 * shift.Dy * scale, (mosaicW - (2.0 * shift.Dx)) * scale, (mosaicH - (2.0 * shift.Dy)) * scale, ctx.ScoreByIndex[index]);
                var derotation = ctx.Derotator?.FieldFor(index);
                var mosaic = frame.MergeBayerChannelsInto(mosaicPlane.Array);
                var sourceRect = new PixelRect(0, 0, mosaic.Width, mosaic.Height);
                if (ctx.Matcher is not null || derotation is not null)
                {
                    // AP-mesh drizzle: forward-scatter each raw sample through the per-AP displacement mesh.
                    // The mesh is built at sub-plane resolution; MeshSourceToCanvas samples it at the
                    // mosaic pixel's sub-plane position, doubles the offset into mosaic space, and applies
                    // the output scale -- so drizzle gets the local de-warp, not just a whole-disk shift.
                    // A de-rotated frame (R6 part 2) goes the same way with or without points, its field beneath
                    // the mesh, and each raw sample relit before it is scattered.
                    var mesh = ctx.Matcher is { } matcher
                        ? matcher.BuildMesh(frame, (float)shift.Dx, (float)shift.Dy, derotation, options.MeshNodeSpacing, options.MeshInfluence, options.MeshGain)
                        : DisplacementMesh.Build(ctx.Width, ctx.Height, (float)shift.Dx, (float)shift.Dy, [], derotation, options.MeshNodeSpacing, options.MeshInfluence);
                    var map = new MeshSourceToCanvas(mesh, scale);
                    if (derotation is not null)
                    {
                        map.RelightInPlace(mosaicPlane.Array);
                    }
                    DrizzleKernel.IterateAndDeposit(
                        mosaic, map, pattern, halfP, flux, weight,
                        xStart: 0, xEnd: canvasW, yStart: 0, yEnd: canvasH,
                        sourceRect, badPixelMask: default, hasBadPixelMask: false);
                }
                else
                {
                    // Whole-disk drizzle: canvas = (mosaic - 2 * sub-plane shift) * scale (the aligner works
                    // at sub-plane resolution). DrizzleKernel applies p * transform.
                    var transform = Matrix3x2.CreateTranslation((float)(-2.0 * shift.Dx), (float)(-2.0 * shift.Dy))
                        * Matrix3x2.CreateScale(scale);
                    DrizzleKernel.IterateAndDeposit(
                        mosaic, transform, pattern, halfP, flux, weight,
                        xStart: 0, xEnd: canvasW, yStart: 0, yEnd: canvasH,
                        sourceRect, badPixelMask: default, hasBadPixelMask: false);
                }

                used++;
            }
            finally
            {
                frame.Release();
            }
        }

        DrizzleKernel.FinaliseDivide(flux, weight, invMaxValue: 1f, canvasH, canvasW);
        // Uncovered cells come back NaN; planetary masters want a solid background, so floor them to 0
        // (also keeps a NaN out of the optional wavelet pass).
        for (var c = 0; c < 3; c++)
        {
            var plane = flux[c];
            for (var y = 0; y < canvasH; y++)
            {
                for (var x = 0; x < canvasW; x++)
                {
                    if (float.IsNaN(plane[y, x]))
                    {
                        plane[y, x] = 0f;
                    }
                }
            }
        }

        var masterMeta = ctx.MasterMeta with { SensorType = SensorType.Color };
        var master = new Image(flux, BitDepth.Float32, 1f, 0f, 0f, masterMeta);
        // Each sample landed at its own photosite in its own colour, so the colours are aligned as the three planes they are.
        PlanetaryChannelAlignmentResult? alignment = null;
        if (options.AlignChannels)
        {
            (master, alignment) = PlanetaryChannelAlignment.Align(master, PlanetaryFrameLayout.Rgb, LimbOptionsFor(master, options, ctx.Derotator?.Epoch.Utc));
        }
        var cropped = PixelRect.Empty;
        if (coverage is { } reached)
        {
            (master, cropped) = PlanetaryMaster.CropToCovered(master, PlanetaryMaster.CoveredRectangle(reached.Plane()), 1);
        }
        if (options.Sharpen is { } sharpen)
        {
            master = WaveletSharpen.Sharpen(master, sharpen);
        }

        return new PlanetaryStackResult(master, ctx.ReferenceIndex, used, ctx.Grades.Length)
        {
            Epoch = ctx.Derotator?.Epoch.Utc, North = ctx.North, NorthUnread = ctx.NorthUnread, TurnPx = ctx.TurnPx, ChannelAlignment = alignment, FramesCut = FramesLeftOutAsCut(ctx.Grades), FramesCutKept = FramesKeptThoughCut(ctx.Grades), FramesSmeared = FramesLeftOutAsSmeared(ctx.Grades), FramesDim = FramesLeftOutAsDim(ctx.Grades), Cropped = cropped,
            AlignmentPoints = ctx.Matcher?.AlignmentPoints.Length ?? 0,
            AlignmentPointCandidates = ctx.AlignmentPointCandidates,
        };
    }

    /// <summary>
    /// Source(mosaic) -&gt; drizzle-canvas map driven by the per-frame AP displacement mesh. The mesh is
    /// built at split-CFA sub-plane resolution, so a mosaic pixel (2x the sub-plane grid) samples the mesh
    /// at half its coordinate and the returned sub-plane offset is doubled into mosaic space. The reference
    /// mosaic position is <c>mosaic - 2*offset</c> (the mesh offset already folds in the global shift), and
    /// the canvas position is that scaled by the output <c>scale</c> -- the per-pixel generalisation of the
    /// whole-disk affine <c>(mosaic - 2*shift) * scale</c>.
    /// <para>
    /// The mesh is the stack's pixel to the frame's, sampled here at the frame's photosite as if that were the stack's pixel,
    /// which a shift and a seeing warp that varies over tens of pixels allow. A de-rotation varies by a pixel or two across a
    /// disk, so over one the offset is sampled again where the first answer lands: one step of the fixed point
    /// <c>c = s - offset(c)</c>, where the stack's pixel <c>c</c> reads the photosite <c>s</c>.
    /// </para>
    /// </summary>
    private readonly struct MeshSourceToCanvas(DisplacementMesh mesh, float scale) : ISourceToCanvas
    {
        public Vector2 Map(int xSrc, int ySrc)
        {
            var (offX, offY) = Offset(xSrc, ySrc);
            return new Vector2((xSrc - (2f * offX)) * scale, (ySrc - (2f * offY)) * scale);
        }

        // Each raw sample of a de-rotated frame multiplied by the relight at the stack pixel it lands on.
        public void RelightInPlace(float[,] mosaic)
        {
            var (height, width) = (mosaic.GetLength(0), mosaic.GetLength(1));
            var (self, field) = (this, mesh);
            ParallelFor.Run(height, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    var (offX, offY) = self.Offset(x, y);
                    mosaic[y, x] *= field.RelightAt((x * 0.5f) - offX, (y * 0.5f) - offY);
                }
            });
        }

        private (float OffsetX, float OffsetY) Offset(int xSrc, int ySrc)
        {
            var (x, y) = (xSrc * 0.5f, ySrc * 0.5f);
            var (offX, offY) = mesh.Sample(x, y);
            return mesh.Derotation is null ? (offX, offY) : mesh.Sample(x - offX, y - offY);
        }
    }

    /// <summary>
    /// Every frame's alignment points read as <see cref="StackAsync"/> reads them, with the same reference, points, aligner and
    /// matcher, the reference's index and the matcher itself: what a dewarp's residual is measured from (<see cref="DewarpResidual"/>).
    /// </summary>
    internal static async Task<(AlignmentPointTracks Tracks, int ReferenceIndex, AlignmentPointMatcher Matcher)> TrackAsync(IPlanetaryFrameStream stream,
        PlanetaryStackOptions options, CancellationToken cancellationToken)
    {
        var ctx = await PrepareAsync(stream, options, includeAlignmentPoints: true, cancellationToken).ConfigureAwait(false);
        var matcher = ctx.Matcher
            ?? throw new InvalidOperationException("PrepareAsync(includeAlignmentPoints: true) must produce an alignment-point matcher.");
        return (await AlignmentPointTracks.MeasureAsync(stream, ctx.Aligner, matcher, cancellationToken).ConfigureAwait(false), ctx.ReferenceIndex, matcher);
    }

    // The frames the grader left out because the frame's edge or a line inside it cut their planet or they held none
    // (FrameGrader.WithoutCutSmearedOrDimFrames).
    private static int FramesLeftOutAsCut(ImmutableArray<FrameGrade> grades) => FrameGrader.CutFrames(grades).LeftOut;

    // The frames read as cut that were kept, since too few of the capture's frames were whole to stack without them (#1307).
    private static int FramesKeptThoughCut(ImmutableArray<FrameGrade> grades) => FrameGrader.CutFrames(grades).Kept;

    // The frames the grader left out because their planet was dim (FrameGrader.DimRatio, #1307).
    private static int FramesLeftOutAsDim(ImmutableArray<FrameGrade> grades)
    {
        var count = 0;
        foreach (var grade in grades)
        {
            count += grade.Dim ? 1 : 0;
        }
        return count;
    }

    // The frames the grader left out because the telescope's motion smeared their planet (FrameGrader.SmearRatio, #1300).
    private static int FramesLeftOutAsSmeared(ImmutableArray<FrameGrade> grades)
    {
        var count = 0;
        foreach (var grade in grades)
        {
            count += grade.Smeared ? 1 : 0;
        }
        return count;
    }

    private sealed record StackContext(
        ImmutableArray<FrameGrade> Grades,
        int ReferenceIndex,
        ImmutableArray<int> Selected,
        float[] ScoreByIndex,
        GlobalAligner Aligner,
        AlignmentPointMatcher? Matcher,
        float[,]? SignalConfidence,
        int Width,
        int Height,
        int Channels,
        ImageMeta MasterMeta,
        FrameDerotator? Derotator,
        PlanetaryNorthDecision? North,
        bool NorthUnread,
        double? TurnPx,
        int AlignmentPointCandidates,
        ImmutableArray<float[]> PointReferences);

    private static async Task<StackContext> PrepareAsync(IPlanetaryFrameStream stream, PlanetaryStackOptions options, bool includeAlignmentPoints, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.FrameCount <= 0)
        {
            throw new InvalidOperationException("The frame stream is empty.");
        }

        var grader = new FrameGrader(options.QualityEstimator);
        var grades = await grader.GradeAllAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var referenceIndex = FrameGrader.Reference(grades);
        var selected = FrameGrader.SelectBest(grades, options.KeepFraction);

        var scoreByIndex = new float[stream.FrameCount];
        foreach (var g in grades)
        {
            scoreByIndex[g.Index] = MathF.Max(0f, g.Score);
        }

        var reference = await stream.LoadAsync(referenceIndex, cancellationToken).ConfigureAwait(false);
        FrameDerotator? derotator = null;
        PlanetaryNorthDecision? north = null;
        var northUnread = false;
        // How far the planet turns over the capture, at its disk's middle: a capture that turns it less than the options ask is
        // stacked as taken (a NaN, no frame times, never reaches a positive least turn).
        double? turnPx = options.Derotation is { } asked ? FrameDerotator.TurnAtCentrePx(stream, asked.Planet, reference) : null;
        if (options.Derotation is { } derotation && (derotation.MinimumTurnPx <= 0 || turnPx >= derotation.MinimumTurnPx))
        {
            // Carried to one epoch (R6 part 2), the stack's disk is the best frame's and its reference the best frame at the
            // epoch, or a stack of the best frames each carried there. Its north is the one the capture agrees with. The
            // derotator keeps the reference it registers against, so the frame is released here; a run whose north cannot be read
            // keeps the frame as its reference.
            try
            {
                // A run whose quarters cannot tell its north (no frames in one, or a turn under
                // PlanetaryDerotation.LeastTurnToTellNorthDeg) is stacked as taken and says so (#1292), but for a de-rotation asked for
                // whatever the turn (MinimumTurnPx 0), which keeps the limb fit's north.
                (derotator, north) = await DecideNorthAsync(stream, grades, referenceIndex, reference, derotation, options,
                    keepFitUnread: derotation.MinimumTurnPx <= 0, cancellationToken).ConfigureAwait(false);
                northUnread = derotator is null;
                derotator?.UseTemplate(derotator.ToEpoch(reference, derotator.AspectOf(referenceIndex)));
            }
            finally
            {
                if (!northUnread)
                {
                    reference.Release();
                }
            }
            if (derotator is not null)
            {
                if (options.ReferenceFrames > 1)
                {
                    derotator.UseTemplate(await StackedReferenceAsync(stream, grades, derotator, options, cancellationToken).ConfigureAwait(false));
                }
                reference = derotator.Template;
            }
        }
        if (derotator is null && options.ReferenceFrames > 1)
        {
            reference = await StackedReferenceAsync(stream, grades, reference, options, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            var refRegion = PlanetaryDisk.BoundingBox(reference);
            var aligner = AlignerFor(reference, refRegion, options.AlignTileSize, options.WhitenedCorrelation);

            AlignmentPointMatcher? matcher = null;
            float[,]? signalConfidence = null;
            var (aps, apCandidates) = (ImmutableArray<PixelPoint>.Empty, 0);
            if (includeAlignmentPoints)
            {
                (aps, apCandidates) = options.PointPlacement == PlanetaryPointPlacement.OverPlanet
                    ? FeatureDetector.DetectOverPlanet(reference, options.AlignmentPointSpacing, options.MaxAlignmentPoints)
                    : FeatureDetector.DetectByPeakFraction(reference, refRegion, options.AlignmentPointSpacing, options.MaxAlignmentPoints);
                matcher = AlignmentPointMatcher.FromReference(reference, aps, options.AlignmentPatchSize, options.WhitenedCorrelation, options.PointEstimator);
                if (options.RemeasureAgainstStack && derotator is null)
                {
                    // #1081's second pass: the reference dewarped by the points it gave, and every frame matched against that.
                    var dewarped = await DewarpedReferenceAsync(stream, grades, aligner, matcher, reference, options, cancellationToken).ConfigureAwait(false);
                    reference.Release();
                    reference = dewarped;
                    refRegion = PlanetaryDisk.BoundingBox(reference);
                    aligner = AlignerFor(reference, refRegion, options.AlignTileSize, options.WhitenedCorrelation);
                    matcher = AlignmentPointMatcher.FromReference(reference, aps, options.AlignmentPatchSize, options.WhitenedCorrelation, options.PointEstimator);
                }

                // The signal-confidence gate is computed once from the reference (= the integrator's output
                // space, since frames are warped to it). Only needed when best-of weighting is on.
                if (options.PerPointQualityWeighting && options.PerPointSignalGate)
                {
                    signalConfidence = PlanetaryDisk.SignalConfidence(reference);
                }
            }

            // The master is of the whole capture, so it carries the capture's span (DATE-OBS its earliest frame, EXPTIME to its latest,
            // in whatever order the frames come, #1292), not the reference frame's: its middle is the instant a planet's aspect is read
            // at when the master is sharpened later.
            var masterMeta = stream.CaptureSpan is { } captured
                ? reference.ImageMeta with { ExposureStartTime = captured.Earliest, ExposureDuration = captured.Latest - captured.Earliest }
                : reference.ImageMeta;
            // And names its planet in OBJECT, where a viewer reads that it is a planetary frame (PlanetaryStackOptions.Planet).
            if ((options.Planet ?? options.Derotation?.Planet) is { } planet)
            {
                masterMeta = masterMeta with { ObjectName = planet.ToString() };
            }
            // Each point's patch of the stacked reference, which a point keep scores every frame's against (#1350).
            var pointReferences = options.PointKeep is not null && matcher is { } pointMatcher
                ? PlanetaryPointKeep.ReferencePatches(reference, pointMatcher.AlignmentPoints)
                : [];
            return new StackContext(grades, referenceIndex, selected, scoreByIndex, aligner, matcher, signalConfidence,
                reference.Width, reference.Height, reference.ChannelCount, masterMeta, derotator, north, northUnread, turnPx, apCandidates, pointReferences);
        }
        finally
        {
            if (derotator is null)
            {
                reference.Release();
            }
        }
    }

    // The best frames' stack, each through the mesh its points give against `reference` (PlanetaryStackOptions.RemeasureAgainstStack):
    // the reference dewarped as far as its points can. The frames are matched a batch side by side on aligner and matcher twins and
    // added in grade order, as the stack adds them. The caller owns the result.
    private static async Task<Image> DewarpedReferenceAsync(IPlanetaryFrameStream stream, ImmutableArray<FrameGrade> grades, GlobalAligner aligner,
        AlignmentPointMatcher matcher, Image reference, PlanetaryStackOptions options, CancellationToken cancellationToken)
    {
        var frames = FrameGrader.SelectBest(grades, Math.Min(1.0, (double)Math.Max(options.ReferenceFrames, 1) / grades.Length));
        var channelAccum = Image.CreateChannelData(reference.ChannelCount, reference.Height, reference.Width);
        var weightAccum = new float[reference.Height, reference.Width];
        var aligners = new GlobalAligner?[PlanetaryFrameBatches.MaxSlots];
        var matchers = new AlignmentPointMatcher?[PlanetaryFrameBatches.MaxSlots];
        await PlanetaryFrameBatches.RunAsync(stream, frames,
            (frame, _, slot) =>
            {
                var shift = (aligners[slot] ??= aligner.Twin()).Estimate(frame, PlanetaryDisk.BoundingBox(frame));
                return (matchers[slot] ??= matcher.Twin()).BuildMesh(frame, (float)shift.Dx, (float)shift.Dy, options.MeshNodeSpacing, options.MeshInfluence, options.MeshGain);
            },
            (frame, _, mesh) => frame.AccumulateByMeshInto(channelAccum, weightAccum, mesh, 1f, options.Interpolation),
            cancellationToken).ConfigureAwait(false);
        return PlanetaryMaster.NormalizeInPlace(channelAccum, weightAccum, reference.ImageMeta);
    }

    // The best frames' stack, each aligned to the best frame's disk and weighted alike (PlanetaryStackOptions.ReferenceFrames).
    // Consumes `best`, which it releases, and returns the stack, which the caller owns; the stack lies on the best frame's
    // global geometry, its local warp averaged over the frames.
    private static async Task<Image> StackedReferenceAsync(IPlanetaryFrameStream stream, ImmutableArray<FrameGrade> grades, Image best,
        PlanetaryStackOptions options, CancellationToken cancellationToken)
    {
        GlobalAligner aligner;
        int width, height, channels;
        ImageMeta meta;
        try
        {
            aligner = AlignerFor(best, PlanetaryDisk.BoundingBox(best), options.AlignTileSize, options.WhitenedCorrelation);
            (width, height, channels, meta) = (best.Width, best.Height, best.ChannelCount, best.ImageMeta);
        }
        finally
        {
            best.Release();
        }

        var frames = FrameGrader.SelectBest(grades, Math.Min(1.0, (double)options.ReferenceFrames / grades.Length));
        var channelAccum = Image.CreateChannelData(channels, height, width);
        var weightAccum = new float[height, width];
        await AccumulateGlobalAsync(stream, frames, aligner, _ => 1f, channelAccum, weightAccum, null, options.Interpolation, cancellationToken).ConfigureAwait(false);
        return PlanetaryMaster.NormalizeInPlace(channelAccum, weightAccum, meta);
    }

    /// <summary>
    /// Which way round <paramref name="stream"/>'s planet turns, read as a de-rotated stack reads it and nothing more stacked
    /// (R6 part 2): its frames graded, its disk fitted on a stack of its best, and its first and last quarters' agreement both ways
    /// round. A session's north is read so (#1347): the stream is its first and last captures, hours apart, where one capture's own
    /// quarters, minutes apart, can tie on a bland globe, and every capture of the session then takes it
    /// (<see cref="PlanetaryDerotationOptions.North"/>). Null when the stream turns the planet too little to tell, or a quarter holds no frame.
    /// </summary>
    public static async Task<PlanetaryNorthDecision?> ReadNorthAsync(IPlanetaryFrameStream stream, PlanetaryStackOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Derotation is not { } derotation)
        {
            throw new ArgumentException("A north is read for a de-rotation: the options carry none.", nameof(options));
        }
        var grades = await new FrameGrader(options.QualityEstimator).GradeAllAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var referenceIndex = FrameGrader.Reference(grades);
        var reference = await stream.LoadAsync(referenceIndex, cancellationToken).ConfigureAwait(false);
        try
        {
            var (_, north) = await DecideNorthAsync(stream, grades, referenceIndex, reference, derotation with { North = null }, options,
                keepFitUnread: false, cancellationToken).ConfigureAwait(false);
            return north;
        }
        finally
        {
            reference.Release();
        }
    }

    // The derotator a capture is stacked with and the north it took (R6 part 2). The disk is fitted on a stack of the capture's best
    // frames as they are, never on one frame: the limb does not turn with the planet, and one 8-bit frame's fit put north anywhere from
    // 260.5 to 268.2 degrees on 2024-12-15 (a stack of 150, 263.8), which tilts every frame's rotation by the difference. Which end of
    // the fit's axis is north comes from agreement, never the fit alone: the capture's first and last quarters, or a north its caller
    // decided (a session's, #1347). A null derotator when the quarters cannot tell, unless the fit's north is to be kept then.
    private static async Task<(FrameDerotator? Derotator, PlanetaryNorthDecision? North)> DecideNorthAsync(IPlanetaryFrameStream stream,
        ImmutableArray<FrameGrade> grades, int referenceIndex, Image reference, PlanetaryDerotationOptions derotation, PlanetaryStackOptions options,
        bool keepFitUnread, CancellationToken cancellationToken)
    {
        var plain = AlignerFor(reference, PlanetaryDisk.BoundingBox(reference), options.AlignTileSize, options.WhitenedCorrelation);
        var steady = stream.CaptureSpan is { } span
            ? await QuarterStackAsync(stream, grades, span.Earliest, span.Latest, plain, reference.ChannelCount, reference.Width, reference.Height, reference.ImageMeta, options, cancellationToken).ConfigureAwait(false)
            : null;
        FrameDerotator fitted;
        try
        {
            fitted = FrameDerotator.Create(stream, steady?.Stack ?? reference, referenceIndex, derotation, options.AlignTileSize, options.WhitenedCorrelation);
        }
        finally
        {
            steady?.Stack.Release();
        }
        if (derotation.North is { } given)
        {
            // The fit's axis kept, and of its two ways round the one nearer the north given (turned over too, when that is asked).
            var wanted = given + (derotation.TurnNorthOver ? 180 : 0);
            var taken = Math.Abs(Math.IEEERemainder(fitted.Placement.NorthAngleDeg - wanted, 360)) <= 90 ? fitted : fitted.TurnedOver();
            return (taken, new PlanetaryNorthDecision(taken.Placement.NorthAngleDeg, double.NaN, double.NaN) { Given = given });
        }
        var (asFitted, turned) = await AgreementBothWaysAsync(stream, grades, fitted, plain, reference.ChannelCount, reference.Width, reference.Height, reference.ImageMeta, options, cancellationToken).ConfigureAwait(false);
        if ((double.IsNaN(asFitted) || double.IsNaN(turned)) && !keepFitUnread)
        {
            return (null, null);
        }
        var derotator = (turned < asFitted) != derotation.TurnNorthOver ? fitted.TurnedOver() : fitted;
        return (derotator, new PlanetaryNorthDecision(derotator.Placement.NorthAngleDeg, asFitted, turned));
    }

    // Which way round the planet turns in this capture (R6 part 2, PlanetaryDerotation.AgreementBothWays). The best frames of the
    // capture's first and last quarters (by its frames' times, in whatever order they come, #1292) are stacked as they are, each on the
    // reference's disk, and the earlier is carried to the later's instant both ways round: the RMS apart each way, the limb fit's north
    // first. NaN for both when the capture turns the planet too little to tell, or a quarter holds no frame to stack.
    private static async Task<(double AsFitted, double TurnedOver)> AgreementBothWaysAsync(IPlanetaryFrameStream stream, ImmutableArray<FrameGrade> grades,
        FrameDerotator fitted, GlobalAligner aligner, int channels, int width, int height, ImageMeta meta, PlanetaryStackOptions options, CancellationToken cancellationToken)
    {
        if (stream.CaptureSpan is not { } span)
        {
            return (double.NaN, double.NaN);
        }
        var (first, last) = (PhysicalEphemeris.Compute(fitted.Epoch.Planet, span.Earliest), PhysicalEphemeris.Compute(fitted.Epoch.Planet, span.Latest));
        if (Math.Abs(Math.IEEERemainder(last.CentralMeridianIII - first.CentralMeridianIII, 360)) < PlanetaryDerotation.LeastTurnToTellNorthDeg)
        {
            return (double.NaN, double.NaN);
        }
        var quarter = (last.Utc - first.Utc) / 4;
        var early = await QuarterStackAsync(stream, grades, first.Utc, first.Utc + quarter, aligner, channels, width, height, meta, options, cancellationToken).ConfigureAwait(false);
        var late = await QuarterStackAsync(stream, grades, last.Utc - quarter, last.Utc, aligner, channels, width, height, meta, options, cancellationToken).ConfigureAwait(false);
        if (early is not { } a || late is not { } b)
        {
            return (double.NaN, double.NaN);
        }
        var (from, to) = (PhysicalEphemeris.Compute(fitted.Epoch.Planet, a.Time), PhysicalEphemeris.Compute(fitted.Epoch.Planet, b.Time));
        return PlanetaryDerotation.AgreementBothWays(a.Stack, from, b.Stack, to, fitted.Placement, fitted.MinnaertK);
    }

    // The best frames between two instants, stacked as they are onto the reference's disk, and their mean time; null for none. Over
    // the whole capture it is what the disk is fitted on; over a quarter, what the north is decided by.
    private static async Task<(Image Stack, DateTimeOffset Time)?> QuarterStackAsync(IPlanetaryFrameStream stream, ImmutableArray<FrameGrade> grades,
        DateTimeOffset from, DateTimeOffset to, GlobalAligner aligner, int channels, int width, int height, ImageMeta meta, PlanetaryStackOptions options, CancellationToken cancellationToken)
    {
        var inside = new List<FrameGrade>();
        foreach (var grade in grades)
        {
            if (grade.Score > 0 && stream.TimestampOf(grade.Index) is { } time && time >= from && time <= to)
            {
                inside.Add(grade);
            }
        }
        if (inside.Count == 0)
        {
            return null;
        }
        // One in a hundred of the quarter's frames, at least 5 and at most 200: enough that its stack's noise is well below the
        // rotation it is to tell, few enough that the best are sharp.
        inside.Sort((p, q) => q.Score.CompareTo(p.Score));
        var keep = Math.Min(inside.Count, Math.Clamp(inside.Count / 100, 5, 200));
        var frames = ImmutableArray.CreateBuilder<int>(keep);
        var ticks = 0.0;
        for (var i = 0; i < keep; i++)
        {
            frames.Add(inside[i].Index);
            ticks += (stream.TimestampOf(inside[i].Index) ?? from).UtcTicks;
        }
        var channelAccum = Image.CreateChannelData(channels, height, width);
        var weightAccum = new float[height, width];
        await AccumulateGlobalAsync(stream, frames.MoveToImmutable(), aligner, _ => 1f, channelAccum, weightAccum, null, options.Interpolation, cancellationToken).ConfigureAwait(false);
        return (PlanetaryMaster.NormalizeInPlace(channelAccum, weightAccum, meta), new DateTimeOffset((long)(ticks / keep), TimeSpan.Zero));
    }

    // The best frames' stack carried to the epoch (PlanetaryStackOptions.ReferenceFrames, R6 part 2): each registered against the
    // derotator's reference as it is, and carried there. Returns the stack, which the caller owns.
    private static async Task<Image> StackedReferenceAsync(IPlanetaryFrameStream stream, ImmutableArray<FrameGrade> grades, FrameDerotator derotator,
        PlanetaryStackOptions options, CancellationToken cancellationToken)
    {
        var template = derotator.Template;
        var frames = FrameGrader.SelectBest(grades, Math.Min(1.0, (double)options.ReferenceFrames / grades.Length));
        var channelAccum = Image.CreateChannelData(template.ChannelCount, template.Height, template.Width);
        var weightAccum = new float[template.Height, template.Width];
        await AccumulateDerotatedAsync(stream, frames, derotator, _ => 1f, channelAccum, weightAccum, null, options, cancellationToken).ConfigureAwait(false);
        return PlanetaryMaster.NormalizeInPlace(channelAccum, weightAccum, template.ImageMeta);
    }

    /// <summary>
    /// Every frame's global shift as <see cref="StackGlobalAsync"/> would apply it, in capture order, with the same reference
    /// (<see cref="PlanetaryStackOptions.ReferenceFrames"/> included) and aligner, and the best frame's index: sampling frame
    /// <c>f</c> at <c>(x + Dx[f], y + Dy[f])</c> lands it on the reference, so a disk that moves +1 px reads +1. What a
    /// registration is compared by (<c>tianwen planetary-registration</c>, docs/plans/planetary-restoration.md, R5 part 3).
    /// </summary>
    public static async Task<(double[] Dx, double[] Dy, int ReferenceIndex)> RegisterAllAsync(IPlanetaryFrameStream stream, PlanetaryStackOptions options,
        CancellationToken cancellationToken)
    {
        var ctx = await PrepareAsync(stream, options, includeAlignmentPoints: false, cancellationToken).ConfigureAwait(false);
        var (dx, dy) = (new double[stream.FrameCount], new double[stream.FrameCount]);
        for (var f = 0; f < stream.FrameCount; f++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = await stream.LoadAsync(f, cancellationToken).ConfigureAwait(false);
            try
            {
                var shift = ctx.Aligner.Estimate(frame, PlanetaryDisk.BoundingBox(frame));
                (dx[f], dy[f]) = (shift.Dx, shift.Dy);
            }
            finally
            {
                frame.Release();
            }
        }
        return (dx, dy, ctx.ReferenceIndex);
    }

    private static Image Normalize(float[][,] channelAccum, float[,] weightAccum, StackContext ctx)
        => PlanetaryMaster.NormalizeInPlace(channelAccum, weightAccum, ctx.MasterMeta);

    // A master's two halves (#1313): each selected frame folded a second time, into the half its rank in the selection names, so both
    // halves span the same quality; each is finished as the master was, by the master's own channel shifts and crop.
    private sealed class HalfStacks
    {
        private readonly int[] _halfOf;
        private readonly (float[][,] Channels, float[,] Weights)[] _halves;

        public HalfStacks(StackContext ctx)
        {
            _halfOf = new int[ctx.ScoreByIndex.Length];
            for (var rank = 0; rank < ctx.Selected.Length; rank++)
            {
                _halfOf[ctx.Selected[rank]] = rank & 1;
            }
            _halves =
            [
                (Image.CreateChannelData(ctx.Channels, ctx.Height, ctx.Width), new float[ctx.Height, ctx.Width]),
                (Image.CreateChannelData(ctx.Channels, ctx.Height, ctx.Width), new float[ctx.Height, ctx.Width]),
            ];
        }

        public (float[][,] Channels, float[,] Weights) For(int index) => _halves[_halfOf[index]];

        public async Task<PlanetaryStackHalves> FinishAsync(StackContext ctx, PlanetaryFrameLayout layout, PlanetaryChannelAlignmentResult? alignment,
            PixelRect cropped, CancellationToken cancellationToken)
        {
            var a = await FinishAsync(_halves[0], ctx, layout, alignment, cropped, cancellationToken).ConfigureAwait(false);
            var b = await FinishAsync(_halves[1], ctx, layout, alignment, cropped, cancellationToken).ConfigureAwait(false);
            return new PlanetaryStackHalves(a, b);
        }

        private static async Task<Image> FinishAsync((float[][,] Channels, float[,] Weights) half, StackContext ctx, PlanetaryFrameLayout layout,
            PlanetaryChannelAlignmentResult? alignment, PixelRect cropped, CancellationToken cancellationToken)
        {
            var stacked = Normalize(half.Channels, half.Weights, ctx);
            if (alignment is { Applied: true } moved)
            {
                stacked = PlanetaryChannelAlignment.Apply(stacked, layout, moved.Red, moved.Blue);
            }
            var finished = await PlanetaryMaster.MergeAndDemosaicAsync(stacked, layout, cancellationToken).ConfigureAwait(false);
            return cropped.IsEmpty ? finished : finished.Crop(cropped);
        }
    }

    /// <summary>
    /// For a split-CFA stack the integrated master is four CFA sub-planes; merge them into a full-resolution
    /// mosaic and demosaic once (MHC). Mono / RGB masters pass through unchanged. A colour master's planes are first aligned onto
    /// green (<see cref="PlanetaryStackOptions.AlignChannels"/>), a split one's as sub-planes, before the demosaic mixes them.
    /// Phase 7: when <see cref="PlanetaryStackOptions.Sharpen"/> is set, the demosaiced linear master is wavelet-sharpened.
    /// </summary>
    private static async Task<(Image Master, PlanetaryChannelAlignmentResult? Alignment, PixelRect Cropped)> FinalizeAsync(Image stacked, PlanetaryFrameLayout layout,
        PlanetaryStackOptions options, DateTimeOffset? epoch, PixelRect covered, CancellationToken cancellationToken)
    {
        PlanetaryChannelAlignmentResult? alignment = null;
        if (options.AlignChannels)
        {
            (stacked, alignment) = PlanetaryChannelAlignment.Align(stacked, layout, LimbOptionsFor(stacked, options, epoch));
        }

        var gridWidth = stacked.Width;
        var master = await PlanetaryMaster.MergeAndDemosaicAsync(stacked, layout, cancellationToken).ConfigureAwait(false);

        // Cropped once the colours lie where they belong and the demosaic has read every photosite (#1300): the rectangle is the
        // accumulator's, a split stack's sub-plane grid half the master's.
        var cropped = PixelRect.Empty;
        if (!covered.IsEmpty)
        {
            (master, cropped) = PlanetaryMaster.CropToCovered(master, covered, master.Width / gridWidth);
        }

        if (options.Sharpen is { } sharpen)
        {
            master = WaveletSharpen.Sharpen(master, sharpen);
        }

        return (master, alignment, cropped);
    }

    // The limb fit's options a master's colours are read by: its planet at the instant it shows it (the de-rotation's epoch, else the
    // middle of its capture), or null for a correlation where either is unknown.
    private static LimbFitOptions? LimbOptionsFor(Image master, PlanetaryStackOptions options, DateTimeOffset? epoch)
        => PlanetaryChannelAlignment.LimbOptionsFor(options.Planet ?? options.Derotation?.Planet, PlanetaryBestStack.InstantOf(master, epoch));

    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1)
        {
            return 1;
        }

        var p = 1;
        while (p < value)
        {
            p <<= 1;
        }

        return p;
    }
}
