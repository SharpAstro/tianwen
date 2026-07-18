using System;
using System.IO;
using System.Linq;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <see cref="ICelestialObjectDB.TryLoadTycho2BulkFromCompressed"/> - the browser/Lightweight
/// injection seam where the ~30 MB <c>tyc2.bin.lz</c> is stripped from the bundle and fetched at
/// runtime, then fed in as raw bytes. Exercised against a FRESH, un-initialised DB so the decode +
/// wiring path runs (the shared cached DB in <see cref="CelestialObjectDBTests"/> already has the
/// bulk data from the embedded path, which would short-circuit the idempotent guard).
/// <para>
/// In <c>[Collection("Catalog")]</c> like the other full-catalog tests
/// (<see cref="Tycho2SupplementRenderTests"/>, <c>Tycho2PhotometryTests</c>): these fresh-DB
/// decodes each hold the ~42 MB Tycho-2 buffer, so they serialize with the other heavy catalog
/// tests instead of piling a concurrent large-allocation spike onto the parallel pool.
/// </para>
/// </summary>
[Collection("Catalog")]
public class Tycho2BulkInjectionTests
{
    // The raw compressed tyc2.bin.lz embedded in TianWen.Lib. Present in the test build; the
    // Lightweight/web build strips it, which is exactly why the injection seam exists.
    private static byte[] ReadEmbeddedTyc2Lz()
    {
        var asm = typeof(ICelestialObjectDB).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(".tyc2.bin.lz", StringComparison.Ordinal));
        name.ShouldNotBeNull("tyc2.bin.lz must be embedded in the (non-Lightweight) test build");

        using var stream = asm.GetManifestResourceStream(name);
        stream.ShouldNotBeNull();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void GivenCompressedBytesWhenInjectedIntoAFreshDBThenTychoStarFieldIsAvailable()
    {
        var lz = ReadEmbeddedTyc2Lz();

        // Fresh, un-initialised DB: no embedded bulk load has run, so the injection path is exercised.
        var db = new CelestialObjectDB();
        ((ICelestialObjectDB)db).Tycho2StarCount.ShouldBe(0);

        db.TryLoadTycho2BulkFromCompressed(lz).ShouldBeTrue();

        // Full catalog now visible (~2.5M).
        var count = ((ICelestialObjectDB)db).Tycho2StarCount;
        count.ShouldBeGreaterThan(2_000_000);

        // The flat records decode: the first chunk carries finite in-range positions and at
        // least one real V magnitude (the atlas plots exactly these).
        var chunk = new Tycho2StarLite[4096];
        var written = db.CopyTycho2Stars(chunk, 0);
        written.ShouldBeGreaterThan(0);

        var sample = chunk.Take(written).ToArray();
        sample.ShouldContain(s => !float.IsNaN(s.VMag));
        foreach (var s in sample)
        {
            s.RaHours.ShouldBeInRange(-0.01f, 24.01f);
            s.DecDeg.ShouldBeInRange(-90.01f, 90.01f);
        }
    }

    [Fact]
    public void GivenAlreadyLoadedDBWhenInjectedAgainThenNoOpReturnsTrue()
    {
        var lz = ReadEmbeddedTyc2Lz();
        var db = new CelestialObjectDB();

        db.TryLoadTycho2BulkFromCompressed(lz).ShouldBeTrue();
        var firstCount = ((ICelestialObjectDB)db).Tycho2StarCount;

        // Idempotent: a second inject is a no-op that still reports the catalog as available,
        // and must not change the loaded count.
        db.TryLoadTycho2BulkFromCompressed(lz).ShouldBeTrue();
        ((ICelestialObjectDB)db).Tycho2StarCount.ShouldBe(firstCount);
    }

    [Fact]
    public void GivenEmptyBytesWhenInjectedThenReturnsFalseAndNoStars()
    {
        var db = new CelestialObjectDB();
        db.TryLoadTycho2BulkFromCompressed(Array.Empty<byte>()).ShouldBeFalse();
        ((ICelestialObjectDB)db).Tycho2StarCount.ShouldBe(0);
    }
}
