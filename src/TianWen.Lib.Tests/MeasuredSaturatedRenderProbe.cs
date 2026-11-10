using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// On demand (TIANWEN_HALOPROBE_BAKE, TIANWEN_HALOPROBE_SESSIONS): does a saturated star drawn with --saturated-measured render
/// as its source star measured? Builds a val master's population as the exporter does, plans draws with saturated stars at the
/// master's own share, renders each saturated star alone on an empty plate, and reads its linear light at 5 / 8 / 12 px (ring
/// medians) beside the master's own saturated stars read the same way.
/// </summary>
[Collection("Imaging")]
public sealed class MeasuredSaturatedRenderProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task AMeasuredSaturatedStarRendersAsItsSourceStar()
    {
        var bake = Environment.GetEnvironmentVariable("TIANWEN_HALOPROBE_BAKE");
        var sessions = Environment.GetEnvironmentVariable("TIANWEN_HALOPROBE_SESSIONS");
        Assert.SkipWhen(bake is null || sessions is null, "TIANWEN_HALOPROBE_* not set");
        var ct = TestContext.Current.CancellationToken;
        var psfStore = await DatasetPsfStore.ReadAsync(Path.Combine(bake, "stats", DatasetPsfStore.FileName), null, ct);
        var context = new DatasetDegradationExporter.StarsContext(Path.Combine(bake, "starless", "plates"), psfStore);
        foreach (var prefix in sessions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sessionId = psfStore.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !k.Contains("|flip=") && File.Exists(RetainedMasterStore.PathFor(bake, k)))
                .Order(StringComparer.Ordinal).First();
            Assert.True(RetainedMasterStore.TryRead(bake, sessionId, out var master));
            Assert.True(Image.TryReadFitsFile(context.PlatePath(sessionId), out var plate));
            var catalogue = await StarlessCatalogue.ReadAsync(context.CataloguePath(sessionId), ct);
            var field = await StarlessFieldProfile.ReadAsync(context.ProfilePath(sessionId), ct);
            var channels = master.ChannelCount;
            var psf = context.ChannelPsf(sessionId, channels, out _);
            var divisor = DatasetTileExporter.UnitDivisor(master);
            var absent = plate.AbsentPixels();
            var population = InjectionPopulation.Build(catalogue, master, plate, 1.0 / divisor, psf, absent, field, measuredSaturated: true);

            const int size = 161, half = size / 2;
            // A plate at the master's sky (unit scale): a star's clip level includes its sky, so on an empty plate a clipped
            // plateau would stand a sky above the real one.
            var empty = Enumerable.Range(0, channels).Select(c =>
            {
                var plane = plate.GetChannelSpan(c).ToArray().Where(float.IsFinite).Order().ToArray();
                return Enumerable.Repeat((float)(plane[plane.Length / 2] / divisor), size * size).ToArray();
            }).ToArray();
            var rendered = new List<double[]>();
            var planAmp = new List<double>();
            var planClip = new List<double>();
            var unmeasured = new List<double[]>();
            var rng = new Random(3);
            for (var draw = 0; draw < 3000 && rendered.Count < 1500; draw++)
            {
                var plan = population.Plan(rng.Next(master.Width - 300), rng.Next(master.Height - 300), 256, 0, InjectionPlacement.Random,
                    StarProfileFamily.Field, 1.0, new Random(draw), saturatedAtOwnShare: true);
                foreach (var s in plan.Stars.Where(static s => s.Saturated && !s.Lobe))
                {
                    planAmp.Add(s.Amplitudes[Math.Min(1, channels - 1)]);
                    planClip.Add(s.ClipLevels[Math.Min(1, channels - 1)]);
                }
                foreach (var s in plan.Stars.Where(static s => s.Saturated && !s.Lobe))
                {
                    var alone = s with { X = half + (s.X - Math.Round(s.X)), Y = half + (s.Y - Math.Round(s.Y)) };
                    var render = StarInjection.Render(empty, size, size, null, [alone], [.. Enumerable.Repeat(1e-9, channels)], new Random(draw));
                    (alone.Profiles[0].Outer.Source == "measured saturated star" ? rendered : unmeasured).Add(Rings(render.Planes[Math.Min(1, channels - 1)], size, alone.X, alone.Y));
                }
            }
            // The master's own saturated stars, read the same way (ring medians against a 32-36 px median sky), green.
            var real = new List<double[]>();
            var pairs = new List<double[]>();
            var pairedRendered = new List<double[]>();
            var pairAmp = new List<double>();
            var pairClip = new List<double>();
            var drawnProfiles = Enumerable.Range(0, channels)
                .Select(c => new StarProfile(StarProfileFamily.Field, field!.Channels[c].Fwhm, field.Channels[c].Beta, 1.0, 0.0, field.Channels[c].Table, field.Channels[c].TableAlpha))
                .ToArray();
            var g = master.GetChannelSpan(Math.Min(1, channels - 1)).ToArray();
            // The stars the measured pool can hold: subtracted, saturated, an amplitude per channel, and a whole finite stamp.
            var planesAll = Enumerable.Range(0, channels).Select(c => master.GetChannelSpan(c).ToArray()).ToArray();
            foreach (var s in catalogue.Where(s => s.Saturated && s.Outcome == StarFitOutcome.Subtracted && s.ChannelAmplitudes.Length == channels
                && s.ChannelAmplitudes.All(float.IsFinite)))
            {
                var (ix, iy) = ((int)Math.Round(s.X), (int)Math.Round(s.Y));
                if (ix < 40 || iy < 40 || ix >= master.Width - 40 || iy >= master.Height - 40)
                {
                    continue;
                }
                var finite = true;
                for (var c = 0; c < channels && finite; c++)
                {
                    for (var y = iy - 36; y <= iy + 36 && finite; y++)
                    {
                        for (var x = ix - 36; x <= ix + 36; x++)
                        {
                            if (!float.IsFinite(planesAll[c][(y * master.Width) + x]))
                            {
                                finite = false;
                                break;
                            }
                        }
                    }
                }
                if (!finite)
                {
                    continue;
                }
                var stamp = new float[81 * 81];
                for (var y = 0; y < 81; y++)
                {
                    for (var x = 0; x < 81; x++)
                    {
                        stamp[(y * 81) + x] = g[((iy - 40 + y) * master.Width) + ix - 40 + x];
                    }
                }
                // On the unit scale the injector draws on, so the absolute light compares too.
                var source = Rings(stamp, 81, 40 + (s.X - ix), 40 + (s.Y - iy)).Select(v => v / divisor).ToArray();
                real.Add(source);
                // The same star measured and rendered alone: its rendered light over its own, ring by ring (pairs, no population).
                var clips = Enumerable.Range(0, channels).Select(c =>
                {
                    double best = double.NegativeInfinity;
                    for (var y = iy - 3; y <= iy + 3; y++)
                    {
                        for (var x = ix - 3; x <= ix + 3; x++)
                        {
                            if (((x - s.X) * (x - s.X)) + ((y - s.Y) * (y - s.Y)) <= 9)
                            {
                                best = Math.Max(best, planesAll[c][(y * master.Width) + x]);
                            }
                        }
                    }
                    return best / divisor;
                }).ToImmutableArray();
                if (InjectionPopulation.MeasureSaturated(planesAll, master.Width, master.Height, s.X, s.Y, 1.0 / divisor, drawnProfiles, clips) is { } m)
                {
                    var star = new InjectedStar(half + (s.X - ix), half + (s.Y - iy), m.Amplitudes,
                        [.. drawnProfiles.Select((p, c) => p with { Outer = m.Halo[c] })], true, m.Clips);
                    var render = StarInjection.Render(empty, size, size, null, [star], [.. Enumerable.Repeat(1e-9, channels)], new Random(1));
                    var r = Rings(render.Planes[Math.Min(1, channels - 1)], size, star.X, star.Y);
                    pairs.Add([.. r.Zip(source, (a, b) => a / b)]);
                    pairedRendered.Add(r);
                    pairAmp.Add(m.Amplitudes[Math.Min(1, channels - 1)]);
                    pairClip.Add(m.Clips[Math.Min(1, channels - 1)]);
                }
            }
            output.WriteLine($"  pairs n={pairs.Count}  rendered/own at 5, 8, 12 px: {Med(pairs.Select(p => p[0])):F3} {Med(pairs.Select(p => p[1])):F3} {Med(pairs.Select(p => p[2])):F3}");
            // The population as well as the shape: the median absolute light at 5, 8 and 12 px over every star, x1000 on the
            // unit scale, beside the ratios.
            string Line(string name, List<double[]> v) => v.Count == 0 ? $"{name}: none" :
                $"{name} n={v.Count,4}  8/5 {Med(v.Select(r => r[1] / r[0])):F3}  12/5 {Med(v.Select(r => r[2] / r[0])):F4}  20/5 {Med(v.Select(r => r[3] / r[0])):F4}" +
                $"  abs5 {1000 * Med(v.Select(r => r[0])):F3}  abs8 {1000 * Med(v.Select(r => r[1])):F3}  abs12 {1000 * Med(v.Select(r => r[2])):F4}";
            output.WriteLine($"{sessionId}  saturated pool {population.SaturatedPool}");
            output.WriteLine("  " + Line("rendered", rendered));
            output.WriteLine("  " + Line("unmeasrd", unmeasured));
            output.WriteLine("  " + Line("master  ", real));
            output.WriteLine("  " + Line("paired  ", pairedRendered));
            output.WriteLine($"  amplitude median plan {Med(planAmp):G4} paired {Med(pairAmp):G4}   clip median plan {Med(planClip):G4} paired {Med(pairClip):G4}");
        }
    }

    private static double Med(IEnumerable<double> v)
    {
        var a = v.Where(double.IsFinite).Order().ToArray();
        return a.Length == 0 ? double.NaN : a[a.Length / 2];
    }

    // Ring medians at 5, 8, 12 and 20 px less a 32-36 px median sky.
    private static double[] Rings(float[] plane, int size, double cx, double cy)
    {
        var bins = new Dictionary<int, List<float>> { [5] = [], [8] = [], [12] = [], [20] = [], [-1] = [] };
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var r = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                var k = (int)Math.Round(r);
                if (r >= 32 && r <= 36)
                {
                    bins[-1].Add(plane[(y * size) + x]);
                }
                else if (bins.TryGetValue(k, out var list) && k > 0)
                {
                    list.Add(plane[(y * size) + x]);
                }
            }
        }
        var sky = Med(bins[-1].Select(static v => (double)v));
        return [Med(bins[5].Select(static v => (double)v)) - sky, Med(bins[8].Select(static v => (double)v)) - sky,
            Med(bins[12].Select(static v => (double)v)) - sky, Med(bins[20].Select(static v => (double)v)) - sky];
    }
}
