using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Local-only exploratory diagnostic: count how many Tycho-2 stars actually
/// fall inside a specific master FITS's field of view, broken down by gate
/// (in-FOV, has B-V photometry, has V_Mag). Used to diagnose the SPCC
/// match-count funnel without having to instrument production code.
///
/// <para>Parses the embedded <c>tyc2.bin.lz</c> binary directly rather than
/// going through <see cref="CelestialObjectDB"/> -- a fresh DB instance in
/// the test context doesn't populate <c>_tycho2Data</c> after
/// <c>InitDBAsync(waitForTycho2BulkLoad: true)</c> (separate bug to investigate),
/// so the diagnostic does its own binary walk.</para>
///
/// <para>Skipped silently in CI -- needs a local master FITS at a hard-coded
/// path (the SoL drizzle output sitting in <c>C:\temp\stack\output\</c>). To
/// run manually after a stack:</para>
/// <code>
/// dotnet test TianWen.Lib.Tests --filter FullyQualifiedName~Tycho2FovExploratory
/// </code>
/// </summary>
public class Tycho2FovExploratoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Diagnose_Tycho2_BulkLoad_State()
    {
        // Probe the CelestialObjectDB internals after a full init with
        // waitForTycho2BulkLoad: true. If the bulk task ran but _tycho2Data
        // is still null, the early-exit in ReadTycho2Bulk fired -- print the
        // task status + manifest names so we can see why.
        var db = new CelestialObjectDB();
        await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: System.Threading.CancellationToken.None);
        await db.EnsureTycho2DataLoadedAsync(System.Threading.CancellationToken.None);

        var state = db.Tycho2BulkLoadState;
        output.WriteLine($"DataLoaded={state.DataLoaded} StreamCount={state.StreamCount} IndexBuilt={state.IndexBuilt} BulkTaskRan={state.BulkTaskRan} TaskStatus={state.BulkTaskStatus}");
        output.WriteLine($"Tycho2StarCount={db.Tycho2StarCount}");

        // Also dump the manifest names that include "tyc2" to confirm the
        // assembly the DB is looking at actually contains the resource.
        var asm = typeof(CelestialObjectDB).Assembly;
        var names = asm.GetManifestResourceNames();
        foreach (var n in names.Where(n => n.Contains("tyc2", StringComparison.OrdinalIgnoreCase)))
        {
            output.WriteLine($"  manifest: {n}");
        }
        // And confirm the EndsWith predicate that ReadTycho2Bulk uses matches.
        var match = names.FirstOrDefault(p => p.EndsWith(".tyc2.bin.lz", StringComparison.Ordinal));
        output.WriteLine($"  FirstOrDefault(.tyc2.bin.lz): {match ?? "(null)"}");
    }

    [Fact]
    public async Task SoL_60s_Drizzle_TychoFovCount()
    {
        const string masterPath = @"C:\temp\stack\output\master_StatueofLibertyNebula_light_60s_-5C_g120_drizzle.fits";
        if (!File.Exists(masterPath))
        {
            output.WriteLine($"SKIP: master not found at {masterPath}");
            return;
        }

        // Read master + WCS from the FITS file produced by the stacking pipeline.
        Image.TryReadFitsFile(masterPath, out var master, out var wcs).ShouldBeTrue();
        master.ShouldNotBeNull();
        wcs.ShouldNotBeNull();
        var (w, h) = (master.Width, master.Height);
        output.WriteLine($"master: {w}x{h}, WCS present");

        // Production DB path now that the Tycho2StarCount lazy bug is fixed.
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        await db.EnsureTycho2DataLoadedAsync(TestContext.Current.CancellationToken);
        var catalogTotal = db.Tycho2StarCount;
        var stars = new Tycho2StarLite[catalogTotal];
        var copied = db.CopyTycho2Stars(stars);
        copied.ShouldBe(catalogTotal);

        // Funnel counters.
        int inFovTotal = 0;
        int inFovWithBV = 0;
        int inFovWithVMag = 0;
        int inFovWithBoth = 0;
        Span<int> magBins = stackalloc int[16];
        Span<int> magBinsWithBV = stackalloc int[16];

        for (var i = 0; i < copied; i++)
        {
            var star = stars[i];
            // Production code path: CopyTycho2Stars fills BMinusV=0.65 (solar
            // fallback) when BT is missing -- it never returns NaN for BV.
            // To match the SPCC pipeline's "real BV" gate, we treat 0.65f
            // EXACTLY as the synthesised default and bucket separately. In
            // practice few stars hit this branch (Tycho-2 has BT for most),
            // so the histogram is dominated by real measurements.
            var raDeg = star.RaHours * 15.0;
            var pixel = wcs.Value.SkyToPixel(raDeg, (double)star.DecDeg);
            if (pixel is not { } p) continue;
            var x0 = p.X - 1.0;
            var y0 = p.Y - 1.0;
            if (x0 < 0 || x0 >= w || y0 < 0 || y0 >= h) continue;

            inFovTotal++;
            var hasVMag = !float.IsNaN(star.VMag);
            // 0.65f is the synthetic solar-type default; treat anything else
            // as a real measurement.
            var hasBV = star.BMinusV != 0.65f && !float.IsNaN(star.BMinusV);
            if (hasBV) inFovWithBV++;
            if (hasVMag) inFovWithVMag++;
            if (hasBV && hasVMag) inFovWithBoth++;

            if (hasVMag)
            {
                var bin = (int)((star.VMag - 5.5f) * 2f);
                bin = Math.Clamp(bin, 0, magBins.Length - 1);
                magBins[bin]++;
                if (hasBV) magBinsWithBV[bin]++;
            }
        }

        output.WriteLine($"Tycho-2 catalog total: {catalogTotal}");
        output.WriteLine($"");
        output.WriteLine($"SoL FOV Tycho-2 funnel:");
        output.WriteLine($"  in-FOV total       : {inFovTotal}");
        output.WriteLine($"  in-FOV w/ V_Mag    : {inFovWithVMag}");
        output.WriteLine($"  in-FOV w/ B-V      : {inFovWithBV}  ({(100.0 * inFovWithBV / Math.Max(1, inFovTotal)):F1} % of in-FOV)");
        output.WriteLine($"  in-FOV w/ both     : {inFovWithBoth}  ({(100.0 * inFovWithBoth / Math.Max(1, inFovTotal)):F1} % of in-FOV)");
        output.WriteLine($"");
        output.WriteLine($"V-mag histogram of in-FOV stars (with B-V coverage):");
        for (var b = 0; b < magBins.Length; b++)
        {
            if (magBins[b] == 0) continue;
            var magLo = 5.5f + b * 0.5f;
            var magHi = magLo + 0.5f;
            var bvPct = 100.0 * magBinsWithBV[b] / magBins[b];
            output.WriteLine($"  [{magLo:F1} .. {magHi:F1}]  total={magBins[b],4}  withBV={magBinsWithBV[b],4}  ({bvPct,5:F1} %)");
        }

        inFovTotal.ShouldBeGreaterThan(0);
    }
}
