using Shouldly;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Catalog")]
public class Tycho2PhotometryTests
{
    /// <summary>
    /// Regression for the blank-VT bake bug: a Tycho-2 star whose source VT magnitude is
    /// blank must decode as a MISSING magnitude (NaN), never a bogus bright value.
    /// <para>
    /// TYC 9372-1058-1 (a faint star in Mensa) carries only BT in Tycho-2; its VT field is
    /// blank. <c>Get-Tycho2Catalogs.ps1</c> used <c>[void][float]::TryParse(...)</c>, and
    /// since <c>float.TryParse</c> writes 0 to its out-param on failure, the blank VT was
    /// baked as decimag 20 (= mag 0.0) instead of the 0xFF missing-sentinel. Combined with a
    /// present BT (~12.8) that produced V = 0 - 0.090*(12.8) = -1.15 and B-V = 0.850*12.8 =
    /// 10.88 -- a spuriously bright star with an impossible colour. The fix reads the TryParse
    /// bool and resets to NaN, so the bake now emits 0xFF and the decode returns NaN.
    /// </para>
    /// </summary>
    [Fact]
    public async Task BlankVtMag_DecodesAsMissing_NotBogusBright()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);

        db.TryLookupByIndex("TYC 9372-1058-1", out var star).ShouldBeTrue();
        star.ObjectType.ShouldBe(ObjectType.Star);

        // Position sanity — catalogued ICRS coords (~04h12m / -80deg11').
        star.RA.ShouldBe(4.211, 0.01);
        star.Dec.ShouldBe(-80.189, 0.01);

        // The fix: blank VT -> magnitude missing (NaN), never the spurious -1.15.
        float.IsNaN((float)star.V_Mag).ShouldBeTrue();

        // And B-V must be physically plausible, not the impossible 10.88 seen pre-fix.
        ((float)star.BMinusV).ShouldBeLessThan(5f);
    }
}
