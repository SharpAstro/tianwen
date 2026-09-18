using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="OpenNgcCorrections"/> is the gap between a fix sent upstream to OpenNGC and the refresh that
/// brings it in. Two things keep it honest: every line must still be NEEDED against the raw embedded row,
/// and the catalogue must actually carry the corrected names.
/// </summary>
public sealed class OpenNgcCorrectionsTests
{
    /// <summary>
    /// The raw OpenNGC rows exactly as embedded (NGC and the addendum), by name, before any correction:
    /// the 14th and 15th fields are the common names and the identifiers, unit-separated. Read straight
    /// from the resource so a correction cannot hide the very row it corrects.
    /// </summary>
    private static Dictionary<string, (string[] CommonNames, string[] Identifiers)> RawRows()
    {
        var rows = new Dictionary<string, (string[], string[])>(StringComparer.Ordinal);
        var assembly = typeof(CelestialObjectDB).Assembly;
        foreach (var suffix in new[] { ".NGC.gs.gz", ".NGC.addendum.gs.gz" })
        {
            var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(resource).ShouldNotBeNull();
            using var gz = new GZipStream(stream, CompressionMode.Decompress);
            using var payload = new MemoryStream();
            gz.CopyTo(payload);

            foreach (var recMem in AsciiRecordReader.EnumerateRecords(new ReadOnlyMemory<byte>(payload.GetBuffer(), 0, (int)payload.Length)))
            {
                var rec = recMem.Span;
                if (rec.IsEmpty)
                {
                    continue;
                }
                var name = AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref rec));
                for (var i = 0; i < 12; i++)
                {
                    AsciiRecordReader.TakeField(ref rec);
                }
                var commons = AsciiRecordReader.ReadStringArray(AsciiRecordReader.TakeField(ref rec));
                var idents = AsciiRecordReader.ReadStringArray(AsciiRecordReader.TakeField(ref rec));
                rows[name] = (commons, idents);
            }
        }
        return rows;
    }

    public static TheoryData<string> CorrectedRows()
    {
        var data = new TheoryData<string>();
        foreach (var correction in OpenNgcCorrections.All)
        {
            data.Add(correction.Row);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CorrectedRows))]
    public void EveryCorrectionIsStillNeededAgainstTheRawUpstreamRow(string row)
    {
        // A value to remove must still be on the raw row and a value to add must still be absent from it.
        // When this fails after an OpenNGC refresh, upstream has shipped the fix: delete the line.
        var correction = OpenNgcCorrections.All.Single(c => c.Row == row);
        var raw = RawRows();
        raw.ShouldContainKey(row, "the corrected row must exist in the embedded OpenNGC table");
        var (commons, idents) = raw[row];

        foreach (var value in correction.RemoveCommonNames)
        {
            commons.ShouldContain(value, $"{row}: '{value}' is no longer on the upstream row; delete this correction");
        }
        foreach (var value in correction.RemoveIdentifiers)
        {
            idents.ShouldContain(value, $"{row}: '{value}' is no longer on the upstream row; delete this correction");
        }
        foreach (var value in correction.AddCommonNames)
        {
            commons.ShouldNotContain(value, $"{row}: upstream now carries '{value}'; delete this correction");
        }
        foreach (var value in correction.AddIdentifiers)
        {
            idents.ShouldNotContain(value, $"{row}: upstream now carries '{value}'; delete this correction");
        }

        correction.Evidence.ShouldContain("simbad.cds.unistra.fr", Case.Sensitive, "every correction cites the SIMBAD object it agrees with");
    }

    [Fact]
    public async Task TheCatalogueCarriesTheFlameNebulaOnNgc2024NotIc434()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);

        db.TryLookupByIndex("IC434", out var ic434).ShouldBeTrue();
        ic434.CommonNames.ShouldNotContain("Flame Nebula", "the red label over the Horsehead, 2026-09-18");
        ic434.CommonNames.ShouldNotContain("Orion B");

        db.TryLookupByIndex("NGC2024", out var ngc2024).ShouldBeTrue();
        ngc2024.CommonNames.ShouldContain("Flame Nebula");

        db.TryResolveCommonName("Flame Nebula", out var matches).ShouldBeTrue();
        matches.ShouldContain(ngc2024.Index);
        matches.ShouldNotContain(ic434.Index);
    }

    [Fact]
    public async Task TheCatalogueCarriesTheCocoonGalaxyOnNgc4490NotNgc4990()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);

        db.TryLookupByIndex("NGC4990", out var wrong).ShouldBeTrue();
        wrong.CommonNames.ShouldNotContain("Cocoon Galaxy");
        db.TryLookupByIndex("NGC4490", out var right).ShouldBeTrue();
        right.CommonNames.ShouldContain("Cocoon Galaxy");

        db.TryResolveCommonName("Cocoon Galaxy", out var matches).ShouldBeTrue();
        matches.ShouldContain(right.Index);
        matches.ShouldNotContain(wrong.Index);
    }

    [Fact]
    public void ApplyReplacesTheArraysAndKeepsTheNullConvention()
    {
        string[]? commons = ["Flame Nebula", "Orion B"];
        string[]? idents = ["LBN 953"];
        OpenNgcCorrections.Apply("IC0434", ref commons, ref idents);
        commons.ShouldBeNull("every common name was removed, and none is null, as the merge takes it");
        idents.ShouldBeNull();

        string[]? none = null;
        string[]? noIdents = null;
        OpenNgcCorrections.Apply("NGC2024", ref none, ref noIdents);
        none.ShouldNotBeNull().ShouldBe(["Flame Nebula"]);
        noIdents.ShouldNotBeNull().ShouldBe(["LBN 953"]);

        string[]? untouched = ["Whirlpool Galaxy"];
        string[]? untouchedIdents = null;
        OpenNgcCorrections.Apply("NGC5194", ref untouched, ref untouchedIdents);
        untouched.ShouldBe(["Whirlpool Galaxy"]);
        untouchedIdents.ShouldBeNull();
    }
}
