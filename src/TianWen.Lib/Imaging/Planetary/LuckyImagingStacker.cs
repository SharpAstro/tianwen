using System;
using System.Collections.Immutable;
using System.Drawing;
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

        var used = 0;
        foreach (var index in ctx.Selected)
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
                var shift = ctx.Aligner.Estimate(frame, PlanetaryDisk.BoundingBox(frame));
                frame.AccumulateTranslatedInto(channelAccum, weightAccum, (float)shift.Dx, (float)shift.Dy, weight);
                used++;
            }
            finally
            {
                frame.Release();
            }
        }

        var stacked = Normalize(channelAccum, weightAccum, ctx);
        var master = await FinalizeAsync(stacked, stream.Layout, options, cancellationToken).ConfigureAwait(false);
        return new PlanetaryStackResult(master, ctx.ReferenceIndex, used, ctx.Grades.Length);
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
        var ctx = await PrepareAsync(stream, options, includeAlignmentPoints: true, cancellationToken).ConfigureAwait(false);
        var channelAccum = Image.CreateChannelData(ctx.Channels, ctx.Height, ctx.Width);
        var weightAccum = new float[ctx.Height, ctx.Width];
        var matcher = ctx.Matcher!;

        var used = 0;
        foreach (var index in ctx.Selected)
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
                var shift = ctx.Aligner.Estimate(frame, PlanetaryDisk.BoundingBox(frame));
                var mesh = matcher.BuildMesh(frame, (float)shift.Dx, (float)shift.Dy, options.MeshNodeSpacing);
                if (options.PerPointQualityWeighting)
                {
                    var quality = FrameSharpnessMap.Build(frame);
                    frame.AccumulateByMeshWeightedInto(channelAccum, weightAccum, mesh, quality, weight, ctx.SignalConfidence);
                }
                else
                {
                    frame.AccumulateByMeshInto(channelAccum, weightAccum, mesh, weight);
                }

                used++;
            }
            finally
            {
                frame.Release();
            }
        }

        var stacked = Normalize(channelAccum, weightAccum, ctx);
        var master = await FinalizeAsync(stacked, stream.Layout, options, cancellationToken).ConfigureAwait(false);
        return new PlanetaryStackResult(master, ctx.ReferenceIndex, used, ctx.Grades.Length);
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
        // Drop half-extent in OUTPUT pixels. The drop is pixfrac of an INPUT pixel, which is `scale` output
        // pixels wide -- so the output-space half-extent is pixfrac*scale/2. (The deep-sky DrizzleKernel
        // hardcodes pixfrac/2 because it only runs at scale=1; at scale>1 that under-sizes the drop and
        // leaves periodic gaps between drops -- a visible dot-grid pattern.)
        var halfP = drizzle.Pixfrac * scale * 0.5f;
        var pattern = ctx.MasterMeta.SensorType.GetBayerPatternMatrix(ctx.MasterMeta.BayerOffsetX, ctx.MasterMeta.BayerOffsetY);

        var used = 0;
        foreach (var index in ctx.Selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ctx.ScoreByIndex[index] <= 0f)
            {
                continue;
            }

            var frame = await stream.LoadAsync(index, cancellationToken).ConfigureAwait(false);
            try
            {
                var shift = ctx.Aligner.Estimate(frame, PlanetaryDisk.BoundingBox(frame));
                var mosaic = frame.MergeBayerChannels();
                var sourceRect = new Rectangle(0, 0, mosaic.Width, mosaic.Height);
                if (ctx.Matcher is { } matcher)
                {
                    // AP-mesh drizzle: forward-scatter each raw sample through the per-AP displacement mesh.
                    // The mesh is built at sub-plane resolution; MeshSourceToCanvas samples it at the
                    // mosaic pixel's sub-plane position, doubles the offset into mosaic space, and applies
                    // the output scale -- so drizzle gets the local de-warp, not just a whole-disk shift.
                    var mesh = matcher.BuildMesh(frame, (float)shift.Dx, (float)shift.Dy, options.MeshNodeSpacing);
                    DrizzleKernel.IterateAndDeposit(
                        mosaic, new MeshSourceToCanvas(mesh, scale), pattern, halfP, flux, weight,
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
        if (options.Sharpen is { } sharpen)
        {
            master = WaveletSharpen.Sharpen(master, sharpen);
        }

        return new PlanetaryStackResult(master, ctx.ReferenceIndex, used, ctx.Grades.Length);
    }

    /// <summary>
    /// Source(mosaic) -&gt; drizzle-canvas map driven by the per-frame AP displacement mesh. The mesh is
    /// built at split-CFA sub-plane resolution, so a mosaic pixel (2x the sub-plane grid) samples the mesh
    /// at half its coordinate and the returned sub-plane offset is doubled into mosaic space. The reference
    /// mosaic position is <c>mosaic - 2*offset</c> (the mesh offset already folds in the global shift), and
    /// the canvas position is that scaled by the output <c>scale</c> -- the per-pixel generalisation of the
    /// whole-disk affine <c>(mosaic - 2*shift) * scale</c>.
    /// </summary>
    private readonly struct MeshSourceToCanvas(DisplacementMesh mesh, float scale) : ISourceToCanvas
    {
        public Vector2 Map(int xSrc, int ySrc)
        {
            var (offX, offY) = mesh.Sample(xSrc * 0.5f, ySrc * 0.5f);
            return new Vector2((xSrc - (2f * offX)) * scale, (ySrc - (2f * offY)) * scale);
        }
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
        ImageMeta MasterMeta);

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
        try
        {
            var refRegion = PlanetaryDisk.BoundingBox(reference);
            var tileSize = options.AlignTileSize > 0
                ? NextPowerOfTwo(options.AlignTileSize)
                : Math.Clamp(NextPowerOfTwo(Math.Max(refRegion.Width, refRegion.Height)), 64, 512);
            var aligner = GlobalAligner.FromReference(reference, refRegion, tileSize);

            AlignmentPointMatcher? matcher = null;
            float[,]? signalConfidence = null;
            if (includeAlignmentPoints)
            {
                var aps = FeatureDetector.DetectAlignmentPoints(reference, refRegion, options.AlignmentPointSpacing, options.MaxAlignmentPoints);
                matcher = AlignmentPointMatcher.FromReference(reference, aps, options.AlignmentPatchSize);

                // The signal-confidence gate is computed once from the reference (= the integrator's output
                // space, since frames are warped to it). Only needed when best-of weighting is on.
                if (options.PerPointQualityWeighting && options.PerPointSignalGate)
                {
                    signalConfidence = PlanetaryDisk.SignalConfidence(reference);
                }
            }

            return new StackContext(grades, referenceIndex, selected, scoreByIndex, aligner, matcher, signalConfidence,
                reference.Width, reference.Height, reference.ChannelCount, reference.ImageMeta);
        }
        finally
        {
            reference.Release();
        }
    }

    private static Image Normalize(float[][,] channelAccum, float[,] weightAccum, StackContext ctx)
        => PlanetaryMaster.NormalizeInPlace(channelAccum, weightAccum, ctx.MasterMeta);

    /// <summary>
    /// For a split-CFA stack the integrated master is four CFA sub-planes; merge them into a full-resolution
    /// mosaic and demosaic once (MHC). Mono / RGB masters pass through unchanged. Phase 7: when
    /// <see cref="PlanetaryStackOptions.Sharpen"/> is set, the demosaiced linear master is wavelet-sharpened.
    /// </summary>
    private static async Task<Image> FinalizeAsync(Image stacked, PlanetaryFrameLayout layout, PlanetaryStackOptions options, CancellationToken cancellationToken)
    {
        var master = await PlanetaryMaster.MergeAndDemosaicAsync(stacked, layout, cancellationToken).ConfigureAwait(false);

        if (options.Sharpen is { } sharpen)
        {
            master = WaveletSharpen.Sharpen(master, sharpen);
        }

        return master;
    }

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
