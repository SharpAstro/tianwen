using System;
using System.Linq;
using System.Text.Json;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the pure SBDB column/row -> <see cref="CometElements"/> mapping: field-name lookup, string-cell
/// number parsing, null / missing handling, and the skip-and-count of unmappable rows.
/// </summary>
[Collection("Catalog")]
public class SbdbCometParseTests
{
    private static readonly string[] Fields =
        ["prefix", "pdes", "name", "M1", "K1", "e", "q", "i", "om", "w", "tp", "epoch"];

    [Fact]
    public void GivenAMixedSbdbResponseWhenParsedThenValidRowsMapAndBadRowsAreSkipped()
    {
        var response = new SbdbQueryResponse
        {
            Fields = Fields,
            Data =
            [
                // Numbered comet: pdes is the full designation, prefix redundant. Full photometry.
                ["P", "12P", "Pons-Brooks", "5.0", "15.0", "0.9545612", "0.7808611", "74.19", "255.85", "198.98", "2460421.63", "2460211.5"],
                // Provisional: SBDB stores pdes WITHOUT the prefix; rejoined to "C/2023 A3". No name / no M1/K1.
                ["C", "2023 A3", null, null, null, "1.0000953", "0.39143", "139.11", "21.55", "308.49", "2460581.24", "2460448.5"],
                // Unparseable designation -> skipped.
                ["C", "not-a-comet", "Nope", "5", "10", "0.9", "1.0", "10", "20", "30", "2460000.0", "2460000.5"],
                // Missing core element (empty e) -> skipped.
                ["P", "24P", "Schaumasse", "10", "8", "", "1.2", "12", "80", "58", "2460000.0", "2460000.5"],
            ],
            Count = 4,
        };

        var comets = SbdbCometSource.Parse(response, logger: null);

        comets.Count.ShouldBe(2);

        var ponsBrooks = comets[0];
        ponsBrooks.Designation.ToCanonical().ShouldBe("12P");
        ponsBrooks.CommonName.ShouldBe("Pons-Brooks");
        ponsBrooks.PerihelionDistanceAu.ShouldBe(0.7808611, 1e-7);
        ponsBrooks.AbsoluteMagnitudeM1.ShouldBe(5.0);
        ponsBrooks.SlopeK1.ShouldBe(15.0);
        ponsBrooks.HasMagnitudeModel.ShouldBeTrue();
        ponsBrooks.CatalogIndex.ShouldNotBeNull();

        var atlas = comets[1];
        atlas.Designation.ToCanonical().ShouldBe("C/2023 A3");
        atlas.CommonName.ShouldBeNull();
        atlas.HasMagnitudeModel.ShouldBeFalse();
        double.IsNaN(atlas.AbsoluteMagnitudeM1).ShouldBeTrue();
    }

    [Fact]
    public void GivenColumnsInADifferentOrderWhenParsedThenTheyAreMappedByName()
    {
        // Deliberately reorder so positional parsing would produce garbage.
        var response = new SbdbQueryResponse
        {
            Fields = ["q", "e", "pdes", "tp", "w", "om", "i", "name", "K1", "M1", "epoch"],
            Data = [["0.585", "0.999", "C/1995 O1", "2450539.0", "130.6", "282.5", "89.4", "Hale-Bopp", "10.0", "-1.0", "2450520.5"]],
            Count = 1,
        };

        var comet = SbdbCometSource.Parse(response, logger: null).Single();

        comet.Designation.ToCanonical().ShouldBe("C/1995 O1");
        comet.CommonName.ShouldBe("Hale-Bopp");
        comet.PerihelionDistanceAu.ShouldBe(0.585, 1e-9);
        comet.Eccentricity.ShouldBe(0.999, 1e-9);
        comet.AbsoluteMagnitudeM1.ShouldBe(-1.0);
    }

    [Fact]
    public void GivenNoFieldsOrDataWhenParsedThenEmpty()
    {
        SbdbCometSource.Parse(new SbdbQueryResponse(), logger: null).ShouldBeEmpty();
        SbdbCometSource.Parse(null, logger: null).ShouldBeEmpty();
    }

    [Fact]
    public void GivenRealShapedSbdbJsonWhenDeserializedWithSourceGenThenItParses()
    {
        // Mirrors the live wire shape: a "signature" object we ignore, string-or-null cells, jagged data.
        // Exercises the AOT-critical source-generated deserialization of string?[][].
        const string json = """
        {
          "signature": { "version": "1.0", "source": "NASA/JPL SBDB" },
          "fields": ["pdes","name","M1","K1","e","q","i","om","w","tp","epoch"],
          "data": [
            ["12P","Pons-Brooks","5.0","15.0","0.9545612","0.7808611","74.19","255.85","198.98","2460421.63","2460211.5"],
            ["C/2023 A3",null,null,null,"1.0000953","0.39143","139.11","21.55","308.49","2460581.24","2460448.5"]
          ],
          "count": 2
        }
        """;

        var response = JsonSerializer.Deserialize(json, SbdbJsonContext.Default.SbdbQueryResponse);
        response.ShouldNotBeNull();
        response.Count.ShouldBe(2);

        var comets = SbdbCometSource.Parse(response, logger: null);
        comets.Count.ShouldBe(2);
        comets[0].Designation.ToCanonical().ShouldBe("12P");
        comets[1].CommonName.ShouldBeNull();
    }

    [Fact]
    public void GivenCometElementsWhenRoundTrippedThroughTheCacheContextThenDesignationAndNaNSurvive()
    {
        CometDesignation.TryParse("C/2023 A3", out var designation).ShouldBeTrue();
        var original = new CometCacheFile(
            new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
            [new CometElements(designation, null, 0.39143, 1.0000953, 139.11, 21.55, 308.49, 2460581.24, 2460448.5, double.NaN, double.NaN)]);

        var json = JsonSerializer.Serialize(original, SbdbJsonContext.Default.CometCacheFile);

        // Designation is stored as its readable canonical string, NaN photometry as "NaN".
        json.ShouldContain("C/2023 A3");
        json.ShouldContain("NaN");

        var back = JsonSerializer.Deserialize(json, SbdbJsonContext.Default.CometCacheFile);
        back.ShouldNotBeNull();
        back.FetchedUtc.ShouldBe(original.FetchedUtc);
        back.Comets.Length.ShouldBe(1);
        back.Comets[0].Designation.ToCanonical().ShouldBe("C/2023 A3");
        double.IsNaN(back.Comets[0].AbsoluteMagnitudeM1).ShouldBeTrue();
        back.Comets[0].HasMagnitudeModel.ShouldBeFalse();
    }
}
