using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The reader of ASTAP's installed star databases (#1685). The format is pinned on files built here byte by byte, the
/// description's own Sirius example among them; one test reads the database of an ASTAP installed on the machine and is
/// skipped where there is none.
/// </summary>
public sealed class AstapStarDatabaseTests : IDisposable
{
    private const double RaHoursPerUnit = 24.0 / ((1 << 24) - 1);
    private const double DecDegreesPerUnit = 90.0 / ((1 << 23) - 1);
    private const string Gaia = "GAIA DR3, density<=5000 stars/sqr(degree), Epoch=2025. Including 82 bright Tycho2 stars. Magnitude is BP";

    private readonly TempFolders _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheDescriptionsSiriusExampleDecodes()
    {
        // C3 06 48 and D7 39 with E8 as the declination's high byte: the section header carries E8 + 128 (mod 256) = 0x68.
        byte[] records = [0xFF, 0xFF, 0xFF, 0x68, 1, 0xC3, 0x06, 0x48, 0xD7, 0x39];

        var star = AstapStarDatabase.ReadFile([.. Header(Gaia), .. records]).ShouldHaveSingleItem();

        star.RA.ShouldBe(6.75247662, 1e-7);
        star.Dec.ShouldBe(-16.7161401, 1e-6);
        star.Magnitude.ShouldBe(-1.5f);
    }

    [Fact]
    public void AStarInheritsTheDeclinationAndMagnitudeOfTheLastSectionHeader()
    {
        var file = FileOf(Gaia,
            (9.4f, [(1.0, 10.25), (1.5, 10.9)]),
            (9.5f, [(2.0, -0.3)]),
            (12.1f, [(3.0, 80.0)]));

        var stars = AstapStarDatabase.ReadFile(file);

        stars.Select(static s => s.Magnitude).ShouldBe([9.4f, 9.4f, 9.5f, 12.1f]);
        stars.Select(static s => Math.Round(s.RA, 4)).ShouldBe([1.0, 1.5, 2.0, 3.0]);
        stars.Select(static s => Math.Round(s.Dec, 4)).ShouldBe([10.25, 10.9, -0.3, 80.0]);
    }

    [Fact]
    public void ARecordBeforeAnySectionHeaderIsNotAStar()
    {
        byte[] stray = [1, 2, 3, 4, 5];

        AstapStarDatabase.ReadFile([.. Header(Gaia), .. stray]).ShouldBeEmpty();
    }

    [Fact]
    public void OnlyFiveByteRecordsAreRead()
    {
        var six = Header(Gaia);
        six[AstapStarDatabase.HeaderLength - 1] = 6;
        var oldest = Header(Gaia);
        oldest[AstapStarDatabase.HeaderLength - 1] = (byte)' ';

        Should.Throw<NotSupportedException>(() => AstapStarDatabase.ReadFile(six)).Message.ShouldContain("6-byte");
        Should.Throw<NotSupportedException>(() => AstapStarDatabase.ReadFile(oldest)).Message.ShouldContain("11-byte");
        Should.Throw<InvalidDataException>(() => AstapStarDatabase.ReadFile(new byte[20]));
    }

    [Fact]
    public async Task AConeIsReadAcrossCellsRingsAndTheZeroHourLine()
    {
        var folder = _temp.Create("astapdb").FullName;
        // Ring 1 is the northern cap, one cell; ring 2 is cut at 12 h into two cells.
        Write(folder, "t50_0101.3", FileOf(Gaia, (6.0f, [(17.0, 88.5)])));
        Write(folder, "t50_0201.3", FileOf(Gaia, (8.0f, [(0.1, 45.0), (0.1, 49.5)]), (11.0f, [(0.05, 44.0), (6.0, 45.0)])));
        Write(folder, "t50_0202.3", FileOf(Gaia, (7.0f, [(23.8, 45.5)]), (14.0f, [(23.95, 46.0), (12.5, 45.0)])));
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "not a cell");

        var database = AstapStarDatabase.Discover(folder).ShouldHaveSingleItem();
        database.Name.ShouldBe("t50");
        database.Areas.ShouldBe(3);
        database.FileCount.ShouldBe(3);
        database.MaxStarsPerSquareDegree.ShouldBe(5000);
        database.Epoch.ShouldBe(2025);
        database.MagnitudeBand.ShouldBe("BP");

        var field = await database.ReadConeAsync(23.9, 45.0, 3.0, 12f, TestContext.Current.CancellationToken);

        // Both sides of 0 h, brightest first; 49.5 degrees, 6 h and 12.5 h lie outside the cone, magnitude 14 is too faint.
        field.Stars.Select(static s => (Math.Round(s.RA, 3), Math.Round(s.Dec, 3), s.Magnitude))
            .ShouldBe([(23.8, 45.5, 7.0f), (0.1, 45.0, 8.0f), (0.05, 44.0, 11.0f)]);
        field.StarsInCone.ShouldBe(4);
        field.FaintestMagnitude.ShouldBe(14.0f);
        field.DensityLimited.ShouldBeFalse();
        field.AreaSquareDegrees.ShouldBe(Math.PI * 9, 0.02);

        // A cone over the pole takes the cap whatever its right ascension, and a second read answers from the kept cell spans.
        var cap = await database.ReadConeAsync(5.0, 89.5, 2.0, 99f, TestContext.Current.CancellationToken);
        cap.Stars.ShouldHaveSingleItem().Magnitude.ShouldBe(6.0f);
        (await database.ReadConeAsync(12.4, 45.0, 2.0, 99f, TestContext.Current.CancellationToken)).Stars.ShouldHaveSingleItem().RA.ShouldBe(12.5, 1e-4);
    }

    [Fact]
    public async Task AConeAtTheDensityCapSaysTheDatabaseWasCutThere()
    {
        var folder = _temp.Create("astapdb").FullName;
        const string sparse = "GAIA DR3, density<=2 stars/sqr(degree), Epoch=2025. Magnitude is BP";
        Write(folder, "t00_0101.1", FileOf(sparse, (9.0f, [(1.0, 10.0), (1.001, 10.01), (1.002, 10.02), (1.003, 10.03), (1.004, 10.04), (1.005, 10.05)])));

        var database = AstapStarDatabase.Discover(folder).ShouldHaveSingleItem();
        var field = await database.ReadConeAsync(1.0, 10.0, 1.0, 99f, TestContext.Current.CancellationToken);

        // Six stars in pi square degrees against a cap of two a square degree.
        field.StarsInCone.ShouldBe(6);
        field.DensityLimited.ShouldBeTrue();
    }

    [Fact]
    public void TheDensestDatabaseOfAFolderComesFirstAndAMissingFolderHoldsNone()
    {
        var folder = _temp.Create("astapdb").FullName;
        Write(folder, "g05_0101.2", FileOf("GAIA DR3, density<=500 stars/sqr(degree), Epoch=2025. Magnitude is BP", (9.0f, [(1.0, 10.0)])));
        Write(folder, "d50_0101.4", FileOf(Gaia, (9.0f, [(1.0, 10.0)])));

        AstapStarDatabase.Discover(folder).Select(static d => d.Name).ShouldBe(["d50", "g05"]);
        AstapStarDatabase.Discover(Path.Combine(folder, "absent")).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheInstalledDatabaseHoldsSiriusWhereAstapIsInstalled()
    {
        if (AstapStarDatabase.FindInstalled() is not { } database)
        {
            Assert.Skip("No ASTAP star database is installed on this machine.");
            return;
        }

        // Sirius at J2000; the database's epoch is later and Sirius moves 1.3 arcsec a year, so the cone is a tenth of a degree.
        var field = await database.ReadConeAsync(6.7524770, -16.716116, 0.1, 2f, TestContext.Current.CancellationToken);

        field.Stars.ShouldNotBeEmpty();
        field.Stars[0].Magnitude.ShouldBeLessThan(0f);
        database.MagnitudeBand.ShouldBe("BP");
    }

    private static byte[] Header(string description)
    {
        var header = new byte[AstapStarDatabase.HeaderLength];
        header.AsSpan().Fill((byte)' ');
        Encoding.Latin1.GetBytes(description, header);
        header[^1] = 5;
        return header;
    }

    /// <summary>A file of sections, each a magnitude and its stars (hours, degrees); a new section header is written whenever
    /// the declination's high byte changes, as the format has it.</summary>
    private static byte[] FileOf(string description, params (float Magnitude, (double RaHours, double DecDegrees)[] Stars)[] sections)
    {
        var bytes = new List<byte>(Header(description));
        foreach (var (magnitude, stars) in sections)
        {
            int? high = null;
            foreach (var (ra, dec) in stars)
            {
                var raRaw = (int)Math.Round(ra / RaHoursPerUnit);
                var decRaw = (int)Math.Round(dec / DecDegreesPerUnit);
                if (high != decRaw >> 16)
                {
                    high = decRaw >> 16;
                    bytes.AddRange([0xFF, 0xFF, 0xFF, (byte)(high.Value + 128), (byte)Math.Round((magnitude * 10) + 16)]);
                }
                bytes.AddRange([(byte)raRaw, (byte)(raRaw >> 8), (byte)(raRaw >> 16), (byte)decRaw, (byte)(decRaw >> 8)]);
            }
        }
        return [.. bytes];
    }

    private static void Write(string folder, string name, byte[] file) => File.WriteAllBytes(Path.Combine(folder, name), file);
}
