using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The per-object Horizons element fetch, which is what makes a comet appear where it actually is.
///
/// <para>Pinned against a FROZEN REAL RESPONSE (<c>Data/horizons-10p-elements-2026-08-06.txt</c>,
/// fetched from the live API) rather than a hand-written approximation, because every failure mode
/// here is a parsing one: a units flag that stopped being sent, a key that matched inside a longer
/// key, a record shape that shifted. A fixture written from memory would agree with a parser written
/// from the same memory.</para>
/// </summary>
public class HorizonsCometSourceTests
{
    private const double Jd2026Aug06 = 2461258.5;

    /// <summary>10P as the bulk SBDB feed serves it: the 2016-epoch record that lands 9.3 degrees out.</summary>
    private static CometElements BulkTenP()
    {
        CometDesignation.TryParse("10P", out var designation).ShouldBeTrue();
        return new CometElements(designation, "Tempel",
            PerihelionDistanceAu: 1.417400467387836,
            Eccentricity: 0.5373811030007175,
            InclinationDeg: 12.0291718465731,
            AscendingNodeDeg: 117.8001776932984,
            ArgumentOfPerihelionDeg: 195.5324954666831,
            PerihelionJdTt: 2457340.7411673036,
            EpochJdTt: 2457650.5,
            AbsoluteMagnitudeM1: 13.7,
            SlopeK1: 6.5);
    }

    private static string FrozenResponse()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("horizons-10p-elements-2026-08-06.txt", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name).ShouldNotBeNull();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void TheRealResponseParsesToTheCurrentApparitionElements()
    {
        HorizonsCometSource.TryParse(FrozenResponse(), BulkTenP(), out var refined).ShouldBeTrue();

        // The six numbers CometEphemeris consumes, plus the epoch they are stated at.
        refined.Eccentricity.ShouldBe(0.5374514419364470, tolerance: 1e-12);
        refined.PerihelionDistanceAu.ShouldBe(1.417737835301425, tolerance: 1e-12);
        refined.InclinationDeg.ShouldBe(12.02722470731694, tolerance: 1e-10);
        refined.AscendingNodeDeg.ShouldBe(117.7974885719829, tolerance: 1e-10);
        refined.ArgumentOfPerihelionDeg.ShouldBe(195.4683316459254, tolerance: 1e-10);
        refined.PerihelionJdTt.ShouldBe(2461254.615445367061, tolerance: 1e-6);
        refined.EpochJdTt.ShouldBe(Jd2026Aug06, tolerance: 1e-9);
    }

    [Fact]
    public void ThePerihelionMovesFromTheOldApparitionToThisOne()
    {
        var bulk = BulkTenP();
        HorizonsCometSource.TryParse(FrozenResponse(), bulk, out var refined).ShouldBeTrue();

        // This single number IS the 9.3 degrees. The bulk record's perihelion is the 2015 passage, and
        // propagating it two revolutions lands 3.76 days late; the refined set states the 2026 passage
        // directly, so there is nothing left to accumulate.
        bulk.PerihelionJdTt.ShouldBe(2457340.741, tolerance: 0.001);
        refined.PerihelionJdTt.ShouldBe(2461254.615, tolerance: 0.001);

        // And it is no longer stale, so the marker drops its "?" as well as moving.
        bulk.IsElementSetStale(Jd2026Aug06).ShouldBeTrue();
        refined.IsElementSetStale(Jd2026Aug06).ShouldBeFalse();
    }

    [Fact]
    public void IdentityAndPhotometryAreCarriedOverUntouched()
    {
        // Horizons is asked only for the orbit. The designation and common name are ours, and the
        // magnitude model deliberately stays SBDB's because it is the same model Horizons uses.
        var bulk = BulkTenP();
        HorizonsCometSource.TryParse(FrozenResponse(), bulk, out var refined).ShouldBeTrue();

        refined.Designation.ShouldBe(bulk.Designation);
        refined.CommonName.ShouldBe(bulk.CommonName);
        refined.AbsoluteMagnitudeM1.ShouldBe(bulk.AbsoluteMagnitudeM1);
        refined.SlopeK1.ShouldBe(bulk.SlopeK1);
        refined.DisplayName.ShouldBe("10P/Tempel");
    }

    [Theory]
    [InlineData("")]
    [InlineData("API ERROR: HTTP code 400")]                       // the API's own failure shape
    [InlineData("$$SOE\n$$EOE")]                                   // empty record block
    [InlineData("$$SOE\n2461258.5 = A.D. 2026-Aug-06\n EC= 0.5\n$$EOE")] // missing fields
    public void AnUnusableResponseIsRejectedRatherThanHalfRead(string response)
    {
        // The caller treats false as "keep the bulk elements", so a partial parse must never succeed:
        // half a refined orbit is worse than an old but self-consistent one.
        HorizonsCometSource.TryParse(response, BulkTenP(), out var elements).ShouldBeFalse();
        elements.ShouldBe(BulkTenP());
    }

    [Fact]
    public void KilometreUnitsAreRejected()
    {
        // OUT_UNITS=AU-D is what makes QR come back in AU; without it Horizons answers in km, which
        // parses as a perfectly good double and would put the comet 150 million times too far away.
        // A response that somehow arrives in km must fail the parse, not sail through.
        var km = FrozenResponse().Replace("QR= 1.417737835301425E+00", "QR= 2.120905613719205E+08", StringComparison.Ordinal);

        HorizonsCometSource.TryParse(km, BulkTenP(), out _).ShouldBeFalse();
    }

    [Fact]
    public void TheQueryAsksForEverythingTheParserAssumes()
    {
        // Each of these is silently catastrophic if dropped: CAP picks the apparition in progress,
        // AU-D fixes the units the sanity gate above guards, and 500@10 makes the elements heliocentric
        // (the frame CometEphemeris propagates in).
        var url = HorizonsCometSource.BuildQuery(new Uri("https://example.test/api"), "10P", new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero));

        var decoded = Uri.UnescapeDataString(url);
        decoded.ShouldContain("'DES=10P;CAP;'");
        decoded.ShouldContain("EPHEM_TYPE=ELEMENTS");
        decoded.ShouldContain("'AU-D'");
        decoded.ShouldContain("'500@10'");
        decoded.ShouldContain("'2026-08-06'");
        decoded.ShouldContain("'2026-08-07'");
    }
}
