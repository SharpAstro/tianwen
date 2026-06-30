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

namespace TianWen.Lib.Imaging.Stacking;

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
/// Result of <see cref="MasterPreviewRenderer.RenderAsync"/>: the SPCC outcome
/// (for the console summary), the <see cref="StretchUniforms"/> the master was
/// rendered with, and the white-balance triple actually used. The PixInsight OSC
/// flow computes the white balance ONCE (gradient correction -> SPCC with stars in),
/// then star-removal and a per-plate stretch follow. So <see cref="MasterPostProcessor"/>
/// reuses only <see cref="WhiteBalance"/> for the <c>--split-plates</c> TIFFs --
/// each plate self-stretches its own background + MTF, sharing just the one colour
/// calibration. (NOT the full <see cref="Uniforms"/>: a plate whose background
/// differs from the master would inherit the wrong bg-neut and pick up a cast.)
/// </summary>
public readonly record struct PreviewRender(
    SpccDiagnostics? Spcc, StretchUniforms Uniforms, (float R, float G, float B)? WhiteBalance);

/// <summary>
/// Display-side post-processing for a stacking master: SPCC + sky-bg
/// fallback WB, background neutralisation gain solve, then a stretched
/// PNG preview with sRGB ICC. CPU-only (no GPU), so it lives in
/// <c>TianWen.Lib</c> and <see cref="MasterPostProcessor"/> drives it
/// in-pipeline: the master PNG and the <c>--split-plates</c> TIFFs all share
/// ONE WB + bg-neut solve, so the plates come out colour-matched to the PNG.
/// The viewer-side stretch math it relies on (<see cref="StretchSolver"/>)
/// moved down from <c>AstroImageDocument</c> for the same reason.
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
    /// <param name="whiteBalanceOverride">When set, this exact WB triple is used and the
    /// SPCC / sky-bg solve is skipped -- the per-plate stretch path passes the one shared
    /// SPCC balance so each plate self-stretches its own background + MTF while inheriting
    /// the master's colour calibration. Null = solve the WB from this image.</param>
    /// <returns>SPCC diagnostics when SPCC ran and produced a gain triple; null when
    /// SPCC was skipped (mono master, missing WCS / catalog, insufficient throughput
    /// data, fewer than 3 stars, or a WB override was supplied).</returns>
    public async Task<PreviewRender> RenderAsync(
        Image master,
        ImageMeta sensorMeta,
        WCS? wcs,
        Image? statsSource,
        string outputPath,
        WCS? statsWcs = null,
        bool hdr10Pq = false,
        float peakNits = 1000f,
        bool gamutToBt2020 = true,
        (float R, float G, float B)? whiteBalanceOverride = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var stats = statsSource ?? master;
        // When stats is just master, statsWcs naturally collapses to wcs.
        // When the caller passes an autocrop as stats, they should pass
        // its re-solved WCS too -- the autocrop's CRPIX is offset relative
        // to the full canvas so SPCC needs the cropped WCS to project
        // catalogue stars onto the cropped pixel grid.
        var effectiveWcs = statsWcs ?? wcs;

        var (uniforms, spccDiagnostics, wbGains) = await ComputeStretchUniformsAsync(
            master, stats, effectiveWcs, sensorMeta, whiteBalanceOverride, ct);

        // ------------------------------------------------------------
        // Render PNG from the computed uniforms.
        // ------------------------------------------------------------
        var renderSw = Stopwatch.StartNew();
        var (channelCount, width, height) = master.Shape;
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
        // Empty outputPath = solve-only: the caller (MasterPostProcessor) just needs
        // the uniforms to stretch the split plates, e.g. --split-plates with
        // --output-format exr/none. Skip the PNG write in that case.
        if (outputPath is { Length: > 0 })
        {
            var pngOpts = new PngWriteOptions { Cicp = cicp };
            var png = PngWriter.EncodeRgba16(rgba, width, height, pngOpts);
            await File.WriteAllBytesAsync(outputPath, png, ct);
            logger.LogInformation("  wrote {Path} ({TotalMs} ms render+encode)",
                outputPath, renderSw.ElapsedMilliseconds);
        }

        logger.LogInformation("  [preview] total {Ms} ms", sw.ElapsedMilliseconds);

        return new PreviewRender(spccDiagnostics, uniforms, wbGains);
    }

    /// <summary>
    /// Stretches <paramref name="plate"/> (a stars-only or starless lineage plate) for the
    /// <c>--split-plates</c> export and writes a display-referred float TIFF (sRGB ICC) for
    /// Photoshop / Affinity layering. The plate SELF-STRETCHES -- its own per-channel
    /// background neutralisation + shadow/MTF, computed from its own pixels -- and only the
    /// <paramref name="sharedWhiteBalance"/> (the master's one SPCC triple) is shared, so
    /// each plate's background lands neutral while the star colours stay on the master's
    /// colour calibration. (Sharing the master's FULL stretch instead would inherit its
    /// bg-neut, which double-corrects a plate whose own background differs and produces a
    /// cast.) The stretch is the exact CPU mirror of the PNG render
    /// (<see cref="Image.RenderStretchedRgba16"/>), 16-bit per channel -> [0,1] float. The
    /// input <paramref name="plate"/> is not mutated.
    /// </summary>
    public async Task RenderStretchedPlateTiffAsync(
        Image plate, (float R, float G, float B)? sharedWhiteBalance, string tiffPath, CancellationToken ct = default)
    {
        // Per-plate solve: WB is supplied (shared), bg-neut + shadow/MTF come from the
        // plate's own statistics. No WCS / catalog -- the WB override skips SPCC.
        var (uniforms, _, _) = await ComputeStretchUniformsAsync(
            plate, plate, effectiveWcs: null, plate.ImageMeta, sharedWhiteBalance, ct);

        var (channelCount, width, height) = plate.Shape;
        var rgba = new ushort[width * height * 4];
        plate.RenderStretchedRgba16(uniforms, rgba);

        // RGBA16 -> [0,1] float Image (R[,G,B]; alpha dropped). Mono plate keeps 1
        // channel; colour keeps 3. WriteStretchedTiffAsync writes the floats verbatim.
        var outCh = channelCount >= 3 ? 3 : 1;
        var data = Image.CreateChannelData(outCh, height, width);
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var i = (row + x) * 4;
                data[0][y, x] = rgba[i] / 65535f;
                if (outCh == 3)
                {
                    data[1][y, x] = rgba[i + 1] / 65535f;
                    data[2][y, x] = rgba[i + 2] / 65535f;
                }
            }
        }

        var stretched = new Image(data, BitDepth.Float32, 1f, 0f, 0f, plate.ImageMeta);
        try
        {
            await stretched.WriteStretchedTiffAsync(tiffPath, ct);
            logger.LogInformation("  wrote {Path} (split plate, self-stretch + shared WB)", tiffPath);
        }
        finally
        {
            stretched.Release();
        }
    }

    /// <summary>
    /// Rewraps <paramref name="img"/> with <c>MinValue = 0</c> so the stretch sees a zero
    /// pedestal (see the rationale in <see cref="ComputeStretchUniformsAsync"/>). Shares the
    /// channel arrays by reference -- no pixel copy -- so it is cheap to call per render.
    /// Returns the input unchanged when its pedestal is already ~0 (the common raw-master case).
    /// </summary>
    private static Image WithZeroPedestal(Image img)
    {
        if (img.MinValue is 0f or float.NaN) return img;
        var data = new float[img.ChannelCount][,];
        for (var c = 0; c < img.ChannelCount; c++)
        {
            data[c] = img.GetChannelArray(c);
        }
        return new Image(data, img.BitDepth, img.MaxValue, 0f, 0f, img.ImageMeta);
    }

    /// <summary>
    /// The shared solve behind both <see cref="RenderAsync"/> (PNG) and
    /// <see cref="RenderStretchedPlateTiffAsync"/> (split-plate TIFF): scan the per-channel
    /// background, settle the white balance, derive the MinPivot bg-neut, and build the
    /// per-channel stretch uniforms -- the single source of the bg-neut + stretch math so
    /// the PNG and the plates never drift. White balance is either SOLVED here (SPCC needs
    /// <paramref name="effectiveWcs"/> + the catalog DB and stars in <paramref name="statsImage"/>;
    /// sky-bg WB is the fallback) or SUPPLIED via <paramref name="wbOverride"/> (the one
    /// shared SPCC triple, so a split plate self-stretches its own background + MTF while
    /// keeping the master's colour calibration). Stats come from <paramref name="statsImage"/>;
    /// the uniforms are sized to <paramref name="renderImage"/> (the image that will be
    /// stretched). Returns the uniforms, the SPCC diagnostics (null when WB was supplied or
    /// SPCC was skipped), and the WB actually used (so the caller can share it across plates).
    /// </summary>
    private async Task<(StretchUniforms Uniforms, SpccDiagnostics? Spcc, (float R, float G, float B)? Wb)>
        ComputeStretchUniformsAsync(
            Image renderImage,
            Image statsImage,
            WCS? effectiveWcs,
            ImageMeta sensorMeta,
            (float R, float G, float B)? wbOverride,
            CancellationToken ct)
    {
        SpccDiagnostics? spccDiagnostics = null;

        // Force the stretch to a ZERO pedestal. The pedestal (MinValue/MaxValue)
        // is subtracted from the per-channel median inside
        // GetPedestralMedianAndMADScaledToUnit; for a GraXpert-flattened master
        // the floor sits at ~half-scale, so subtracting it leaves the faint
        // per-channel medians as tiny near-zero residues where small absolute
        // differences EXPLODE in relative terms (R-ped=0.012 vs G-ped=0.002 ->
        // 6x -> green crushed) or go negative (drizzle median 0.012 - pedestal
        // 0.164 -> the whole frame renders black). The auto-stretch's own
        // shadow clipping (median - k*MAD) already removes the black point, so
        // the floor is just a uniform DC offset best left in place. Rewrapping
        // with MinValue=0 (a cheap view-share, no pixel copy) makes the enhanced
        // master behave exactly like the proven raw-master path (which only ever
        // worked because raw masters happen to have MinValue ~ 0). The render
        // images themselves are stretched elsewhere with the resulting
        // Pedestal=0 uniform, so render + stats stay in one coordinate space.
        var stats = WithZeroPedestal(statsImage);

        // ------------------------------------------------------------
        // Scan per-channel background medians (anchor the bg-neut gains).
        // The shader applies bg-neut to norm = raw*NormFactor - Pedestal;
        // with the zero-pedestal stats above, Pedestal == 0 so the absolute
        // background ScanBackgroundRegion returns (zero pedestal in) IS the
        // shader's coordinate space -- no mismatch.
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
        // White balance: a supplied (shared) triple short-circuits the
        // solve -- that is the per-plate path, where the master's one
        // SPCC balance is reused and only the bg-neut + stretch below
        // are recomputed from this plate's own pixels. Otherwise SPCC
        // first, sky-bg fallback, else identity. All photometry / catalog
        // / sky-bg sampling runs on `stats`, not `renderImage`, so the
        // gains are anchored to the well-covered region.
        // ------------------------------------------------------------
        (float R, float G, float B)? wbGains = wbOverride;
        if (wbGains is { } shared)
        {
            logger.LogInformation("  [WB] shared SPCC white balance ({R:F3}, {G:F3}, {B:F3}) -- plate self-stretches its own bg + MTF",
                shared.R, shared.G, shared.B);
        }
        else if (stats.ChannelCount >= 3)
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
                var skyWb = StretchSolver.ComputeSkyBackgroundWB(stats, mask);
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
        // Per-channel stretch uniforms (sized to the render image).
        // Stretch uniforms carry bg-neut + WB so the shader runs
        // per-channel-normalize -> bg-neut -> WB -> shadow/MTF. Mono
        // short-circuits to a single-channel stretch.
        // ------------------------------------------------------------
        var channelCount = renderImage.ChannelCount;
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
        var uniforms = StretchSolver.ComputeStretchUniforms(
            StretchMode.Unlinked,
            StretchParameters.Default,
            perChannelStats,
            lumaStats: null,
            imageMaxValue: renderImage.MaxValue,
            whiteBalance: wbGains);
        if (bgGains is { } bg2)
        {
            uniforms = uniforms with { BackgroundNeutralization = bg2 };
        }
        return (uniforms, spccDiagnostics, wbGains);
    }

    /// <summary>
    /// Render <paramref name="master"/> to a high-key PLANETARY preview PNG. Unlike
    /// <see cref="RenderAsync"/> -- which is deep-sky (SPCC / sky-bg WB plus an MTF
    /// auto-stretch that targets a faint background and so blows a bright planetary disk
    /// out to white) -- this uses <see cref="Image.ComputePlanetaryStretchUniforms"/>: a
    /// gentle near-linear percentile black-point + common-scale stretch that keeps the disk
    /// in range and the sky colour-neutral. No catalogue / WCS / star detection (a planet
    /// has no field stars to solve), so it is also much faster.
    /// </summary>
    /// <param name="master">Integrated (optionally wavelet-sharpened) master. Not mutated.</param>
    /// <param name="outputPath">PNG path to write (16-bit per channel, sRGB cICP).</param>
    /// <param name="blackPercentile">Per-channel black point (fractional rank). See
    /// <see cref="Image.ComputePlanetaryStretchUniforms"/>.</param>
    /// <param name="whitePercentile">White point (fractional rank).</param>
    /// <param name="gamma">Midtones gamma; 1 = pure linear, &lt; 1 lifts the belts.</param>
    public async Task RenderPlanetaryAsync(
        Image master,
        string outputPath,
        double blackPercentile = 0.005,
        double whitePercentile = 0.999,
        double gamma = 0.75,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var (channelCount, width, height) = master.Shape;
        var uniforms = master.ComputePlanetaryStretchUniforms(blackPercentile, whitePercentile, gamma);

        // 16-bit per channel + sRGB cICP, mirroring RenderAsync's encode tail so the planetary
        // path produces the same well-formed PNG (no 8-bit banding on the smooth disk gradient).
        var rgba = new ushort[width * height * 4];
        master.RenderStretchedRgba16(uniforms, rgba);
        var png = PngWriter.EncodeRgba16(rgba, width, height, new PngWriteOptions { Cicp = CicpChunk.Srgb });
        await File.WriteAllBytesAsync(outputPath, png, ct);

        logger.LogInformation("  [planetaryPreview] wrote {Path} ({Ms} ms, {Ch}ch {W}x{H}, gamma={Gamma:F2})",
            outputPath, sw.ElapsedMilliseconds, channelCount, width, height, gamma);
    }
}
