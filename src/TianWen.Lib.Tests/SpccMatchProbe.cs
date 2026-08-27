using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.ColorCalibration;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Probe, not an assertion: replicates SPCC's Tycho-2 match on a real master and reports the matched
/// B-V population, the per-B-V-bin observed colour against the SED model's expected display colour,
/// and the implied white balance per bin. A correct pipeline shows the implied WB FLAT across bins;
/// the 2026-08-27 investigation used it to find the many-to-one matcher (observed colour flat across
/// bins) and the normaliser's min anchor (implied WB drifting x3 across bins). Env-gated on
/// TIANWEN_COMET_PROBE_ROOT (the comet-feat working copy); skips without it.
/// </summary>
public class SpccMatchProbe(ITestOutputHelper output)
{
    [Theory]
    [InlineData("c2025r2-swan/runs/starless/master_C2025R2SWAN_light_30s_4C_g120_composite.fits", "SVBONY SV605CC", "")]
    [InlineData("c2025r2-swan/runs/starless/master_C2025R2SWAN_light_30s_4C_g120_composite.fits", "SVBONY SV605CC", "Optolong L-Quad Enhance")]
    [InlineData("10p-tempel2/runs/onerun/master_10PTempel2_light_60s_-5C_g1600_composite.fits", "QHY294PROC", "IDAS LPS-D3")]
    [InlineData("c2025r2-swan/runs/starless-floor/master_C2025R2SWAN_light_30s_4C_g120_composite.fits", "SVBONY SV605CC", "Optolong L-Quad Enhance")]
    [InlineData("10p-tempel2/runs/onerun-floor/master_10PTempel2_light_60s_-5C_g1600_composite.fits", "QHY294PROC", "IDAS LPS-D3")]
    public async Task ReportMatchedPopulation(string path, string instrument, string filterName)
    {
        // Env-gated like SpccReachabilityProbe: the masters live in the user's working copy, not the repo.
        var root = Environment.GetEnvironmentVariable("TIANWEN_COMET_PROBE_ROOT");
        Assert.SkipWhen(string.IsNullOrEmpty(root), "set TIANWEN_COMET_PROBE_ROOT to the comet-feat working copy to run this probe");
        path = Path.Combine(root!, path);
        Assert.SkipWhen(!File.Exists(path), "master not present");
        var ct = TestContext.Current.CancellationToken;
        Image.TryReadFitsFile(path, out var image, out var wcs).ShouldBeTrue();
        image.ShouldNotBeNull();
        wcs.ShouldNotBeNull();
        var w = wcs.Value;
        await FilterCurveDatabase.LoadAsync(ct);
        ICelestialObjectDB db = new CelestialObjectDB();
        await db.InitDBAsync(waitForTycho2BulkLoad: true, ct);

        // Known stars: is the catalogue's B-V on the Johnson scale?
        foreach (var (name, lit) in new[] { ("TYC 3105-2070-1", 0.00), ("TYC 1472-1436-1", 1.23), ("TYC 1266-1416-1", 1.54), ("TYC 3358-3141-1", 0.80), ("TYC 5547-1518-1", -0.24), ("TYC 6803-2158-1", 1.83), ("TYC 3574-3347-1", 0.09), ("TYC 4004-2138-1", 0.00) })
        {
            if (db.TryLookupByIndex(name, out var obj))
                output.WriteLine($"catalogue {name}: V {obj.V_Mag:F2} B-V {(double)obj.BMinusV:F2} (literature {lit:F2})");
            else
                output.WriteLine($"catalogue {name}: not found");
        }

        var stars = await image.FindStarsAsync(channel: image.ReferenceStarChannel, snrMin: 5f, maxStars: 500, minStars: 50, maxRetries: 0, cancellationToken: ct);
        var dtYr = image.ImageMeta.ExposureStartTime.Year > 1900 ? image.ImageMeta.ExposureStartTime.JulianYearsSinceJ2000() : 0.0;
        var (matches, funnel) = Tycho2ColorCalibration.MatchStars(stars, w, db, 5f, 1.5f, dtYr, image.Width, image.Height);
        output.WriteLine($"{Path.GetFileName(path)}: {stars.Count} detected, {matches.Count} matched; funnel {funnel}");

        // Whole catalogue inside the footprint, independent of matching.
        var all = new Tycho2StarLite[db.Tycho2StarCount];
        var n = db.CopyTycho2Stars(all);
        var inField = new List<(double Bv, double V)>();
        for (var i = 0; i < n; i++)
        {
            var s = all[i];
            if (float.IsNaN(s.VMag)) continue;
            if (w.SkyToPixel(s.RaHours * 15.0, s.DecDeg) is { } p && p.X >= 0 && p.X < image.Width && p.Y >= 0 && p.Y < image.Height)
                inField.Add((s.BMinusV, s.VMag));
        }
        output.WriteLine($"Tycho-2 in footprint: B-V {Pct(inField.Select(x => x.Bv))}; V {Pct(inField.Select(x => x.V))}");
        output.WriteLine($"matched: B-V {Pct(matches.Select(m => (double)m.Tycho.BMinusV))}; V {Pct(matches.Select(m => (double)m.Tycho.V_Mag))}");

        // Per-star photometry exactly as SPCC does it (r=6, annulus 8..11) and the implied WB per B-V bin.
        var meta = image.ImageMeta with { Instrument = instrument, SensorType = SensorType.Color, Filter = filterName.Length > 0 ? Filter.FromName(filterName) with { RawName = filterName } : Filter.None };
        var t = FilterCurveDatabase.BuildChannelThroughputs(meta);
        t.ShouldNotBeNull();
        var tt = t.Value;
        FilterCurveDatabase.TryGetSedByName("GALAXY_SB", out var sbSed).ShouldBeTrue();
        var white = Ratios(sbSed, tt)!.Value;
        output.WriteLine($"throughput {tt.G.Name}; Sb white instrumental R/G {white.R:F4} B/G {white.B:F4}");
        var rows = new List<Row>();
        foreach (var (star, tycho) in matches)
        {
            if (Half.IsNaN(tycho.BMinusV)) continue;
            var (cx, cy) = (star.XCentroid, star.YCentroid);
            double oR = 0, oG = 0, oB = 0, bR = 0, bG = 0, bB = 0;
            var ap = 0;
            var bg = 0;
            var bad = false;
            for (var y = (int)(cy - 11); y <= (int)(cy + 11); y++)
            {
                for (var x = (int)(cx - 11); x <= (int)(cx + 11); x++)
                {
                    if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) { bad = true; continue; }
                    var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    var r = image[0, y, x];
                    var g = image[1, y, x];
                    var b = image[2, y, x];
                    if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b)) { bad = true; continue; }
                    if (d <= 6) { oR += r; oG += g; oB += b; ap++; }
                    else if (d >= 8 && d <= 11) { bR += r; bG += g; bB += b; bg++; }
                }
            }
            if (bad || ap < 3 || bg < 5) continue;
            var nR = oR - bR / bg * ap;
            var nG = oG - bG / bg * ap;
            var nB = oB - bB / bg * ap;
            if (nR <= 0 || nG <= 0 || nB <= 0) continue;
            if (!FilterCurveDatabase.TryGetSedByBv((double)tycho.BMinusV, out var sed) || Ratios(sed, tt) is not { } e) continue;
            var pk = 0.0;
            for (var y = (int)(cy - 6); y <= (int)(cy + 6); y++)
            {
                for (var x = (int)(cx - 6); x <= (int)(cx + 6); x++)
                {
                    if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) continue;
                    pk = Math.Max(pk, Math.Max(image[0, y, x], Math.Max(image[1, y, x], image[2, y, x])));
                }
            }
            rows.Add(new Row((double)tycho.BMinusV, (double)tycho.V_Mag, nR / nG, nB / nG, e.R / white.R, e.B / white.B, pk, nG, (image[1, (int)cy, (int)cx] - bG / bg) / nG));
        }
        var peakMax = rows.Max(r => r.Peak);
        // Peak-to-flux of the fainter half is the PSF's own value; a clipped core falls below it.
        var faintHalf = rows.OrderBy(r => r.FluxG).Take(rows.Count / 2).Select(r => r.PeakToFluxG).OrderBy(x => x).ToArray();
        var psfPeakToFlux = faintHalf[faintHalf.Length / 2];
        output.WriteLine($"{rows.Count} photometry rows; frame peak {peakMax:F2}; faint-half peak/flux {psfPeakToFlux:F4}");
        var criteria = new (string Name, Func<Row, bool> Keep)[]
        {
            ("none", _ => true),
            ("peak<0.98max", r => r.Peak < 0.98 * peakMax),
            ("peak<0.70max", r => r.Peak < 0.70 * peakMax),
            ("peak<0.50max", r => r.Peak < 0.50 * peakMax),
            ("peak/flux>0.6psf", r => r.PeakToFluxG > 0.6 * psfPeakToFlux),
            ("peak/flux>0.8psf", r => r.PeakToFluxG > 0.8 * psfPeakToFlux),
        };
        foreach (var (name, keep) in criteria)
        {
            var kept = rows.Where(keep).ToArray();
            output.WriteLine($"-- criterion {name}: {kept.Length} stars; overall WB R {Med(kept, r => r.ExpR / r.ObsR):F3} B {Med(kept, r => r.ExpB / r.ObsB):F3}");
            foreach (var bin in kept.GroupBy(r => Math.Floor(r.Bv / 0.3) * 0.3).OrderBy(g => g.Key))
            {
                var a = bin.ToArray();
                if (a.Length < 8) continue;
                output.WriteLine($"     B-V {bin.Key,5:F1}..{bin.Key + 0.3,4:F1} n={a.Length,4}: obs {Med(a, r => r.ObsR):F3} {Med(a, r => r.ObsB):F3} | exp {Med(a, r => r.ExpR):F3} {Med(a, r => r.ExpB):F3} | WB {Med(a, r => r.ExpR / r.ObsR):F3} {Med(a, r => r.ExpB / r.ObsB):F3}");
            }
        }
        foreach (var bin in rows.GroupBy(r => Math.Floor(r.V)).OrderBy(g => g.Key))
        {
            var a = bin.ToArray();
            output.WriteLine($"  V {bin.Key,3:F0}..{bin.Key + 1,3:F0} n={a.Length,4}: median B-V {Med(a, r => r.Bv):F2}; peak/max {Med(a, r => r.Peak) / peakMax:F2}; peak/flux vs psf {Med(a, r => r.PeakToFluxG) / psfPeakToFlux:F2}; WB {Med(a, r => r.ExpR / r.ObsR):F3} {Med(a, r => r.ExpB / r.ObsB):F3}");
        }
    }

    private sealed record Row(double Bv, double V, double ObsR, double ObsB, double ExpR, double ExpB, double Peak, double FluxG, double PeakToFluxG);

    private static double Med(Row[] a, Func<Row, double> f)
    {
        var v = a.Select(f).OrderBy(x => x).ToArray();
        return v.Length == 0 ? double.NaN : v[v.Length / 2];
    }

    private static string Pct(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToArray();
        if (a.Length == 0) return "n/a";
        double P(double q) => a[(int)Math.Clamp(q * (a.Length - 1), 0, a.Length - 1)];
        return $"p10 {P(0.1):F2} p25 {P(0.25):F2} p50 {P(0.5):F2} p75 {P(0.75):F2} p90 {P(0.9):F2} (n={a.Length})";
    }

    private static (double R, double B)? Ratios(FilterCurve sed, (FilterCurve R, FilterCurve G, FilterCurve B) t)
    {
        var g = FilterCurve.IntegrateSedThroughput(sed, t.G);
        if (g <= 0) return null;
        return (FilterCurve.IntegrateSedThroughput(sed, t.R) / g, FilterCurve.IntegrateSedThroughput(sed, t.B) / g);
    }
}
