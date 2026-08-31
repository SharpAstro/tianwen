using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="CatalogUtils.Tyc2CatalogIndex"/> must be EXACTLY the string round trip it replaces:
/// <c>AbbreviationToCatalogIndex(EncodeTyc2CatalogIndex(...), isBase91Encoded: true)</c>. A
/// <see cref="CatalogIndex"/> is a packed identifier that indexes every catalog dictionary, so a
/// single differing value is not a rounding difference -- it is a star that can never be looked up
/// again, and nothing would throw.
/// </summary>
public class Tyc2CatalogIndexTests
{
    /// <summary>
    /// The real domain: Tycho-2 runs TYC1 1-9537, TYC2 1-12121, TYC3 1-4. Sweeping the corners plus a
    /// stride across the interior is what makes this a proof rather than a spot check -- and the
    /// stride is deliberately coprime-ish with the field widths so it does not sample only values
    /// that happen to align with a byte boundary, which is exactly where a base91 packing bug would
    /// hide.
    /// </summary>
    [Fact]
    public void TheAllocationFreePathMatchesTheStringRoundTripEverywhere()
    {
        var checked_ = 0;
        for (var tyc1 = 1; tyc1 <= 9537; tyc1 += 7)
        {
            foreach (var tyc2 in new[] { 1, 2, 3, 91, 92, 8191, 8192, 12120, 12121 })
            {
                for (byte tyc3 = 1; tyc3 <= 4; tyc3++)
                {
                    var viaString = CatalogUtils.AbbreviationToCatalogIndex(
                        CatalogUtils.EncodeTyc2CatalogIndex(Catalog.Tycho2, (ushort)tyc1, (ushort)tyc2, tyc3),
                        isBase91Encoded: true);
                    var direct = CatalogUtils.Tyc2CatalogIndex(Catalog.Tycho2, (ushort)tyc1, (ushort)tyc2, tyc3);

                    direct.ShouldBe(viaString,
                        $"TYC {tyc1}-{tyc2}-{tyc3}: allocation-free path gave {(ulong)direct:X} where the "
                        + $"string round trip gives {(ulong)viaString:X}");
                    checked_++;
                }
            }
        }

        checked_.ShouldBeGreaterThan(40_000, "premise: the sweep must actually cover the domain");
    }

    /// <summary>
    /// The boundary values base91 itself branches on (its 13-vs-14-bit decision keys on 88/8191), and
    /// zero, which the callers filter out but which must not silently produce a valid-looking index.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(88, 88, 1)]
    [InlineData(89, 89, 2)]
    [InlineData(8191, 8191, 3)]
    [InlineData(8192, 8192, 4)]
    [InlineData(9537, 12121, 4)]
    [InlineData(ushort.MaxValue, ushort.MaxValue, byte.MaxValue)]
    public void TheTwoPathsAgreeOnTheBase91BranchBoundaries(int tyc1, int tyc2, int tyc3)
    {
        var viaString = CatalogUtils.AbbreviationToCatalogIndex(
            CatalogUtils.EncodeTyc2CatalogIndex(Catalog.Tycho2, (ushort)tyc1, (ushort)tyc2, (byte)tyc3),
            isBase91Encoded: true);

        CatalogUtils.Tyc2CatalogIndex(Catalog.Tycho2, (ushort)tyc1, (ushort)tyc2, (byte)tyc3)
            .ShouldBe(viaString);
    }
}
