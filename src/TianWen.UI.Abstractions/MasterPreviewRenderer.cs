using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpAstro.Png;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.ColorCalibration;
using TianWen.Lib.Stat;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Per-render SPCC outcome surfaced by <see cref="MasterPreviewRenderer.RenderAsync"/>
/// so the calling subcommand can emit a deterministic <c>[stack] ... SPCC=...</c>
/// summary line through its <c>IConsoleHost</c>. Keeps the SPCC diagnostic on the
/// stack command's output channel (always visible in Release) instead of relying on
/// the ILogger pipeline's Information level being enabled.
/// <para>
/// <see cref="Funnel"/> reports WHY the detected-star count dropped to
/// <see cref="InitialMatches"/> -- whether stars never found a Tycho-2 candidate,
/// were past the position tolerance, or had missing photometry. Lets the caller
/// answer "of 500 detected stars why did only N reach SPCC?" without piecing it
/// together from multiple log lines.
/// </para>
/// </summary>
public readonly record struct SpccDiagnostics(
    float WbR, float WbG, float WbB,
    int InitialMatches, int FinalMatches,
    int Iterations,
    Tycho2ColorCalibration.SpccFunnel Funnel,
    TimeSpan Elapsed);

/// <summary>
/// Display-side post-processing for a stacking master: SPCC + sky-bg
/// fallback WB, background neutralisation gain solve, then a stretched
/// PNG preview with sRGB ICC. Pulled out of the original
/// <c>StackingEndToEndManualTest.PostProcessAndWriteAsync</c> so the
/// <see cref="TianWen.Lib.Imaging.Stacking.StackingPipeline"/> can stay
/// Lib-only (no <c>AstroImageDocument</c> dep) while the CLI / test
/// composes the renderer on top.
/// </summary>
public sealed class MasterPreviewRenderer(ICelestialObjectDB? catalogDb, ILogger logger)
{
    /// <summary>
    /// Render <paramref name="master"/> to a stretched preview PNG at
    /// <paramref name="outputPath"/>. Mono masters (single channel) skip
    /// WB + bg-neut entirely and just stretch on the per-channel
    /// histogram. RGB masters try SPCC (needs <paramref name="wcs"/> +
    /// <paramref name="catalogDb"/>) first, then sky-bg WB as fallback,
    /// then degrade to wb=(1,1,1) when neither has enough signal.
    /// </summary>
    /// <param name="master">Integrated master to render. Not mutated.</param>
    /// <param name="sensorMeta">Reference frame's <see cref="ImageMeta"/>.
    /// SPCC needs the sensor model + filter info, which often gets
    /// stripped through the integration pipeline; the manual test learned
    /// to fall back to the source frame's meta when the master's is
    /// empty.</param>
    /// <param name="wcs">Plate-solved WCS for the master, if available.
    /// SPCC is skipped when this is null AND <paramref name="statsWcs"/>
    /// is also null (sky-bg WB still runs).</param>
    /// <param name="statsSource">Cropped master for stats computation
    /// (per-channel background, WB star photometry, stretch shadows /
    /// midtones / rescale). Null falls back to <paramref name="master"/>.
    /// Used so the full-canvas render inherits its colour balance from
    /// the well-covered autocrop region instead of being polluted by
    /// the NaN / zero-coverage edges.</param>
    /// <param name="statsWcs">WCS for <paramref name="statsSource"/> when
    /// it differs from the master (the autocrop is re-solved against the
    /// cropped pixel grid so its CRPIX is offset). Falls back to
    /// <paramref name="wcs"/> when null, which is correct whenever
    /// <paramref name="statsSource"/> is null or shares the master's
    /// grid.</param>
    /// <param name="outputPath">PNG path to write.</param>
    /// <returns>SPCC diagnostics when SPCC ran and produced a gain triple; null when
    /// SPCC was skipped (mono master, missing WCS / catalog, insufficient throughput
    /// data, or fewer than 3 stars).</returns>
    public async Task<SpccDiagnostics?> RenderAsync(
        Image master,
        ImageMeta sensorMeta,
        WCS? wcs,
        Image? statsSource,
        string outputPath,
        WCS? statsWcs = null,
        bool hdr10Pq = false,
        float peakNits = 1000f,
        bool gamutToBt2020 = true,
        CancellationToken ct = default)
    {
        SpccDiagnostics? spccDiagnostics = null;
        var sw = Stopwatch.StartNew();
        var stats = statsSource ?? master;
        // When stats is just master, statsWcs naturally collapses to wcs.
        // When the caller passes an autocrop as stats, they should pass
        // its re-solved WCS too -- the autocrop's CRPIX is offset relative
        // to the full canvas so SPCC needs the cropped WCS to project
        // catalogue stars onto the cropped pixel grid.
        var effectiveWcs = statsWcs ?? wcs;

        // ------------------------------------------------------------
        // Scan per-channel background medians (used to anchor the
        // shader's bg-neut math against the post-WB coordinate space).
        // ------------------------------------------------------------
        float[]? perChannelBg = null;
        if (stats.ChannelCount >= 3)
        {
            var pedestals = new float[stats.ChannelCount];
            (perChannelBg, _) = stats.ScanBackgroundRegion(pedestals);
            logger.LogInformation("  [bgScan] bg=({R:F4}, {G:F4}, {B:F4})",
                perChannelBg[0], perChannelBg[1], perChannelBg[2]);
        }

        // ------------------------------------------------------------
        // White balance: SPCC first, sky-bg fallback, else identity.
        // All photometry / catalog / sky-bg sampling runs on `stats`,
        // not `master`, so the WB gains are anchored to the well-
        // covered region. When the caller passes an autocrop as
        // statsSource, the resulting gains are then applied to the
        // full master via the shader uniforms below.
        // ------------------------------------------------------------
        (float R, float G, float B)? wbGains = null;
        if (stats.ChannelCount >= 3)
        {
            // Detect stars once (cap at 500 -- SPCC's photometry +
            // catalog match scales with detection count and the
            // brightest 500 give the same WB answer as 10k+).
            StarList? statsStars = null;
            try
            {
                statsStars = await stats.FindStarsAsync(channel: 0, snrMin: 5f, maxStars: 500,
                    minStars: 50, maxRetries: 0, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning("  [WB] star detection failed: {Type}: {Msg}", ex.GetType().Name, ex.Message);
            }

            // Post-stack PSF summary on the master. Lets us spot when a
            // stacked output ends up with broader / more elongated stars
            // than the per-frame medians would suggest -- a tell for
            // residual registration drift or aggressive rejection cutting
            // into the best frames.
            if (statsStars is { Count: > 0 } detected)
            {
                var masterHfd = detected.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median);
                var masterFwhm = detected.MapReduceStarProperty(SampleKind.FWHM, AggregationMethod.Median);
                var masterEcc = detected.MapReduceStarProperty(SampleKind.Ellipticity, AggregationMethod.Median);
                logger.LogInformation(
                    "  [masterStats] N={N} hfd={Hfd:F2} fwhm={Fwhm:F2} ecc={Ecc:F3}",
                    detected.Count, masterHfd, masterFwhm, masterEcc);
            }

            if (effectiveWcs is { } w && statsStars is { Count: >= 3 } && catalogDb is { } db)
            {
                var spccSw = Stopwatch.StartNew();
                try
                {
                    if (!FilterCurveDatabase.IsLoaded)
                    {
                        await FilterCurveDatabase.LoadAsync(ct);
                    }
                    var throughputs = FilterCurveDatabase.BuildChannelThroughputs(sensorMeta);
                    if (throughputs is { } t)
                    {
                        var spcc = Tycho2ColorCalibration.ComputeSpectrophotometricWhiteBalance(
                            stats, statsStars, w, db, t.R, t.G, t.B);
                        if (spcc is { } gains)
                        {
                            wbGains = (gains.R, gains.G, gains.B);
                            spccDiagnostics = new SpccDiagnostics(
                                gains.R, gains.G, gains.B,
                                gains.InitialMatches, gains.FinalMatches,
                                gains.Iterations,
                                gains.Funnel,
                                spccSw.Elapsed);
                            logger.LogInformation(
                                "  [SPCC] WB=({R:F3}, {G:F3}, {B:F3}) from {Final}/{Initial} Tycho-2 matches in {Iters} kappa-sigma iter(s) ({Ms} ms)",
                                gains.R, gains.G, gains.B,
                                gains.FinalMatches, gains.InitialMatches, gains.Iterations,
                                spccSw.ElapsedMilliseconds);
                        }
                        else
                        {
                            logger.LogInformation("  [SPCC] insufficient matches; will try sky-bg fallback");
                        }
                    }
                    else
                    {
                        logger.LogInformation("  [SPCC] no channel throughput for this sensor; will try sky-bg fallback");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning("  [SPCC] failed: {Type}: {Msg}", ex.GetType().Name, ex.Message);
                }
            }

            if (wbGains is null && statsStars is { StarMask: { } mask })
            {
                var skyWb = AstroImageDocument.ComputeSkyBackgroundWB(stats, mask);
                if (skyWb is { } w2)
                {
                    wbGains = w2;
                    logger.LogInformation("  [skyBgWB] WB=({R:F3}, {G:F3}, {B:F3})", w2.R, w2.G, w2.B);
                }
            }
        }

        // ------------------------------------------------------------
        // Bg-neut gains. The shader applies bg-neut BEFORE WB:
        //   out = (raw * bn + (1 - bn)) * wb
        // For the post-shader bg to be neutral across channels we need
        //   (bg_X * bn_X + (1 - bn_X)) * wb_X = K
        // Solving for MinPivot (K = min over X of bg_X * wb_X):
        //   bn_X = (K / wb_X - 1) / (bg_X - 1)
        // ------------------------------------------------------------
        (float R, float G, float B)? bgGains = null;
        if (perChannelBg is { Length: >= 3 } bg)
        {
            var wb = wbGains ?? (1f, 1f, 1f);
            var bgRWb = bg[0] * wb.R;
            var bgGWb = bg[1] * wb.G;
            var bgBWb = bg[2] * wb.B;
            var pivot = MathF.Min(bgRWb, MathF.Min(bgGWb, bgBWb));
            float Solve(float bgX, float wbX) =>
                MathF.Abs(bgX - 1f) < 1e-6f ? 1f
                    : Math.Clamp((pivot / wbX - 1f) / (bgX - 1f), 0f, 10f);
            var bn = (Solve(bg[0], wb.R), Solve(bg[1], wb.G), Solve(bg[2], wb.B));
            bgGains = bn;
            logger.LogInformation("  [bgNeut/MinPivot-postWB] target={Target:F4} gains=({R:F3}, {G:F3}, {B:F3})",
                pivot, bn.Item1, bn.Item2, bn.Item3);
        }

        // ------------------------------------------------------------
        // Render PNG. Stretch uniforms carry bg-neut + WB so the
        // shader runs per-channel-normalize -> bg-neut -> WB ->
        // shadow/MTF. Mono short-circuits to a single-channel stretch.
        // ------------------------------------------------------------
        var renderSw = Stopwatch.StartNew();
        var (channelCount, width, height) = master.Shape;
        var perChannelStats = new ChannelStretchStats[channelCount];
        Span<float> bgPerCh = stackalloc float[3];
        if (bgGains is { } gIn) { bgPerCh[0] = gIn.R; bgPerCh[1] = gIn.G; bgPerCh[2] = gIn.B; }
        else { bgPerCh[0] = bgPerCh[1] = bgPerCh[2] = 1f; }
        for (var c = 0; c < channelCount; c++)
        {
            var (ped, med, mad) = stats.GetPedestralMedianAndMADScaledToUnit(c);
            // Pre-adjust into post-bg-neut coordinate space so the
            // shadow lands where the shader sees the pixel after
            // norm = norm * bn + (1 - bn).
            var bn = c < 3 ? bgPerCh[c] : 1f;
            var adjMed = med * bn + (1f - bn);
            var adjMad = mad * MathF.Abs(bn);
            perChannelStats[c] = new ChannelStretchStats(ped, adjMed, adjMad);
        }
        var uniforms = AstroImageDocument.ComputeStretchUniforms(
            StretchMode.Unlinked,
            StretchParameters.Default,
            perChannelStats,
            lumaStats: null,
            imageMaxValue: master.MaxValue,
            whiteBalance: wbGains);
        if (bgGains is { } bg2)
        {
            uniforms = uniforms with { BackgroundNeutralization = bg2 };
        }
        // 16-bit per channel via SharpAstro.Png 3.0's EncodeRgba16 path.
        // 65,536 levels per channel eliminates the banding the 8-bit path
        // produced on smooth nebula gradients (visible after MTF stretch).
        //
        // Colour signalling: PNG-3 cICP -- 4-byte chunk instead of an ICC
        // profile. Default is {sRGB primaries, sRGB transfer, RGB, full
        // range} for SDR display-referred output. When hdr10Pq=true, the
        // rgba buffer is re-encoded to BT.2020+PQ via Bt2020Pq.EncodeInPlace
        // and tagged with cICP Hdr10Pq -- modern browsers + HDR displays
        // interpret it as actual HDR. peakNits sets the "1.0 stretched
        // value -> X nits" mapping (default 1000 nits, cinema HDR10).
        var rgba = new ushort[width * height * 4];
        master.RenderStretchedRgba16(uniforms, rgba);
        CicpChunk cicp;
        if (hdr10Pq)
        {
            Bt2020Pq.EncodeInPlace(rgba, peakNits, gamutToBt2020);
            // BT.2020 + PQ is canonical HDR10. sRGB-primaries + PQ is the
            // "narrow-gamut HDR" variant (cICP {1, 16, 0, 1}) -- non-standard
            // but valid PNG-3 / ICC v4.4 and avoids gamut-mismatch
            // desaturation on consumer HDR pipelines that don't apply the
            // inverse BT.2020-to-display matrix on output.
            cicp = gamutToBt2020 ? CicpChunk.Hdr10Pq : CicpChunk.SrgbPq;
        }
        else
        {
            cicp = CicpChunk.Srgb;
        }
        var pngOpts = new PngWriteOptions { Cicp = cicp };
        var png = PngWriter.EncodeRgba16(rgba, width, height, pngOpts);
        await File.WriteAllBytesAsync(outputPath, png, ct);
        logger.LogInformation("  wrote {Path} ({TotalMs} ms render+encode)",
            outputPath, renderSw.ElapsedMilliseconds);

        logger.LogInformation("  [preview] total {Ms} ms", sw.ElapsedMilliseconds);

        return spccDiagnostics;
    }
}
