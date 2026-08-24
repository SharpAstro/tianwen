using System;
using System.IO;
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
        output.WriteLine("-- names containing IDAS / LPS / UHC / CLS / L-Pro / Quad / Tri --");
        foreach (var f in FilterCurveDatabase.AllFilters)
        {
            var n = f.Name;
            if (n.Contains("IDAS", StringComparison.OrdinalIgnoreCase)
                || n.Contains("LPS", StringComparison.OrdinalIgnoreCase)
                || n.Contains("UHC", StringComparison.OrdinalIgnoreCase)
                || n.Contains("CLS", StringComparison.OrdinalIgnoreCase)
                || n.Contains("L-Pro", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Quad", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Tri", StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine($"  {n}");
            }
        }

        output.WriteLine("");
        output.WriteLine("-- does a written FILTER card resolve? --");
        foreach (var candidate in (string[])[
            "IDAS LPS-D3", "IDAS LPS D3", "LPS-D3", "LPS-D2", "IDAS-LPS-D3", "IDAS", "LPS",
            "IDAS NBZ", "RGB", "Unknown",
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
