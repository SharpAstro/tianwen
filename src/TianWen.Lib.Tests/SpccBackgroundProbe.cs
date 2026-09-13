using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Diagnostic probe for the report that SPCC can leave a NON-GREY background. Not an assertion: it
/// runs the viewer's own calibration path on a real master and prints what the render actually does
/// to the sky, with the calibration on and off and under each stretch mode, so the question is
/// answered by numbers rather than by reading the pipeline.
/// <para>
/// Point <c>TIANWEN_SPCC_BG_PROBE</c> at a plate-solved colour master (the env var both gates and
/// supplies the path, so no absolute path is committed).
/// </para>
/// </summary>
[Collection("Imaging")]
public class SpccBackgroundProbe(ITestOutputHelper output)
{
    private const string EnvVar = "TIANWEN_SPCC_BG_PROBE";

    private static ICelestialObjectDB? _cachedDb;
    private static readonly SemaphoreSlim _dbSem = new(1, 1);

    [Fact]
    public async Task WhatSpccDoesToTheSky()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 }, $"{EnvVar} not set");
        var path = Environment.GetEnvironmentVariable(EnvVar)!;
        Assert.SkipUnless(File.Exists(path), $"{path} not found");

        var ct = TestContext.Current.CancellationToken;

        var doc = await AstroImageDocument.OpenAsync(path, cancellationToken: ct);
        Assert.NotNull(doc);

        var img = doc.UnstretchedImage;
        var (channels, w, h) = img.Shape;
        var meta = img.ImageMeta;
        output.WriteLine($"file       {Path.GetFileName(path)}");
        output.WriteLine($"shape      {w}x{h} ch={channels} maxValue={img.MaxValue:G6} minValue={img.MinValue:G6}");
        // Image.Pedestal is the DECLARED zero point (PEDESTAL, else APP's AD-PED); the stretch's own
        // pedestal comes from MinValue instead, so these two are expected to disagree here.
        output.WriteLine($"pedestal   declared={img.Pedestal:G6}");
        output.WriteLine($"meta       instrument={meta.Instrument} sensor={meta.SensorModel} filter={meta.Filter.FilterNameForFits} sensorType={meta.SensorType}");
        output.WriteLine($"wcs        {(doc.Wcs is { HasCDMatrix: true } ? "solved (CD matrix present)" : doc.Wcs is null ? "NONE" : "present but no CD matrix")}");

        // What is ALREADY known at open, before star detection: the stretch needs these to render the
        // first frame, so any decision taken from them is free.
        output.WriteLine($"at open    PerChannelStats={(doc.PerChannelStats is { Length: > 0 } ? "populated" : "EMPTY")}");
        for (var ci = 0; ci < doc.PerChannelStats.Length; ci++)
        {
            output.WriteLine($"  open[{ci}]  median={doc.PerChannelStats[ci].Median:G6} mad={doc.PerChannelStats[ci].Mad:G5}");
        }

        // And what a fresh background scan would cost, if one were wanted anyway.
        var scanBuf = new float[channels];
        for (var ci = 0; ci < channels; ci++) { scanBuf[ci] = doc.PerChannelStats[ci].Pedestal; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        img.ScanBackgroundRegion(scanBuf);
        sw.Stop();
        output.WriteLine($"scan cost  ScanBackgroundRegion {sw.Elapsed.TotalMilliseconds:F1} ms over {w}x{h}x{channels}");

        await doc.DetectStarsAsync(ct);
        output.WriteLine($"stars      {doc.Stars?.Count ?? 0}");

        // The star-masked background is the one background neutralisation actually consumes; taking it
        // before detection reads a different (and here, degenerate) array.
        output.WriteLine($"bg         {(doc.PerChannelBackground is { Length: > 0 } b ? Fmt(b) : "(none)")}");
        for (var ci = 0; ci < doc.PerChannelStats.Length; ci++)
        {
            var s = doc.PerChannelStats[ci];
            output.WriteLine($"stats[{ci}]   pedestal={s.Pedestal:G5} median={s.Median:G5} mad={s.Mad:G5}");
        }

        var db = await InitDbAsync(ct);
        var (matched, diag) = await doc.ComputeSpccColorCalibrationAsync(db, ct);
        output.WriteLine($"SPCC       matched={matched} diag={diag ?? "(none)"}");
        output.WriteLine($"           calibration={(doc.ColorCalibration is { } c ? $"({c.R:F4}, {c.G:F4}, {c.B:F4})" : "NULL")}");
        output.WriteLine($"           narrowband={doc.IsNarrowbandColorCalibration} summary={(doc.ColorCalibrationSummary is null ? "null" : "present")}");

        // Post-WB medians: what the shared Linked curve has to straddle.
        if (doc.ColorCalibration is { } wb)
        {
            var st = doc.PerChannelStats;
            output.WriteLine($"post-WB    medians ({st[0].Median * wb.R:G5}, {st[1].Median * wb.G:G5}, {st[2].Median * wb.B:G5})");
        }

        output.WriteLine($"bgNeut     before={(doc.BackgroundNeutralization is { } b0 ? $"({b0.R:F4}, {b0.G:F4}, {b0.B:F4})" : "NULL")}");

        var rgba = new byte[w * h * 4];
        output.WriteLine("");
        output.WriteLine("rendered sky, per channel (median of the 8-bit render; spread = max/min - 1)");
        output.WriteLine("mode        spcc    R    G    B   spread");
        Render(doc, img, rgba);

        // THE EXPERIMENT: solve background neutralisation, which the doc comments say is "the step
        // that greys a sky" and is solved AFTER the white balance. If the cast is the missing step,
        // this and only this changes the Linked row.
        var gains = doc.ComputeBackgroundNeutralization();
        output.WriteLine("");
        output.WriteLine($"bgNeut     solved={(gains is { } g ? $"({g.R:F4}, {g.G:F4}, {g.B:F4})" : "NULL")}");
        output.WriteLine("same again, with background neutralisation applied");
        output.WriteLine("mode        spcc    R    G    B   spread");
        Render(doc, img, rgba);

        void Render(AstroImageDocument d, Image image, byte[] buf)
        {
            foreach (var mode in new[] { StretchMode.Auto, StretchMode.Linked, StretchMode.Unlinked })
            {
                foreach (var applyCc in new[] { true, false })
                {
                    var u = d.ComputeStretchUniforms(mode, StretchParameters.Default, applyColorCalibration: applyCc);
                    image.RenderStretchedRgba(u, buf);
                    var med = MedianPerChannel(buf);
                    output.WriteLine($"{mode,-10} {(applyCc ? "on " : "off")}   {med[0],3}  {med[1],3}  {med[2],3}   {Spread([med[0], med[1], med[2]]):P1}");
                }
            }
        }
    }

    /// <summary>Median of each of R, G, B over a strided sample; on an astro frame the median pixel is sky.</summary>
    private static int[] MedianPerChannel(ReadOnlySpan<byte> rgba)
    {
        const int Stride = 11; // pixels, coprime with any plausible row width
        var pixels = rgba.Length / 4;
        var n = pixels / Stride;
        var buf = new byte[n];
        var result = new int[3];

        for (var c = 0; c < 3; c++)
        {
            for (var i = 0; i < n; i++)
            {
                buf[i] = rgba[i * Stride * 4 + c];
            }

            Array.Sort(buf);
            result[c] = buf[n / 2];
        }

        return result;
    }

    private static string Fmt(ReadOnlySpan<float> v)
    {
        var parts = new string[v.Length];
        for (var i = 0; i < v.Length; i++) { parts[i] = v[i].ToString("G5"); }
        return "(" + string.Join(", ", parts) + ")";
    }

    /// <summary>max/min - 1, so 0 is perfectly grey and 0.05 is a five percent cast.</summary>
    private static double Spread(ReadOnlySpan<float> v)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var x in v) { if (x < min) min = x; if (x > max) max = x; }
        return min <= 0 ? double.NaN : max / min - 1.0;
    }

    private static double Spread(ReadOnlySpan<int> v)
    {
        Span<float> f = stackalloc float[v.Length];
        for (var i = 0; i < v.Length; i++) { f[i] = v[i]; }
        return Spread(f);
    }

    private static async Task<ICelestialObjectDB> InitDbAsync(CancellationToken ct)
    {
        if (_cachedDb is { } cached) return cached;
        await _dbSem.WaitAsync(ct);
        try
        {
            if (_cachedDb is { } cached2) return cached2;
            var db = new CelestialObjectDB();
            await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);
            _cachedDb = db;
            return db;
        }
        finally
        {
            _dbSem.Release();
        }
    }
}
