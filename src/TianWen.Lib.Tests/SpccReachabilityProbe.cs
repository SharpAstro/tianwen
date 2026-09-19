using System;
using System.Collections.Frozen;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Why SPCC declines on a specific file on THIS machine. A probe, not an assertion: it reads a real
/// master from the user's archive and reports which of SPCC's preconditions fails, so the answer comes
/// from the file rather than from reasoning about the file.
///
/// <para>Env-gated on <c>TIANWEN_SPCC_PROBE</c> (a path to a FITS master) so a bare <c>dotnet test</c>
/// skips it -- the file lives outside the repo and is gigabytes of someone's archive.</para>
/// </summary>
public class SpccReachabilityProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task ReportWhySpccWouldDeclineOnARealMaster()
    {
        var path = Environment.GetEnvironmentVariable("TIANWEN_SPCC_PROBE");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(path) || !File.Exists(path),
            "set TIANWEN_SPCC_PROBE to a FITS master to run this probe");

        var ct = TestContext.Current.CancellationToken;
        Image.TryReadFitsFile(path!, out var image).ShouldBeTrue("could not read the FITS");
        var meta = image!.ImageMeta;

        output.WriteLine($"file        : {Path.GetFileName(path)}");
        output.WriteLine($"channels    : {image.ChannelCount}   sensorType={meta.SensorType}");
        output.WriteLine($"Instrument  : '{meta.Instrument}'");
        output.WriteLine($"SensorModel : '{meta.SensorModel}'");
        output.WriteLine($"Filter      : '{meta.Filter.FilterNameForFits}'");
        output.WriteLine($"Telescope   : '{meta.Telescope}'");

        // The two gates SPCC passes through before it ever looks at a star. Loading explicitly, so a
        // NULL throughput below means the METADATA did not resolve rather than "the DB was cold" --
        // those are different bugs and the first probe run could not tell them apart.
        output.WriteLine($"FilterCurveDatabase.IsLoaded (before load): {FilterCurveDatabase.IsLoaded}");
        await FilterCurveDatabase.LoadAsync(ct);
        output.WriteLine($"FilterCurveDatabase.IsLoaded (after load) : {FilterCurveDatabase.IsLoaded}");
        var throughput = await Task.Run(() => FilterCurveDatabase.BuildChannelThroughputs(meta), ct);
        output.WriteLine(throughput is null
            ? "BuildChannelThroughputs -> NULL  (this alone makes SPCC return 'No throughput for ...')"
            : "BuildChannelThroughputs -> OK");

        if (FilterCurveDatabase.TryComputeSensorLumaWeights(meta, out var w))
        {
            output.WriteLine($"sensor luma weights resolved: {w.R:F4}/{w.G:F4}/{w.B:F4} (the sensor WAS matched)");
        }
        else
        {
            output.WriteLine("sensor luma weights NOT resolved (the sensor name did not match)");
        }
    }

    /// <summary>
    /// The filter families worth listing. A SET of tokens compared against the curve's OWN tokens,
    /// not a chain of substring tests: <c>Contains("HA")</c> matches "en<b>HA</b>nce" and filed
    /// <c>OPTOLONG_L_ENHANCE</c> under Ha, which is the same class of false positive the matcher's
    /// own token rules exist to avoid. Tokenising through
    /// <see cref="FilterCurveDatabase.TokenizeFromUnderscores"/> means this groups the curves the way
    /// the matcher reads them rather than a second way.
    /// </summary>
    private static readonly FrozenSet<string> FamilyTokens = new[]
    {
        "IDAS", "LPS", "UHC", "CLS", "PRO", "QUAD", "TRI", "TRIBAND", "ENHANCE",
        "HA", "ALPHA", "OIII", "SII", "LUM", "LUMINANCE",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What light-pollution / broadband filters the embedded curve database actually knows, and
    /// whether a given name resolves. Not env-gated: it reads only embedded resources.
    /// </summary>
    [Fact]
    public async Task ReportKnownLightPollutionFilters()
    {
        var ct = TestContext.Current.CancellationToken;
        await FilterCurveDatabase.LoadAsync(ct);

        output.WriteLine($"{FilterCurveDatabase.AllFilters.Length} filter curves embedded.");
        output.WriteLine("");
        output.WriteLine($"-- curves carrying any of: {string.Join(", ", FamilyTokens.Order())} --");
        foreach (var f in FilterCurveDatabase.AllFilters)
        {
            if (FilterCurveDatabase.TokenizeFromUnderscores(f.Name).Any(FamilyTokens.Contains))
            {
                output.WriteLine($"  {f.Name}");
            }
        }

        output.WriteLine("");
        output.WriteLine("-- does a written FILTER card resolve? --");
        foreach (var candidate in (string[])[
            "IDAS LPS-D3", "IDAS LPS D3", "LPS-D3", "LPS-D2", "IDAS-LPS-D3", "IDAS", "LPS",
            "IDAS NBZ", "RGB", "Unknown",
            // The slugs D:/Astro-Organized actually uses as its filter directory names, verbatim
            // and hyphenated. These are the strings a bake reads off the tree, so they are the ones
            // that decide whether a session gets a curve; the prose spellings below are what a FITS
            // card might say instead. A slug that stops resolving is a silent SPCC skip for every
            // session under it, which is how "LPS" went unnoticed.
            "Optolong-L-Ultimate-3nm", "Optolong-L-Quad-Enhance", "IDAS-LPS-D3",
            "Optolong-L-eNhance",
            // A PROVISIONAL slug for a measured-but-unnamed population (ASI585 + ZS61, 2024-10 and
            // 2025-01). It must resolve to NO MATCH: inventing a curve for a filter nobody can name
            // would be worse than having none, because SPCC would then use it as if it described the
            // glass. Listed here so that stays true if the matcher's token rules ever loosen.
            "Unidentified-Broadband",
            // A SECOND measured-but-unnamed population on the same camera (ASI585 + ZS61,
            // 2025-05-25). It is distinct from the one above and must also resolve to NO MATCH.
            // Measured: bias-corrected flat R/G 0.607 B/G 0.497 against Unidentified-Broadband's
            // 0.737/0.801 and L-eNhance's 0.166/0.862, with sky brightness of 151 and 128 ADU/s
            // against 190 broadband and 19 narrowband. So it is broadband-throughput with the blue
            // suppressed, consistent with a light-pollution filter and NOT proven to be one: the
            // ratios that could name it are IMX533-derived and do not transfer to this IMX585.
            "Unidentified-Broadband-BlueCut",
            // A NARROWBAND session nobody can name (PlayerOne Uranus-C, 2023-08-09, "M8 M20 OIII HA"):
            // sky 3 ADU/s at gain 220 against 75 to 230 on the broadband nights of that body, so a
            // dual-band by measurement, and the owner's 2023 dual-band was the L-eNhance, but no
            // IMX585 reference band names it. Must resolve to NO MATCH for the same reason as above.
            "Unidentified-HaOIII",
            // A UV/IR cut on the ZWO ASI294MC (2024-02-03, 135 mm, Eta Carinae), by the owner's
            // recollection. Measured broadband: flat B/G 0.81 against 0.53 for the IDAS LPS-D3 on the
            // same IMX294 sensor. A generic cut must not resolve to a SPECIFIC brand's curve.
            "UV-IR-Cut", "UV/IR Cut", "UV IR Cut", "IR-Cut", "IR Cut", "Baader UV/IR Cut", "ZWO IR Cut",
            "Optolong UV/IR Cut", "Svbony UV/IR Cut",
            // Mono channel names, for the two ASI1600MM sessions. A mono session has one filter and
            // no CFA, so the pixel method cannot help and the path tag is the only evidence.
            "Ha", "H-Alpha", "Luminance", "LUM", "Baader Ha", "Astrodon Ha",
            // Filters we do NOT carry, listed so the report says what a card naming one would
            // resolve to instead. A confident WRONG match is worse than no match: the curve is
            // then used as if it described the glass in front of the sensor.
            "Optolong L-Quad Enhance", "L-Quad Enhance", "Optolong L-eNhance", "L-eNhance",
            "Optolong L-eXtreme", "Optolong L-Ultimate", "Optolong L-Pro",
            "Askar Colour Magic D1", "Askar D1", "Askar D2", "Colour Magic D2", "D1", "D2"])
        {
            var ok = FilterCurveDatabase.TryMatchFilter(candidate, out var match);
            output.WriteLine($"  '{candidate}' -> {(ok ? match.Name : "NO MATCH")}");
        }
    }
}
