using System;
using System.Collections.Generic;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// A master flat's own defects, judged and repaired while the frames it was combined from are still in hand
/// (<see cref="MasterFrameBuilder"/>): what was on the sensor while the flats were taken but is not part of the light
/// path the lights see.
///
/// <para><b>Why the flat, not the stack.</b> A flat exists to correct the light path: vignetting, the shadow of dust on
/// the window or the filter, each photosite's response. Condensation on the sensor while it cools for the flats is none
/// of those. On the SVBONY SV605CC flats of 2026-10-17, 499 droplets formed as the sensor fell below about +2 C and dried
/// slowly once it reached -5 C (their photosites read 1.5% off their neighbours in the first flat, 75% at the peak, 58% in
/// the last), and the lights an hour later were clean there. Dividing a light by that master wrote the inverse of every
/// droplet into it; repaired here, the lights calibrated by it read 0.69% off their neighbours at those photosites,
/// against 0.66% anywhere and 75% before. The stack's own mask of a flat's defects
/// (<see cref="BadPixelDetection.BuildMaskFromFlat"/>) stays as the net under a master built elsewhere.</para>
///
/// <para><b>What tells a defect of the flat from the light path, by physics.</b> A photosite more than
/// <see cref="BadPixelDetection.DefaultMaxFlatDeviation"/> off its own colour's neighbours (the scatter of a flat is under a
/// percent) belongs to a group of such photosites, and the group is repaired when either holds:</para>
/// <list type="bullet">
/// <item><b>It changed during the run</b> (<see cref="TransientSwing"/>): the light path does not change while flats are
/// taken. The droplets swung a median 139% across the 46 frames; the steady defects of that sensor (dead or low-response
/// photosites at 0.13 to 0.9 of their neighbours) swung 2 to 4%, which is the frames' own noise. Read only from
/// <see cref="MinFramesForTime"/> frames on.</item>
/// <item><b>It moves light</b>: a photosite brighter than its neighbours beside one darker than them, both past the
/// deviation. An absorber, dust anywhere in the path, only darkens; a photosite of high response is bright alone; a lens,
/// a droplet, takes light from its ring and puts it in its core. That holds for condensation that did NOT change during
/// the run, which the first test cannot see.</item>
/// </list>
/// <para>A group that is steady and only dark (or only bright) is the light path's and keeps the flat's value. A repaired
/// photosite takes the median of the unflagged photosites of its own colour around it, which leaves it its neighbours'
/// illumination and costs it only its own response (0.44% RMS on that sensor).</para>
/// </summary>
public static class FlatDefects
{
    /// <summary>A group whose deviation from its neighbours spans more than this across the flat run (5th to 95th
    /// percentile, so a stray frame cannot set it) changed while the flats were taken. Steady defects on the SV605CC
    /// spanned 2 to 4%, the droplets a median 139%, and 4 groups of 565 fell between 5 and 20%.</summary>
    public const float TransientSwing = 0.20f;

    /// <summary>The fewest frames whose spread is read as a change; below it only the lens test is applied.</summary>
    public const int MinFramesForTime = 5;

    /// <summary>What <see cref="RepairInPlace"/> did: groups and photosites repaired, and those kept as the light
    /// path's own.</summary>
    public readonly record struct Outcome(int RepairedGroups, int RepairedPhotosites, int KeptGroups, int KeptPhotosites);

    /// <summary>
    /// Repairs, in <paramref name="master"/>, every group of flagged photosites that changed across
    /// <paramref name="frames"/> or moves light (see the type's remarks). <paramref name="frames"/> are the frames the
    /// master was combined from, normalised as they were combined. The master must be the caller's own, freshly built.
    /// </summary>
    internal static Outcome RepairInPlace(Image master, IReadOnlyList<Image> frames, float maxDeviation = BadPixelDetection.DefaultMaxFlatDeviation)
    {
        var mask = BadPixelDetection.BuildMaskFromFlat(master, maxDeviation);
        var step = BadPixelDetection.FlatLatticeStep(master);
        var timeTest = frames.Count >= MinFramesForTime;
        var (repairedGroups, repairedPhotosites, keptGroups, keptPhotosites) = (0, 0, 0, 0);
        for (var c = 0; c < master.ChannelCount; c++)
        {
            if (!mask[c].Any())
            {
                continue;
            }
            var data = master.GetChannelArray(c);
            var frameData = new float[frames.Count][,];
            for (var f = 0; f < frames.Count; f++)
            {
                frameData[f] = frames[f].GetChannelArray(c);
            }
            var groups = Groups(mask[c]);
            var repair = new bool[groups.Count];
            ParallelFor.Run(groups.Count, g =>
            {
                Span<float> scratch = stackalloc float[BadPixelDetection.FlatNeighbourhoodSize];
                var series = new float[frameData.Length];
                var (bright, dark, swing) = (false, false, 0f);
                foreach (var (y, x) in groups[g])
                {
                    var ratio = BadPixelDetection.FlatRatio(data, y, x, step, scratch);
                    bright |= ratio > 1f + maxDeviation;
                    dark |= ratio < 1f - maxDeviation;
                    if (timeTest)
                    {
                        var n = 0;
                        foreach (var fd in frameData)
                        {
                            if (BadPixelDetection.FlatRatio(fd, y, x, step, scratch) is var r && float.IsFinite(r))
                            {
                                series[n++] = r;
                            }
                        }
                        if (n >= MinFramesForTime)
                        {
                            var some = series.AsSpan(0, n);
                            some.Sort();
                            var lo = (int)MathF.Floor(0.05f * (n - 1));
                            var hi = (int)MathF.Ceiling(0.95f * (n - 1));
                            swing = MathF.Max(swing, some[hi] - some[lo]);
                        }
                    }
                }
                repair[g] = (bright && dark) || swing > TransientSwing;
            });
            // Every replacement excludes every flagged photosite, and only flagged photosites are written, so the order
            // of the writes cannot change a value read.
            Span<float> neighbours = stackalloc float[BadPixelDetection.FlatNeighbourhoodSize];
            for (var g = 0; g < groups.Count; g++)
            {
                if (!repair[g])
                {
                    keptGroups++;
                    keptPhotosites += groups[g].Count;
                    continue;
                }
                repairedGroups++;
                foreach (var (y, x) in groups[g])
                {
                    if (BadPixelDetection.FlatNeighbourMedian(data, y, x, step, mask[c], neighbours) is var median && float.IsFinite(median))
                    {
                        data[y, x] = median;
                        repairedPhotosites++;
                    }
                }
            }
        }
        return new Outcome(repairedGroups, repairedPhotosites, keptGroups, keptPhotosites);
    }

    /// <summary>The set photosites of <paramref name="mask"/> as groups touching on a side or a corner: on a mosaic a
    /// droplet's core and ring are neighbours of every colour.</summary>
    private static List<List<(int Y, int X)>> Groups(BitMatrix mask)
    {
        var (h, w) = (mask.Rows, mask.Columns);
        var seen = new BitMatrix(h, w);
        var groups = new List<List<(int Y, int X)>>();
        var stack = new Stack<(int Y, int X)>();
        for (var y = 0; y < h; y++)
        {
            for (var x = mask.NextSetBit(y, 0); x >= 0; x = mask.NextSetBit(y, x + 1))
            {
                if (seen[y, x])
                {
                    continue;
                }
                var group = new List<(int Y, int X)>();
                seen[y, x] = true;
                stack.Push((y, x));
                while (stack.TryPop(out var p))
                {
                    group.Add(p);
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var (ny, nx) = (p.Y + dy, p.X + dx);
                            if (ny >= 0 && ny < h && nx >= 0 && nx < w && mask[ny, nx] && !seen[ny, nx])
                            {
                                seen[ny, nx] = true;
                                stack.Push((ny, nx));
                            }
                        }
                    }
                }
                groups.Add(group);
            }
        }
        return groups;
    }
}
