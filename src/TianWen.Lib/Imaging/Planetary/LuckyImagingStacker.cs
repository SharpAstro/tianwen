using System;
using System.Collections.Immutable;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

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
                    frame.AccumulateByMeshWeightedInto(channelAccum, weightAccum, mesh, quality, weight);
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

    private sealed record StackContext(
        ImmutableArray<FrameGrade> Grades,
        int ReferenceIndex,
        ImmutableArray<int> Selected,
        float[] ScoreByIndex,
        GlobalAligner Aligner,
        AlignmentPointMatcher? Matcher,
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
            if (includeAlignmentPoints)
            {
                var aps = FeatureDetector.DetectAlignmentPoints(reference, refRegion, options.AlignmentPointSpacing, options.MaxAlignmentPoints);
                matcher = AlignmentPointMatcher.FromReference(reference, aps, options.AlignmentPatchSize);
            }

            return new StackContext(grades, referenceIndex, selected, scoreByIndex, aligner, matcher,
                reference.Width, reference.Height, reference.ChannelCount, reference.ImageMeta);
        }
        finally
        {
            reference.Release();
        }
    }

    private static Image Normalize(float[][,] channelAccum, float[,] weightAccum, StackContext ctx)
    {
        for (var c = 0; c < ctx.Channels; c++)
        {
            var plane = channelAccum[c];
            for (var y = 0; y < ctx.Height; y++)
            {
                for (var x = 0; x < ctx.Width; x++)
                {
                    var wv = weightAccum[y, x];
                    plane[y, x] = wv > 0f ? plane[y, x] / wv : 0f;
                }
            }
        }

        return new Image(channelAccum, BitDepth.Float32, 1f, 0f, 0f, ctx.MasterMeta);
    }

    /// <summary>
    /// For a split-CFA stack the integrated master is four CFA sub-planes; merge them into a full-resolution
    /// mosaic and demosaic once (MHC). Mono / RGB masters pass through unchanged. Phase 7: when
    /// <see cref="PlanetaryStackOptions.Sharpen"/> is set, the demosaiced linear master is wavelet-sharpened.
    /// </summary>
    private static async Task<Image> FinalizeAsync(Image stacked, PlanetaryFrameLayout layout, PlanetaryStackOptions options, CancellationToken cancellationToken)
    {
        Image master;
        if (layout == PlanetaryFrameLayout.SplitCfa && stacked.ChannelCount == 4)
        {
            var mosaic = stacked.MergeBayerChannels();
            master = await mosaic.DebayerAsync(DebayerAlgorithm.MHC, normalizeToUnit: false, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            master = stacked;
        }

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
