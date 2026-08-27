using System;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>Probe, not an assertion (embedded data only, so not gated): what the SPCC model says stars of known
/// B-V should DISPLAY as against each white reference, per sensor + filter, and which Pickles star the Sb
/// template resembles by shape. Read it before suspecting the white reference or the SED library.</summary>
public class SpccWhiteReferenceProbe(ITestOutputHelper output)
{
    private static (double R, double B)? Ratios(FilterCurve sed, (FilterCurve R, FilterCurve G, FilterCurve B) t)
    {
        var g = FilterCurve.IntegrateSedThroughput(sed, t.G);
        if (g <= 0) return null;
        return (FilterCurve.IntegrateSedThroughput(sed, t.R) / g, FilterCurve.IntegrateSedThroughput(sed, t.B) / g);
    }

    [Fact]
    public async Task ReportExpectedDisplayColoursAgainstTheWhite()
    {
        var ct = TestContext.Current.CancellationToken;
        await FilterCurveDatabase.LoadAsync(ct);
        output.WriteLine("SED names: " + string.Join(", ", FilterCurveDatabase.AllSeds.Select(s => s.Name)));
        output.WriteLine("Filter names with JOHNSON/BESSELL/UBV: " + string.Join(", ",
            FilterCurveDatabase.AllFilters.Select(f => f.Name).Where(n => n.Contains("JOHNSON", StringComparison.OrdinalIgnoreCase)
                || n.Contains("BESSELL", StringComparison.OrdinalIgnoreCase) || n.Contains("UBV", StringComparison.OrdinalIgnoreCase)
                || n.Contains("PHOTOMETRIC", StringComparison.OrdinalIgnoreCase))));

        var metas = new (string Label, ImageMeta Meta)[]
        {
            ("SV605CC bare", new ImageMeta { Instrument = "SVBONY SV605CC", SensorType = SensorType.Color }),
            ("SV605CC + L-Quad", new ImageMeta { Instrument = "SVBONY SV605CC", SensorType = SensorType.Color, Filter = Filter.FromName("Optolong L-Quad Enhance") with { RawName = "Optolong L-Quad Enhance" } }),
            ("QHY294PROC + LPS-D3", new ImageMeta { Instrument = "QHY294PROC", SensorType = SensorType.Color, Filter = Filter.FromName("IDAS LPS-D3") with { RawName = "IDAS LPS-D3" } }),
        };
        string[] whites = ["GALAXY_SB", "G2V", "GALAXY_SA", "GALAXY_SC"];
        double[] bvs = [0.0, 0.3, 0.5, 0.65, 0.82, 1.0, 1.2, 1.5];
        foreach (var (label, meta) in metas)
        {
            var t = FilterCurveDatabase.BuildChannelThroughputs(meta);
            if (t is not { } tt) { output.WriteLine($"{label}: no throughput"); continue; }
            output.WriteLine($"== {label}: tsys {tt.R.Name} | {tt.G.Name} | {tt.B.Name}");
            foreach (var wn in whites)
            {
                if (!FilterCurveDatabase.TryGetSedByName(wn, out var wsed) || Ratios(wsed, tt) is not { } w) { output.WriteLine($"  white {wn}: unresolved"); continue; }
                output.WriteLine($"  white {wn}: instrumental R/G {w.R:F4} B/G {w.B:F4}");
                foreach (var bv in bvs)
                {
                    if (!FilterCurveDatabase.TryGetSedByBv(bv, out var sed)) { output.WriteLine($"    B-V {bv:F2}: TryGetSedByBv FAILED"); continue; }
                    if (Ratios(sed, tt) is not { } s) { output.WriteLine($"    B-V {bv:F2} -> {sed.Name}: green integrates to nothing; sed range {sed.WavelengthAt(0):F0}..{sed.WavelengthAt(sed.Count - 1):F0}, tsysG range {tt.G.WavelengthAt(0):F0}..{tt.G.WavelengthAt(tt.G.Count - 1):F0}"); continue; }
                    output.WriteLine($"    B-V {bv:F2} -> {sed.Name,-6} instrumental R/G {s.R:F4} B/G {s.B:F4}   DISPLAY R {s.R / w.R:F3} B {s.B / w.B:F3}");
                }
            }
        }

        // Shape of the Sb template against the Pickles stars, normalised at 5500 A: which star is it closest to?
        if (FilterCurveDatabase.TryGetSedByName("GALAXY_SB", out var sb))
        {
            output.WriteLine($"GALAXY_SB: {sb.Count} samples, {sb.WavelengthAt(0):F0}..{sb.WavelengthAt(sb.Count - 1):F0} A; value at 4000/4500/5500/6500/7000: "
                + string.Join(" ", new[] { 4000.0, 4500, 5500, 6500, 7000 }.Select(l => (sb.Interpolate(l) / sb.Interpolate(5500)).ToString("F3"))));
            var best = FilterCurveDatabase.AllSeds.Where(s => !s.Name.StartsWith("GALAXY", StringComparison.Ordinal))
                .Select(s =>
                {
                    var err = 0.0; var n = 0;
                    for (var l = 4000.0; l <= 7000; l += 50)
                    {
                        var a = sb.Interpolate(l) / sb.Interpolate(5500);
                        var b = s.Interpolate(l) / s.Interpolate(5500);
                        if (a > 0 && b > 0) { var d = Math.Log(a / b); err += d * d; n++; }
                    }
                    return (s.Name, Rms: n > 0 ? Math.Sqrt(err / n) : double.PositiveInfinity, Star: s);
                })
                .OrderBy(x => x.Rms).Take(5).ToList();
            foreach (var (name, rms, star) in best)
            {
                output.WriteLine($"  closest Pickles by shape 4000-7000: {name} rms(log) {rms:F3}; star value at 4000/4500/6500/7000: "
                    + string.Join(" ", new[] { 4000.0, 4500, 6500, 7000 }.Select(l => (star.Interpolate(l) / star.Interpolate(5500)).ToString("F3"))));
            }
            if (FilterCurveDatabase.TryGetSedByName("G2V", out var g2v) && FilterCurveDatabase.TryGetSedByName("K0V", out var k0v))
            {
                output.WriteLine("G2V value at 4000/4500/6500/7000: " + string.Join(" ", new[] { 4000.0, 4500, 6500, 7000 }.Select(l => (g2v.Interpolate(l) / g2v.Interpolate(5500)).ToString("F3"))));
                output.WriteLine("K0V value at 4000/4500/6500/7000: " + string.Join(" ", new[] { 4000.0, 4500, 6500, 7000 }.Select(l => (k0v.Interpolate(l) / k0v.Interpolate(5500)).ToString("F3"))));
            }
        }
    }
}
