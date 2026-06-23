using System;
using System.Collections.Immutable;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Orchestrates a planetary lucky-imaging stack: grade -> select the sharpest N% -> global-align each
/// to the best frame -> quality-weighted mean. This is the Phase 4 end-to-end path (global alignment,
/// no alignment points yet); Phase 5/6 add feature-driven AP placement + a per-AP mesh-warped,
/// quality-weighted integration and a final demosaic, and Phase 7 the wavelet sharpen. The same grade /
/// select / align primitives feed the live rolling-window stacker.
/// </summary>
public sealed class LuckyImagingStacker
{
    /// <summary>
    /// Stacks <paramref name="stream"/> with whole-disk global alignment and a quality-weighted mean.
    /// </summary>
    public async Task<PlanetaryStackResult> StackGlobalAsync(IPlanetaryFrameStream stream, PlanetaryStackOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new PlanetaryStackOptions();
        if (stream.FrameCount <= 0)
        {
            throw new InvalidOperationException("The frame stream is empty.");
        }

        // 1. Grade every frame and pick the reference + the kept set.
        var grader = new FrameGrader(options.QualityEstimator);
        var grades = await grader.GradeAllAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var referenceIndex = FrameGrader.Reference(grades);
        var selected = FrameGrader.SelectBest(grades, options.KeepFraction);

        var scoreByIndex = new float[stream.FrameCount];
        foreach (var g in grades)
        {
            scoreByIndex[g.Index] = MathF.Max(0f, g.Score);
        }

        // 2. Build the aligner from the reference frame's disk.
        var reference = await stream.LoadAsync(referenceIndex, cancellationToken).ConfigureAwait(false);
        GlobalAligner aligner;
        float[][,] channelAccum;
        float[,] weightAccum;
        ImageMeta masterMeta;
        int width, height, channels;
        try
        {
            var refRegion = PlanetaryDisk.BoundingBox(reference);
            var tileSize = options.AlignTileSize > 0
                ? NextPowerOfTwo(options.AlignTileSize)
                : Math.Clamp(NextPowerOfTwo(Math.Max(refRegion.Width, refRegion.Height)), 64, 512);

            aligner = GlobalAligner.FromReference(reference, refRegion, tileSize);

            width = reference.Width;
            height = reference.Height;
            channels = reference.ChannelCount;
            masterMeta = reference.ImageMeta;
            channelAccum = Image.CreateChannelData(channels, height, width);
            weightAccum = new float[height, width];
        }
        finally
        {
            reference.Release();
        }

        // 3. Align + accumulate each selected frame (quality-weighted).
        var used = 0;
        foreach (var index in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var weight = scoreByIndex[index];
            if (weight <= 0f)
            {
                continue;
            }

            var frame = await stream.LoadAsync(index, cancellationToken).ConfigureAwait(false);
            try
            {
                var region = PlanetaryDisk.BoundingBox(frame);
                var shift = aligner.Estimate(frame, region);
                frame.AccumulateTranslatedInto(channelAccum, weightAccum, (float)shift.Dx, (float)shift.Dy, weight);
                used++;
            }
            finally
            {
                frame.Release();
            }
        }

        // 4. Normalise the weighted sum into the master.
        for (var c = 0; c < channels; c++)
        {
            var plane = channelAccum[c];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var wv = weightAccum[y, x];
                    plane[y, x] = wv > 0f ? plane[y, x] / wv : 0f;
                }
            }
        }

        var master = new Image(channelAccum, BitDepth.Float32, 1f, 0f, 0f, masterMeta);
        return new PlanetaryStackResult(master, referenceIndex, used, grades.Length);
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
