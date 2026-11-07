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
            var unmeasured = new List<double[]>();
            var rng = new Random(3);
            for (var draw = 0; draw < 400 && rendered.Count < 300; draw++)
            {
                var plan = population.Plan(rng.Next(master.Width - 300), rng.Next(master.Height - 300), 256, 0, InjectionPlacement.Random,
                    StarProfileFamily.Field, 1.0, new Random(draw), saturatedAtOwnShare: true);
                foreach (var s in plan.Stars.Where(static s => s.Saturated && !s.Lobe))
                {
                    var alone = s with { X = half + (s.X - Math.Round(s.X)), Y = half + (s.Y - Math.Round(s.Y)) };
                    var render = StarInjection.Render(empty, size, size, null, [alone], [.. Enumerable.Repeat(1e-9, channels)], new Random(draw));
                    (alone.Profiles[0].Outer.Source == "measured saturated star" ? rendered : unmeasured).Add(Rings(render.Planes[Math.Min(1, channels - 1)], size, alone.X, alone.Y));
                }
            }
            // The master's own saturated stars, read the same way (ring medians against a 32-36 px median sky), green.
            var real = new List<double[]>();
            var g = master.GetChannelSpan(Math.Min(1, channels - 1)).ToArray();
            foreach (var s in catalogue.Where(s => InjectionPopulation.InSaturatedPool(s, channels)))
            {
                var (ix, iy) = ((int)Math.Round(s.X), (int)Math.Round(s.Y));
                if (ix < 40 || iy < 40 || ix >= master.Width - 40 || iy >= master.Height - 40)
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
                real.Add(Rings(stamp, 81, 40 + (s.X - ix), 40 + (s.Y - iy)));
            }
            string Line(string name, List<double[]> v) => v.Count == 0 ? $"{name}: none" :
                $"{name} n={v.Count,4}  8/5 {Med(v.Select(r => r[1] / r[0])):F3}  12/5 {Med(v.Select(r => r[2] / r[0])):F4}  20/5 {Med(v.Select(r => r[3] / r[0])):F4}";
            output.WriteLine($"{sessionId}");
            output.WriteLine("  " + Line("rendered", rendered));
            output.WriteLine("  " + Line("unmeasrd", unmeasured));
            output.WriteLine("  " + Line("master  ", real));
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
