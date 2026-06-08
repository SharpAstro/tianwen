using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Guards that the Tycho-2 Supplement-1 stars are folded into the rendered star
/// buffer. The very brightest stars (Sirius, Vega, Antares, ...) saturate the
/// Tycho detector and live in suppl_1.dat, NOT the main tyc2.dat. If the bake
/// ever drops the supplement again, every first-magnitude star vanishes from the
/// sky map even though its catalogue entry (mag/position) is still correct.
/// </summary>
[Collection("Catalog")]
public class Tycho2SupplementRenderTests(ITestOutputHelper output)
{
    // First-magnitude stars that are ALL Supplement-1 entries (absent from the
    // main Tycho-2 catalogue). HIP number + common name for diagnostics.
    private static readonly (int Hip, string Name)[] FamousBrightStars =
    [
        (32349, "Sirius"),
        (30438, "Canopus"),
        (69673, "Arcturus"),
        (91262, "Vega"),
        (24608, "Capella"),
        (24436, "Rigel"),
        (37279, "Procyon"),
        (27989, "Betelgeuse"),
        (21421, "Aldebaran"),
        (80763, "Antares"),
        (65474, "Spica"),
    ];

    [Fact]
    public async Task Supplement1_BrightStars_AreInRenderBuffer()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        await db.EnsureTycho2DataLoadedAsync(TestContext.Current.CancellationToken);

        var buf = new Tycho2StarLite[db.Tycho2StarCount];
        var written = db.CopyTycho2Stars(buf);
        written.ShouldBeGreaterThan(2_550_000); // ~2.54M main + 17.5k supplement

        foreach (var (hip, name) in FamousBrightStars)
        {
            db.TryLookupHIP(hip, out var ra, out var dec, out var dbVmag, out _).ShouldBeTrue($"{name} (HIP {hip}) should resolve");

            // Nearest rendered star to the catalogued position.
            var bestSepArcsec = double.MaxValue;
            var bestVmag = float.NaN;
            for (var i = 0; i < written; i++)
            {
                var dra = (buf[i].RaHours - ra) * Math.Cos(dec * Math.PI / 180.0) * 15.0;
                var ddec = buf[i].DecDeg - dec;
                var sep = Math.Sqrt(dra * dra + ddec * ddec) * 3600.0;
                if (sep < bestSepArcsec) { bestSepArcsec = sep; bestVmag = buf[i].VMag; }
            }

            output.WriteLine($"{name} (HIP {hip}, V={dbVmag:F2}): nearest render entry {bestSepArcsec:F1}\" VMag={bestVmag:F2}");

            // A renderable entry must sit on the star (well within a pixel at any
            // sane FOV) with a finite magnitude (NaN would be culled by the GPU).
            bestSepArcsec.ShouldBeLessThan(30.0, $"{name} should have a Tycho-2 render entry on its position");
            float.IsNaN(bestVmag).ShouldBeFalse($"{name}'s render entry must carry a magnitude, not NaN");
            bestVmag.ShouldBeLessThan(3.5f, $"{name} is a bright star; its render mag should be bright");
        }
    }
}
