using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// The classical step after TianWen's own star remover (#2204, adopted 2026-11-18): where the model dug a hole under a star
/// it removed, the hole is filled to the sky around it, and where it left a little light standing, that is lowered a little.
/// No model is involved and nothing outside a removed star moves. This is the fifth version of the step ("floor5" in
/// docs/plans/star-remover-training.md; the third, "floor3", drew flat patches inside bright nebula, #2243, and the fourth,
/// "floor4", still cut square holes into nebula knots under crowded stars and lifted dark globules, #2252),
/// ported from <c>training/star-removal/whole_floor.py</c>, with which it agrees to float rounding on whole masters
/// (<c>training/star-removal/dig_fill_parity.py</c>).
/// </summary>
/// <remarks>
/// <para><b>What it does, in order.</b></para>
/// <list type="number">
/// <item><b>Noise.</b> Each channel's local noise sigma is read off the input: the scatter (1.4826 MAD) of the input less its
/// <see cref="NoiseHighPassPx"/> px median, in <see cref="NoiseBlockPx"/> px blocks, smoothed and interpolated to every pixel.</item>
/// <item><b>Where a star was taken.</b> Input less starless, in sigmas, smoothed <see cref="RemovedBlurPx"/> px: a pixel over
/// <see cref="RemovedSigma"/> in any channel was a star's, and the set is grown <see cref="GrowPx"/> px. Only these pixels
/// can change.</item>
/// <item><b>The sky under it.</b> The starless plate OUTSIDE that set, spread in by a normalised Gaussian of
/// <see cref="SkyBlurPx"/> px (three times that deep inside a large set, where the near one reaches almost nothing).</item>
/// <item><b>Pinholes.</b> A pixel of the set more than <see cref="PinholeSigma"/> sigmas under that sky is a pinhole at a
/// star's core, not noise, and is set to the sky.</item>
/// <item><b>The smooth lift.</b> What is left of the plate's departure from the sky is smoothed <see cref="SmoothBlurPx"/> px
/// within the set. Where it is negative (a dig) it is added back; the pixel noise under the star is kept, since only the
/// smooth part moves. The lift is tapered to nothing at the set's edge so no step is drawn there.</item>
/// <item><b>Capped by what the model took.</b> No correction exceeds a share of the model's own removal at that pixel (input
/// less starless, smoothed <see cref="TakenBlurPx"/> px, the largest within <see cref="TakenReachPx"/> px): a lift is at most
/// <see cref="MaxLiftOfTaken"/> times it, so smooth bright nebula the model barely touched is left alone, and what still
/// stands ABOVE the sky is lowered by at most <see cref="MaxLoweringOfTaken"/> of it, which reaches the residue at a
/// saturated core (the removal there is hundreds of sigmas) and not a galaxy's nucleus.</item>
/// <item><b>Only as far as the sky can be trusted.</b> The sky of step 3 is an interpolation, and inside bright, structured
/// nebula the set is one large blob, so that sky comes from far away and is flat where the nebula is not: filling to it drew
/// flat patches over real dark structure. So the scatter of the plate OUTSIDE the set is read under the very Gaussian that
/// made the sky, in noise sigmas, the largest of the channels: about 1 on flat sky, 3.5 to 5 where the patches were. The
/// whole correction (lift and lowering) is applied in full up to <see cref="TrustedSkySpread"/>, not at all from
/// <see cref="UntrustedSkySpread"/>, and in proportion between.</item>
/// <item><b>Only where the set as a whole is near that sky</b> (#2252). The far pixels can agree with one another, so the
/// sky is trusted, and still not be the level under a large blob: a nebula knot between the star cores was lowered to the far
/// sky in the square footprint of step 6's cap, and a dark globule was lifted toward it. So the plate less the sky is averaged
/// over the set under the near sky's Gaussian, in noise sigmas, per channel. On dark sky it is a few tenths of a sigma where
/// the step lowers (99 % under 1.0 to 1.7) and never 2 under where it fills; on a knot under crowded stars it is tens of
/// sigmas. The lowering is applied in full while the highest channel stands under <see cref="NearSetLevel"/> over the sky
/// and not at all from <see cref="FarSetLevel"/>. The fill is held back the same way where the lowest channel lies that far
/// under the sky AND the input is as dark there itself, read as the input's own floor about the pixel
/// (<see cref="InputFloorWindowPx"/>, <see cref="InputFloorRank"/>): a globule is in the input, a dig is the model's.</item>
/// </list>
/// <para>Absent pixels (NaN in either image, the canvas ring) are held out of every measure and stay absent.</para>
/// <para><b>The filters are scipy.ndimage's, to the bit.</b> The constants were chosen and scored through scipy, so the
/// Gaussian (truncated at four sigmas, mirrored at the border, rows then columns, summed in scipy's order), the median, the
/// maximum, the dilation and the linear zoom here reproduce it exactly. Two stated differences, neither reachable on a frame
/// the Python step handles: a frame with no noise block of over <see cref="MinBlockSamples"/> pixels takes its noise from the
/// whole frame (the script yields NaN), and the zoom's last sample is clamped to the grid (scipy writes zero if rounding
/// carries it past).</para>
/// </remarks>
public static class DigFill
{
    /// <summary>The side of the median the input is high-passed with before its noise is read, pixels.</summary>
    public const int NoiseHighPassPx = 5;

    /// <summary>The block the local noise is read in, pixels a side.</summary>
    public const int NoiseBlockPx = 64;

    /// <summary>A noise block needs more present pixels than this to be read.</summary>
    public const int MinBlockSamples = 200;

    /// <summary>The blur of the removal (input less starless, in sigmas) before it is thresholded, pixels.</summary>
    public const double RemovedBlurPx = 1.0;

    /// <summary>The smoothed removal, in noise sigmas, over which a pixel belonged to a removed star.</summary>
    public const float RemovedSigma = 1.5f;

    /// <summary>How far the set of removed-star pixels is grown, pixels (city-block distance).</summary>
    public const int GrowPx = 2;

    /// <summary>The Gaussian that spreads the sky outside the set in under it, pixels.</summary>
    public const double SkyBlurPx = 4.0;

    /// <summary>The wider Gaussian used where the near one reaches too little sky (<see cref="ThinSkyWeight"/>).</summary>
    public const double FarSkyBlurPx = 3 * SkyBlurPx;

    /// <summary>The share of the near Gaussian's weight that lies on sky, under which the far one is used.</summary>
    public const float ThinSkyWeight = 0.02f;

    /// <summary>A pixel of the set this many sigmas under the sky is a pinhole and is set to the sky.</summary>
    public const float PinholeSigma = 2.5f;

    /// <summary>The blur that separates the plate's smooth departure from the sky (moved) from its pixel noise (kept).</summary>
    public const double SmoothBlurPx = 1.5;

    /// <summary>The blur of the set that makes the taper: full inside, nothing at the edge.</summary>
    public const double TaperBlurPx = 1.0;

    /// <summary>The blur of what the model took (input less starless) before the caps are read off it, pixels.</summary>
    public const double TakenBlurPx = 1.0;

    /// <summary>The window the largest take is read in, pixels a side (so within 2 px).</summary>
    public const int TakenReachPx = 5;

    /// <summary>A lift is at most this many times what the model took there.</summary>
    public const float MaxLiftOfTaken = 2f;

    /// <summary>
    /// The two corrections at one pixel, each held to its share of what the model took there: a lift of at most
    /// <see cref="MaxLiftOfTaken"/> times it and a lowering of at most <see cref="MaxLoweringOfTaken"/> of it. A wish
    /// in the other direction is no correction (a wanted lift below zero lifts nothing).
    /// </summary>
    /// <remarks>
    /// Its own function so the lift's cap has a test: with the cap taken out, every scene in <c>DigFillTests</c> still
    /// passed (2026-11-20, #2209). The reason as far as it is understood: a hole the model dug is itself a take, so
    /// the lift a dig wants stays under twice the take, and a plate lying under a sky the input lies under too is
    /// refused earlier, by the input's own floor (#2252). No scene that gets past both was found; the cap is the
    /// bound for one that does.
    /// </remarks>
    internal static (float Up, float Down) Capped(float wantedLift, float standing, float taken)
        => (MathF.Min(MathF.Max(wantedLift, 0f), MaxLiftOfTaken * taken),
            MathF.Min(MathF.Max(standing, 0f), MaxLoweringOfTaken * taken));

    /// <summary>Light standing above the sky is lowered by at most this share of what the model took there.</summary>
    public const float MaxLoweringOfTaken = 0.05f;

    /// <summary>The scatter of the sky under the set, in noise sigmas, up to which the correction is applied in full: the
    /// 99th percentile of where the step acts on dark sky is 1.7 to 1.9 on the held-out masters.</summary>
    public const float TrustedSkySpread = 2f;

    /// <summary>The scatter from which nothing is corrected: about the median of where the third version drew its patches.</summary>
    public const float UntrustedSkySpread = 4f;

    /// <summary>How far the set as a whole may stand from its sky, in noise sigmas, for a correction toward that sky to be
    /// applied in full: under a hundredth of the pixels the step lowers on dark sky stand further over it, and none it fills
    /// lies further under.</summary>
    public const float NearSetLevel = 2f;

    /// <summary>The distance from which the lowering is nothing, and the fill where the input is that dark too.</summary>
    public const float FarSetLevel = 4f;

    /// <summary>The blur of the input before its own floor is read, pixels.</summary>
    public const double InputFloorBlurPx = 1.0;

    /// <summary>The window the input's own floor is read in, pixels a side: wide enough that a star's core may cover most of
    /// it and the floor is still the scene's.</summary>
    public const int InputFloorWindowPx = 15;

    /// <summary>The input's floor is the value this share of the way up the window's present pixels (no interpolation).</summary>
    public const double InputFloorRank = 0.2;

    private const float MadToSigma = 1.4826f;
    private const float MinSigma = 1e-9f;
    private const float MinWeight = 1e-6f;
    private const double MinWideWeight = 1e-6;

    /// <summary>
    /// <paramref name="starless"/> (a remover's plate of <paramref name="input"/>, on the input's scale) with its digs filled
    /// and its residue lowered; also the share of pixels that could change (the grown set of removed-star pixels). A plate
    /// in which no star was taken is handed back as it is, the same instance.
    /// </summary>
    public static (Image Plate, double MaskedShare) Apply(Image input, Image starless)
    {
        if (input.Width != starless.Width || input.Height != starless.Height)
        {
            throw new ArgumentException($"the input is {input.Width}x{input.Height} and the starless {starless.Width}x{starless.Height}: put them on one grid first");
        }
        var (channels, width, height) = starless.Shape;
        var n = width * height;

        // Absent in either image is absent in both here, and the pixel is held out of every channel's measures.
        var present = new bool[n];
        Array.Fill(present, true);
        var absent = new bool[channels][];
        var x = new float[channels][];
        var o = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            var a = input.GetChannelSpan(Math.Min(c, input.ChannelCount - 1));
            var s = starless.GetChannelSpan(c);
            var gone = new bool[n];
            var xc = new float[n];
            var oc = new float[n];
            for (var i = 0; i < n; i++)
            {
                if (float.IsFinite(a[i]) && float.IsFinite(s[i]))
                {
                    xc[i] = a[i];
                    oc[i] = s[i];
                }
                else
                {
                    gone[i] = true;
                    present[i] = false;
                }
            }
            absent[c] = gone;
            x[c] = xc;
            o[c] = oc;
        }

        // 1. The noise, and 2. where a star was taken.
        var sigma = new float[channels][];
        var strongest = new float[n];
        Array.Fill(strongest, float.NegativeInfinity);
        for (var c = 0; c < channels; c++)
        {
            if (NoiseMap(x[c], present, width, height) is not { } map)
            {
                return (starless, 0);
            }
            sigma[c] = map;
            var inSigmas = new float[n];
            for (var i = 0; i < n; i++)
            {
                inSigmas[i] = (x[c][i] - o[c][i]) / map[i];
            }
            var removed = Gaussian(inSigmas, width, height, RemovedBlurPx);
            for (var i = 0; i < n; i++)
            {
                strongest[i] = MathF.Max(strongest[i], removed[i]);
            }
        }
        var set = new bool[n];
        var any = false;
        for (var i = 0; i < n; i++)
        {
            set[i] = strongest[i] > RemovedSigma;
            any |= set[i];
        }
        if (!any)
        {
            return (starless, 0);
        }
        for (var step = 0; step < GrowPx; step++)
        {
            set = Dilate(set, width, height);
        }
        long masked = 0;
        var sky = new float[n];       // 1 on present pixels outside the set
        var inside = new float[n];    // 1 on the set
        for (var i = 0; i < n; i++)
        {
            set[i] &= present[i];
            if (set[i])
            {
                inside[i] = 1f;
                masked++;
            }
            else if (present[i])
            {
                sky[i] = 1f;
            }
        }

        // The weights every channel shares: how much sky each Gaussian reaches, the smooth part's normaliser, the taper.
        var nearWeight = Gaussian(sky, width, height, SkyBlurPx);
        var thin = false;
        for (var i = 0; i < n && !thin; i++)
        {
            thin = nearWeight[i] < ThinSkyWeight;
        }
        var farWeight = thin ? Gaussian(sky, width, height, FarSkyBlurPx) : null;
        var smoothWeight = Gaussian(inside, width, height, SmoothBlurPx);
        var taper = Gaussian(inside, width, height, TaperBlurPx);
        for (var i = 0; i < n; i++)
        {
            smoothWeight[i] = MathF.Max(smoothWeight[i], MinWeight);
            taper[i] = MathF.Min(MathF.Max((2f * taper[i]) - 1f, 0f), 1f);
        }

        // 3. The sky under the set, per channel.
        var backgrounds = new float[channels][];
        var work = new float[n];
        for (var c = 0; c < channels; c++)
        {
            var oc = o[c];
            for (var i = 0; i < n; i++)
            {
                work[i] = oc[i] * sky[i];
            }
            var background = Gaussian(work, width, height, SkyBlurPx);
            var far = farWeight is null ? null : Gaussian(work, width, height, FarSkyBlurPx);
            for (var i = 0; i < n; i++)
            {
                background[i] = far is not null && farWeight is not null && nearWeight[i] < ThinSkyWeight
                    ? far[i] / MathF.Max(farWeight[i], MinWeight)
                    : background[i] / MathF.Max(nearWeight[i], MinWeight);
            }
            backgrounds[c] = background;
        }

        // 7. How far that sky can be trusted: the largest of the channels' scatters.
        var trust = SkyTrust(o, sky, sigma, set, nearWeight, farWeight is not null, width, height);

        // 8. How far the set as a whole stands from that sky: what the lowering and the fill each keep. The step is done with
        // strongest and nearWeight, so those two planes carry the answer, and work is its scratch as it is everyone's.
        var (stands, lies) = SetLevel(o, x, backgrounds, sigma, set, inside, present, work, strongest, nearWeight, width, height);

        var planes = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var oc = o[c];
            var xc = x[c];
            var sig = sigma[c];
            var background = backgrounds[c];

            // 4. Pinholes to the sky, then 5. the smooth departure of what is left.
            var pinned = new float[n];
            for (var i = 0; i < n; i++)
            {
                pinned[i] = set[i] && (oc[i] - background[i]) < (-PinholeSigma * sig[i]) ? background[i] : oc[i];
                work[i] = (pinned[i] - background[i]) * inside[i];
            }
            var smooth = Gaussian(work, width, height, SmoothBlurPx);

            // 6. What the model took, the largest nearby.
            for (var i = 0; i < n; i++)
            {
                work[i] = xc[i] - oc[i];
            }
            var taken = Gaussian(work, width, height, TakenBlurPx);
            for (var i = 0; i < n; i++)
            {
                taken[i] = MathF.Max(taken[i], 0f);
            }
            taken = Maximum(taken, width, height, TakenReachPx);

            var plane = new float[height, width];
            var gone = absent[c];
            for (var y = 0; y < height; y++)
            {
                for (var px = 0; px < width; px++)
                {
                    var i = (y * width) + px;
                    if (gone[i])
                    {
                        plane[y, px] = float.NaN;
                    }
                    else if (set[i])
                    {
                        var departure = smooth[i] / smoothWeight[i];
                        var lifted = pinned[i] - (MathF.Min(departure, 0f) * taper[i]);
                        var (up, down) = Capped(lifted - oc[i], departure * taper[i], taken[i]);
                        plane[y, px] = oc[i] + (trust[i] * ((lies[i] * up) - (stands[i] * down)));
                    }
                    else
                    {
                        plane[y, px] = oc[i];
                    }
                }
            }
            planes[c] = plane;
        }

        // Labelled with the peak and floor the plate HOLDS, as NarrowbandCombination's ThroughOneScale does: this step lowers
        // and lifts values, so the plate's own labels may no longer bound them.
        var max = float.NegativeInfinity;
        var min = float.PositiveInfinity;
        foreach (var plane in planes)
        {
            foreach (var v in plane)
            {
                if (float.IsFinite(v))
                {
                    max = MathF.Max(max, v);
                    min = MathF.Min(min, v);
                }
            }
        }
        var filled = float.IsFinite(max)
            ? new Image(planes, BitDepth.Float32, max, min, starless.Pedestal, starless.ImageMeta)
            : new Image(planes, BitDepth.Float32, starless.MaxValue, starless.MinValue, starless.Pedestal, starless.ImageMeta);
        return (filled, (double)masked / n);
    }

    // Step 8, what the lowering (stands) and the fill (lies) each keep of their correction. The plate less its sky is averaged
    // over the set under the near sky's Gaussian, in noise sigmas, per channel: the highest channel answers for the lowering,
    // the lowest for the fill. A dig lies under its sky as a dark globule does, and only the globule is dark in the input
    // too, so where the plate says far under, the input's own floor about the pixel is read against the same sky and the
    // fill keeps the larger of the two weights. That floor is read at a few thousand pixels of a master.
    //
    // work is scratch; highest and lowest are planes the caller is done with, and come back as the two weights. Two planes
    // are made here (the set's weight and the Gaussian's first pass) and every Gaussian runs through them. With a new plane
    // per Gaussian this step added 233 MB to the peak of a 12 megapixel colour master and a gigabyte at 24, where the
    // planes went uncollected; this way it adds 140 MB and under 30 MB.
    private static (float[] Stands, float[] Lies) SetLevel(
        float[][] plates, float[][] inputs, float[][] backgrounds, float[][] sigma, bool[] set, float[] inside, bool[] present,
        float[] work, float[] highest, float[] lowest, int width, int height)
    {
        var n = width * height;
        var between = new float[n];
        var setWeight = Gaussian(inside, width, height, SkyBlurPx, between: between);
        Array.Fill(highest, float.NegativeInfinity);
        Array.Fill(lowest, float.PositiveInfinity);
        for (var c = 0; c < plates.Length; c++)
        {
            var (plate, background, noise) = (plates[c], backgrounds[c], sigma[c]);
            for (var i = 0; i < n; i++)
            {
                work[i] = (plate[i] - background[i]) * inside[i];
            }
            var level = Gaussian(work, width, height, SkyBlurPx, into: work, between: between);
            for (var i = 0; i < n; i++)
            {
                var inSigmas = level[i] / MathF.Max(setWeight[i], MinWeight) / noise[i];
                highest[i] = MathF.Max(highest[i], inSigmas);
                lowest[i] = MathF.Min(lowest[i], inSigmas);
            }
        }

        // The weights are made in place: highest becomes what the lowering keeps, lowest what the fill keeps.
        var asked = new List<int>();
        for (var i = 0; i < n; i++)
        {
            highest[i] = Math.Clamp((FarSetLevel - highest[i]) / (FarSetLevel - NearSetLevel), 0f, 1f);
            lowest[i] = Math.Clamp((FarSetLevel + lowest[i]) / (FarSetLevel - NearSetLevel), 0f, 1f);
            if (set[i] && lowest[i] < 1f)
            {
                asked.Add(i);
            }
        }
        if (asked.Count == 0)
        {
            return (highest, lowest);
        }

        // The input's own floor at the asked pixels: the lowest channel's, each against its own sky and noise.
        var half = InputFloorWindowPx / 2;
        var floor = new float[asked.Count];
        Array.Fill(floor, float.PositiveInfinity);
        for (var c = 0; c < inputs.Length; c++)
        {
            var smooth = Gaussian(inputs[c], width, height, InputFloorBlurPx, into: work, between: between);
            var (background, noise) = (backgrounds[c], sigma[c]);
            Parallel.For(0, asked.Count, () => new float[InputFloorWindowPx * InputFloorWindowPx], (at, _, window) =>
            {
                var i = asked[at];
                var (row, column) = (i / width, i % width);
                var count = 0;
                for (var y = Math.Max(0, row - half); y <= Math.Min(height - 1, row + half); y++)
                {
                    for (var px = Math.Max(0, column - half); px <= Math.Min(width - 1, column + half); px++)
                    {
                        var j = (y * width) + px;
                        if (present[j])
                        {
                            window[count++] = smooth[j];
                        }
                    }
                }
                // The asked pixel is of the set, so present, and the window holds it at least.
                var value = StatisticsHelper.NthSmallest(window.AsSpan(0, count), (int)(InputFloorRank * (count - 1)));
                floor[at] = MathF.Min(floor[at], (value - background[i]) / noise[i]);
                return window;
            }, static _ => { });
        }
        for (var at = 0; at < asked.Count; at++)
        {
            var i = asked[at];
            lowest[i] = MathF.Max(lowest[i], Math.Clamp((FarSetLevel + floor[at]) / (FarSetLevel - NearSetLevel), 0f, 1f));
        }
        return (highest, lowest);
    }

    // Step 7, the trust per pixel: 1 where the largest of the channels' scatters of the plate outside the set is under
    // TrustedSkySpread noise sigmas, 0 from UntrustedSkySpread, linear between. The scatter is the square root of the
    // weighted mean of squares less the squared weighted mean, under the Gaussian that makes the sky there (the near one
    // where it reaches enough sky, the far one where it does not). It is a small difference of two large sums, so it is
    // taken in double: in float a level of a few thousand sigmas would read as no scatter at all.
    //
    // Read row by row, each row's sums held in a few lines of doubles, so no whole-frame plane is held in double (#2254:
    // as whole planes they were about 2 GB on a 26 megapixel colour master). The sums and their order are those of scipy's
    // gaussian_filter on float64: down the column first (the centre tap, then the pairs from the farthest in), then along
    // the row the same way, both mirrored at the border. Only the set's pixels are read off it, so only they are worked
    // out: a row with none is skipped, and so is a Gaussian on a row that has no pixel for it.
    //
    // spread, where given, takes each set pixel's largest scatter as it was summed. The trust is clamped and narrowed to
    // float, which hides a last digit, so a test that holds the sums to the bit reads them here.
    internal static float[] SkyTrust(
        float[][] plates, float[] sky, float[][] sigma, bool[] set, float[] nearWeight, bool hasFar, int width, int height, double[]? spread = null)
    {
        var trust = new float[width * height];
        var near = GaussianKernel(SkyBlurPx);
        var far = hasFar ? GaussianKernel(FarSkyBlurPx) : null;
        var reach = (far ?? near).Length / 2;
        Parallel.For(0, height, () => new ScatterRow(width, reach), (y, _, row) =>
        {
            var (anyNear, anyFar) = (false, false);
            for (var px = 0; px < width; px++)
            {
                var i = (y * width) + px;
                var thin = hasFar && nearWeight[i] < ThinSkyWeight;
                row.Near[px] = set[i] && !thin;
                row.Far[px] = set[i] && thin;
                anyNear |= row.Near[px];
                anyFar |= row.Far[px];
            }
            if (!anyNear && !anyFar)
            {
                return row;
            }
            Array.Clear(row.Largest);
            if (anyNear)
            {
                RaiseToScatter(row, near, row.Near);
            }
            if (anyFar && far is not null)
            {
                RaiseToScatter(row, far, row.Far);
            }
            for (var px = 0; px < width; px++)
            {
                if (row.Near[px] || row.Far[px])
                {
                    trust[(y * width) + px] = (float)Math.Clamp((UntrustedSkySpread - row.Largest[px]) / (UntrustedSkySpread - TrustedSkySpread), 0.0, 1.0);
                    if (spread is not null)
                    {
                        spread[(y * width) + px] = row.Largest[px];
                    }
                }
            }
            return row;

            // Raises row.Largest to each channel's scatter under this Gaussian, at the pixels this Gaussian answers for,
            // one run of them at a time: a run is wanted pixels no further apart than the kernel is wide, and only the
            // columns within the kernel's reach of it are summed.
            void RaiseToScatter(ScatterRow r, double[] kernel, bool[] wanted)
            {
                var radius = kernel.Length / 2;
                var px = 0;
                while (px < width)
                {
                    if (!wanted[px])
                    {
                        px++;
                        continue;
                    }
                    var last = px;
                    for (var next = px + 1; next < width && next - last <= 2 * radius; next++)
                    {
                        if (wanted[next])
                        {
                            last = next;
                        }
                    }
                    RaiseRun(r, kernel, wanted, px, last);
                    px = last + 1;
                }
            }

            void RaiseRun(ScatterRow r, double[] kernel, bool[] wanted, int first, int last)
            {
                var radius = kernel.Length / 2;
                var centre = kernel[radius];
                var own = y * width;
                var (from, to) = (Math.Max(0, first - radius), Math.Min(width - 1, last + radius));

                // The Gaussian of the sky's own 0/1 weight on this row.
                for (var px = from; px <= to; px++)
                {
                    r.First[px] = sky[own + px] * centre;
                }
                for (var j = radius; j >= 1; j--)
                {
                    var above = Mirror(y - j, height) * width;
                    var below = Mirror(y + j, height) * width;
                    var weight = kernel[radius - j];
                    for (var px = from; px <= to; px++)
                    {
                        r.First[px] += ((double)sky[above + px] + sky[below + px]) * weight;
                    }
                }
                AlongTheRow(r.First, r.Total, r.Line, kernel, wanted, first, last, width);

                for (var c = 0; c < plates.Length; c++)
                {
                    // The plate's first and second moments outside the set, down the column.
                    var plate = plates[c];
                    for (var px = from; px <= to; px++)
                    {
                        double value = plate[own + px];
                        double on = sky[own + px];
                        r.First[px] = (value * on) * centre;
                        r.Second[px] = (value * value * on) * centre;
                    }
                    for (var j = radius; j >= 1; j--)
                    {
                        var above = Mirror(y - j, height) * width;
                        var below = Mirror(y + j, height) * width;
                        var weight = kernel[radius - j];
                        for (var px = from; px <= to; px++)
                        {
                            double valueAbove = plate[above + px];
                            double onAbove = sky[above + px];
                            double valueBelow = plate[below + px];
                            double onBelow = sky[below + px];
                            r.First[px] += ((valueAbove * onAbove) + (valueBelow * onBelow)) * weight;
                            r.Second[px] += ((valueAbove * valueAbove * onAbove) + (valueBelow * valueBelow * onBelow)) * weight;
                        }
                    }
                    AlongTheRow(r.First, r.Sum, r.Line, kernel, wanted, first, last, width);
                    AlongTheRow(r.Second, r.Squares, r.Line, kernel, wanted, first, last, width);

                    var noise = sigma[c];
                    for (var px = first; px <= last; px++)
                    {
                        if (wanted[px])
                        {
                            var share = Math.Max(r.Total[px], MinWideWeight);
                            var mean = r.Sum[px] / share;
                            var scatter = Math.Sqrt(Math.Max((r.Squares[px] / share) - (mean * mean), 0.0)) / noise[own + px];
                            r.Largest[px] = Math.Max(r.Largest[px], scatter);
                        }
                    }
                }
            }
        }, static _ => { });
        return trust;
    }

    // The second pass of the Gaussian on one row, at the wanted pixels of a run: the row's part within the kernel's reach
    // of the run mirrored into a line, then the centre tap and the pairs from the farthest in. A column past the frame's
    // edge mirrors onto one within that reach, which the first pass summed.
    private static void AlongTheRow(double[] row, double[] result, double[] line, double[] kernel, bool[] wanted, int first, int last, int width)
    {
        var radius = kernel.Length / 2;
        var centre = kernel[radius];
        for (var k = first; k <= last + (2 * radius); k++)
        {
            line[k] = row[Mirror(k - radius, width)];
        }
        for (var px = first; px <= last; px++)
        {
            if (!wanted[px])
            {
                continue;
            }
            var at = px + radius;
            var sum = line[at] * centre;
            for (var j = radius; j >= 1; j--)
            {
                sum += (line[at - j] + line[at + j]) * kernel[radius - j];
            }
            result[px] = sum;
        }
    }

    // One thread's lines for SkyTrust: a row's sums down the column, the same after the pass along the row, the largest
    // scatter found on the row so far, and which of the row's pixels each Gaussian answers for.
    private sealed class ScatterRow(int width, int reach)
    {
        public readonly double[] First = new double[width];
        public readonly double[] Second = new double[width];
        public readonly double[] Total = new double[width];
        public readonly double[] Sum = new double[width];
        public readonly double[] Squares = new double[width];
        public readonly double[] Largest = new double[width];
        public readonly double[] Line = new double[width + (2 * reach)];
        public readonly bool[] Near = new bool[width];
        public readonly bool[] Far = new bool[width];
    }

    // The local noise sigma at every pixel (step 1), or null when the frame has no present pixel to read it from.
    private static float[]? NoiseMap(float[] plane, bool[] present, int width, int height)
    {
        var highPass = HighPass(plane, width, height);
        var gridWidth = (width / NoiseBlockPx) + 1;
        var gridHeight = (height / NoiseBlockPx) + 1;
        var grid = new float[gridWidth * gridHeight];
        Parallel.For(0, gridHeight, () => new float[NoiseBlockPx * NoiseBlockPx], (gy, _, values) =>
        {
            for (var gx = 0; gx < gridWidth; gx++)
            {
                var count = 0;
                for (var y = gy * NoiseBlockPx; y < Math.Min(height, (gy + 1) * NoiseBlockPx); y++)
                {
                    for (var px = gx * NoiseBlockPx; px < Math.Min(width, (gx + 1) * NoiseBlockPx); px++)
                    {
                        if (present[(y * width) + px])
                        {
                            values[count++] = highPass[(y * width) + px];
                        }
                    }
                }
                grid[(gy * gridWidth) + gx] = count > MinBlockSamples ? Scatter(values.AsSpan(0, count)) : float.NaN;
            }
            return values;
        }, static _ => { });

        // A block too thin to read takes the median of those that were; a frame with none is read whole.
        var read = new float[grid.Length];
        var reads = 0;
        foreach (var v in grid)
        {
            if (!float.IsNaN(v))
            {
                read[reads++] = v;
            }
        }
        float fill;
        if (reads > 0)
        {
            fill = Median(read.AsSpan(0, reads));
        }
        else
        {
            var all = new float[plane.Length];
            var count = 0;
            for (var i = 0; i < plane.Length; i++)
            {
                if (present[i])
                {
                    all[count++] = highPass[i];
                }
            }
            if (count == 0)
            {
                return null;
            }
            fill = Scatter(all.AsSpan(0, count));
        }
        for (var i = 0; i < grid.Length; i++)
        {
            if (float.IsNaN(grid[i]))
            {
                grid[i] = fill;
            }
        }

        var map = Zoom(Gaussian(grid, gridWidth, gridHeight, 1.0), gridWidth, gridHeight, width, height);
        for (var i = 0; i < map.Length; i++)
        {
            map[i] = MathF.Max(map[i], MinSigma);
        }
        return map;
    }

    // 1.4826 times the median absolute deviation from the median; the span is reordered.
    private static float Scatter(Span<float> values)
    {
        var median = Median(values);
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = MathF.Abs(values[i] - median);
        }
        return MadToSigma * Median(values);
    }

    // numpy's median: the middle value, or the mean of the middle two; the span is sorted.
    private static float Median(Span<float> values)
    {
        values.Sort();
        var mid = values.Length / 2;
        return (values.Length & 1) == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2f;
    }

    // The plane less its NoiseHighPassPx square median (scipy's median_filter, mirrored at the border).
    private static float[] HighPass(float[] src, int width, int height)
    {
        const int side = NoiseHighPassPx;
        const int half = side / 2;
        var dst = new float[src.Length];
        Parallel.For(0, height, y =>
        {
            Span<int> rows = stackalloc int[side];
            Span<int> columns = stackalloc int[side];
            Span<float> window = stackalloc float[side * side];
            for (var k = 0; k < side; k++)
            {
                rows[k] = Mirror(y + k - half, height) * width;
            }
            for (var px = 0; px < width; px++)
            {
                for (var k = 0; k < side; k++)
                {
                    columns[k] = Mirror(px + k - half, width);
                }
                var count = 0;
                foreach (var row in rows)
                {
                    foreach (var column in columns)
                    {
                        // Insertion keeps the window sorted as it fills.
                        var v = src[row + column];
                        var at = count++;
                        while (at > 0 && window[at - 1] > v)
                        {
                            window[at] = window[at - 1];
                            at--;
                        }
                        window[at] = v;
                    }
                }
                dst[(y * width) + px] = src[(y * width) + px] - window[(side * side) / 2];
            }
        });
        return dst;
    }

    // scipy's gaussian_filter on a float32 plane: the kernel truncated at four sigmas and normalised, the border mirrored,
    // rows first and then columns with the result rounded to float between, each sum taken in double from the centre tap
    // and then the pairs from the farthest in, which is the order scipy's symmetric correlation adds in.
    //
    // into, where given, takes the result in place of a new plane, and may be src itself (src is read whole before the
    // result is written). between, where given, is the plane the first pass is held in, and may not be src.
    private static float[] Gaussian(float[] src, int width, int height, double sigma, float[]? into = null, float[]? between = null)
    {
        var kernel = GaussianKernel(sigma);
        var radius = kernel.Length / 2;
        var centre = kernel[radius];
        var mid = between ?? new float[src.Length];
        Parallel.For(0, height, () => new double[width], (y, _, sum) =>
        {
            var own = src.AsSpan(y * width, width);
            for (var px = 0; px < width; px++)
            {
                sum[px] = own[px] * centre;
            }
            for (var j = radius; j >= 1; j--)
            {
                var above = src.AsSpan(Mirror(y - j, height) * width, width);
                var below = src.AsSpan(Mirror(y + j, height) * width, width);
                var weight = kernel[radius - j];
                for (var px = 0; px < width; px++)
                {
                    sum[px] += ((double)above[px] + below[px]) * weight;
                }
            }
            var row = mid.AsSpan(y * width, width);
            for (var px = 0; px < width; px++)
            {
                row[px] = (float)sum[px];
            }
            return sum;
        }, static _ => { });

        var dst = into ?? new float[src.Length];
        Parallel.For(0, height, () => new double[width + (2 * radius)], (y, _, line) =>
        {
            var row = mid.AsSpan(y * width, width);
            for (var k = 0; k < line.Length; k++)
            {
                line[k] = row[Mirror(k - radius, width)];
            }
            var result = dst.AsSpan(y * width, width);
            for (var px = 0; px < width; px++)
            {
                var at = px + radius;
                var sum = line[at] * centre;
                for (var j = radius; j >= 1; j--)
                {
                    sum += (line[at - j] + line[at + j]) * kernel[radius - j];
                }
                result[px] = (float)sum;
            }
            return line;
        }, static _ => { });
        return dst;
    }

    // scipy's kernel: exp(-x^2 / 2 sigma^2) over [-4 sigma, 4 sigma], divided by its sum taken as numpy takes it (eight
    // running sums, combined in pairs, then the rest), so the weights are numpy's to the last bit.
    internal static double[] GaussianKernel(double sigma)
    {
        var radius = (int)((4.0 * sigma) + 0.5);
        var kernel = new double[(2 * radius) + 1];
        var scale = -0.5 / (sigma * sigma);
        for (var i = 0; i < kernel.Length; i++)
        {
            long offset = i - radius;
            kernel[i] = Math.Exp(scale * (offset * offset));
        }
        double total;
        if (kernel.Length < 8)
        {
            total = 0;
            foreach (var v in kernel)
            {
                total += v;
            }
        }
        else
        {
            Span<double> running = stackalloc double[8];
            kernel.AsSpan(0, 8).CopyTo(running);
            var i = 8;
            for (; i < kernel.Length - (kernel.Length % 8); i += 8)
            {
                for (var j = 0; j < 8; j++)
                {
                    running[j] += kernel[i + j];
                }
            }
            total = ((running[0] + running[1]) + (running[2] + running[3])) + ((running[4] + running[5]) + (running[6] + running[7]));
            for (; i < kernel.Length; i++)
            {
                total += kernel[i];
            }
        }
        for (var i = 0; i < kernel.Length; i++)
        {
            kernel[i] /= total;
        }
        return kernel;
    }

    // scipy's maximum_filter over a square window, mirrored at the border: the largest along rows, then along columns.
    private static float[] Maximum(float[] src, int width, int height, int side)
    {
        var half = side / 2;
        var mid = new float[src.Length];
        Parallel.For(0, height, y =>
        {
            var row = src.AsSpan(y * width, width);
            var result = mid.AsSpan(y * width, width);
            for (var px = 0; px < width; px++)
            {
                var best = row[px];
                for (var k = -half; k <= half; k++)
                {
                    best = MathF.Max(best, row[Mirror(px + k, width)]);
                }
                result[px] = best;
            }
        });
        var dst = new float[src.Length];
        Parallel.For(0, height, y =>
        {
            var result = dst.AsSpan(y * width, width);
            mid.AsSpan(y * width, width).CopyTo(result);
            for (var k = -half; k <= half; k++)
            {
                var other = mid.AsSpan(Mirror(y + k, height) * width, width);
                for (var px = 0; px < width; px++)
                {
                    result[px] = MathF.Max(result[px], other[px]);
                }
            }
        });
        return dst;
    }

    // scipy's binary_dilation, one step of its default cross: a pixel and its four neighbours, nothing past the border.
    private static bool[] Dilate(bool[] src, int width, int height)
    {
        var dst = new bool[src.Length];
        for (var y = 0; y < height; y++)
        {
            for (var px = 0; px < width; px++)
            {
                var i = (y * width) + px;
                dst[i] = src[i]
                    || (px > 0 && src[i - 1]) || (px < width - 1 && src[i + 1])
                    || (y > 0 && src[i - width]) || (y < height - 1 && src[i + width]);
            }
        }
        return dst;
    }

    // scipy's zoom at order 1: output sample k reads the grid at k (in - 1) / (out - 1), linear between its two neighbours,
    // the four products summed in double in scipy's order.
    private static float[] Zoom(float[] grid, int gridWidth, int gridHeight, int width, int height)
    {
        var (column, columnNext, columnShare) = ZoomAxis(gridWidth, width);
        var (row, rowNext, rowShare) = ZoomAxis(gridHeight, height);
        var dst = new float[width * height];
        Parallel.For(0, height, y =>
        {
            var upper = grid.AsSpan(row[y] * gridWidth, gridWidth);
            var lower = grid.AsSpan(rowNext[y] * gridWidth, gridWidth);
            var lowerShare = rowShare[y];
            var upperShare = 1.0 - lowerShare;
            var result = dst.AsSpan(y * width, width);
            for (var px = 0; px < width; px++)
            {
                var rightShare = columnShare[px];
                var leftShare = 1.0 - rightShare;
                var sum = upper[column[px]] * upperShare * leftShare;
                sum += upper[columnNext[px]] * upperShare * rightShare;
                sum += lower[column[px]] * lowerShare * leftShare;
                sum += lower[columnNext[px]] * lowerShare * rightShare;
                result[px] = (float)sum;
            }
        });
        return dst;
    }

    private static (int[] At, int[] Next, double[] Share) ZoomAxis(int from, int to)
    {
        var at = new int[to];
        var next = new int[to];
        var share = new double[to];
        var step = to > 1 ? (double)(from - 1) / (to - 1) : 1.0;
        for (var k = 0; k < to; k++)
        {
            var position = Math.Min(k * step, from - 1);
            var floor = Math.Floor(position);
            at[k] = (int)floor;
            // Past the last sample scipy mirrors, and the share there is zero.
            next[k] = at[k] + 1 < from ? at[k] + 1 : Math.Max(0, (2 * from) - 3 - at[k]);
            share[k] = position - floor;
        }
        return (at, next, share);
    }

    // scipy's "reflect" border: the edge sample is repeated (d c b a | a b c d | d c b a).
    internal static int Mirror(int i, int n)
    {
        if ((uint)i < (uint)n)
        {
            return i;
        }
        var period = 2 * n;
        i %= period;
        if (i < 0)
        {
            i += period;
        }
        return i < n ? i : period - 1 - i;
    }
}
