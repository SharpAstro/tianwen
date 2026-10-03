using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.StarRemoval;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Env-gated diagnostic: does the starless builder's finder read the noise of a bright nebula right? On the Orion
/// master's M42 core the plate carries clusters of dark dots where the master shows smooth nebula and no star: sources
/// of significance 5 to 10, many from the second pass, fitted and subtracted. If the finder's local noise (the
/// background map's rms) is under the nebula's real noise there, its noise peaks read as stars. This runs the real
/// finder and sets, at each detection, the noise the plane's own pixel-to-pixel differences give
/// (<see cref="PointSourceFinder.DifferenceNoiseMap"/>, blind to structure) against the rms map it divided by, inside a
/// box and outside it.
/// <para>Set <c>TIANWEN_FINDER_NOISE_MASTER</c> to a master FITS; optionally <c>TIANWEN_FINDER_NOISE_BOX</c> as
/// <c>x,y,half</c> (default the Orion master's M42 core, 1943,1663,300) and <c>TIANWEN_FINDER_NOISE_FWHM</c> (default
/// 2.5).</para>
/// </summary>
[Collection("Imaging")]
public sealed class FinderNoiseOnNebulaProbe(ITestOutputHelper output)
{
    [Fact]
    public void TheFindersNoiseAgainstTheLocalNoiseOnANebula()
    {
        var path = Environment.GetEnvironmentVariable("TIANWEN_FINDER_NOISE_MASTER");
        Assert.SkipWhen(string.IsNullOrEmpty(path), "TIANWEN_FINDER_NOISE_MASTER not set");
        var box = (Environment.GetEnvironmentVariable("TIANWEN_FINDER_NOISE_BOX") ?? "1943,1663,300")
            .Split(',').Select(static v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var fwhm = double.Parse(Environment.GetEnvironmentVariable("TIANWEN_FINDER_NOISE_FWHM") ?? "2.5", CultureInfo.InvariantCulture);
        Assert.True(Image.TryReadFitsFile(path!, out var image), "the master did not read");
        var (channels, width, height) = image.Shape;
        var lum = new float[width * height];
        for (var c = 0; c < channels; c++)
        {
            var plane = image.GetChannelSpan(c);
            for (var i = 0; i < lum.Length; i++)
            {
                lum[i] += plane[i] / channels;
            }
        }
        var absent = image.AbsentPixels();
        var (found, sky) = PointSourceFinder.Find(lum, width, height, absent, fwhm, 5f);
        var rms = new float[lum.Length];
        var level = new float[lum.Length];
        sky.FillRms(rms);
        sky.FillBackground(level);
        var local = PointSourceFinder.DifferenceNoiseMap(lum, width, height, absent, PointSourceFinder.SkyBlockFor(fwhm));
        Assert.NotNull(local);

        bool Inside(int x, int y) => Math.Abs(x - box[0]) < box[2] && Math.Abs(y - box[1]) < box[2];
        foreach (var inside in new[] { true, false })
        {
            var ratio = new List<float>();
            var levels = new List<float>();
            var count = 0;
            foreach (var s in found)
            {
                if (Inside(s.PeakX, s.PeakY) != inside || s.Significance is < 5f or >= 10f)
                {
                    continue;
                }
                var i = s.PeakY * width + s.PeakX;
                count++;
                if (rms[i] > 0)
                {
                    ratio.Add(local[i] / rms[i]);
                }
            }
            // The same ratio over the area itself, sampled on a grid, so a detection's ratio can be read against its
            // region's.
            var area = new List<float>();
            var pixels = 0;
            for (var y = 0; y < height; y += 7)
            {
                for (var x = 0; x < width; x += 7)
                {
                    var i = y * width + x;
                    if (Inside(x, y) != inside || (absent is { } a && a[y, x]) || !(rms[i] > 0))
                    {
                        continue;
                    }
                    area.Add(local[i] / rms[i]);
                    levels.Add(level[i]);
                    pixels += 49;
                }
            }
            static float Median(List<float> v) => v.Count == 0 ? float.NaN : StatisticsHelper.NthSmallest(v.ToArray(), v.Count / 2);
            output.WriteLine($"{(inside ? "inside the box " : "outside the box")}: {count} detections of significance 5-10, " +
                $"{1e4 * count / Math.Max(1, pixels):F2} per 10^4 px; local noise over the finder's rms at them {Median(ratio):F2}, " +
                $"over the area {Median(area):F2}; sky level {Median(levels):E3}");
        }
    }
}
