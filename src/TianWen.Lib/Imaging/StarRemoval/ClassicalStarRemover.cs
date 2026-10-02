using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Sources;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// The classical starless plate builder (R0 of docs/plans/star-remover-training.md): every point source is found, a
/// pixel-integrated Moffat is fitted at each and subtracted where the fit says it is a star, and whatever the fit
/// could not take (a saturated core, a ring, a residual beyond the noise) is inpainted with the plate's own grain.
/// </summary>
/// <remarks>
/// <para><b>The fit is the star test.</b> A point source whose fit wants a width well beyond the field's stars, or
/// whose model explains under half its peak, is a knot of structure and is left in the plate; a nebula's knots are
/// what a remover must keep, and a plate that removed them would never show the net one to keep.</para>
/// <para><b>Brightest first, then once more with the neighbours gone.</b> Stars are fitted on a working copy in
/// order of significance, each subtracted before the next, so a faint star is fitted on what its brighter neighbours
/// left; one refinement pass then puts each star back and fits it again with every neighbour, brighter or fainter,
/// already subtracted, and the finder runs again on the residual for stars a brighter one hid.</para>
/// <para><b>Parallel by tiles that cannot touch.</b> A fit reads and a subtraction writes at most
/// <see cref="MaxSubtractRadius"/> pixels from its star, so stars are grouped into tiles of twice that and the tiles
/// run in four phases of a checkerboard: two tiles of one phase are a whole tile apart and no two windows of a phase
/// overlap. The order inside a tile is the significance order.</para>
/// <para>Deterministic: the fill's noise comes from a counter-based hash of the seed, the channel and the pixel, so the
/// same image and seed give the same plate on any number of threads.</para>
/// </remarks>
public static class ClassicalStarRemover
{
    internal const int MaxSubtractRadius = 128;
    private const int MaxFitRadius = 96;
    private const int TileSize = 2 * MaxSubtractRadius;
    private const int RefinementPasses = 2;
    private const int HoleSweepRounds = 3;
    private const double CompressedCoreFraction = 0.8;
    internal const float HoleSigma = 3f;
    private const float DefaultBeta = 3f;
    private const float DefaultFwhm = 2.5f;
    private static readonly double[] WidthGrid = { 0.7, 0.85, 1.0, 1.2, 1.4, 2.0 };
    private const float MaxSeedBeta = 6f;
    private const double SizingBeta = 4.0;
    private static readonly float[] BandEdges = { 5f, 10f, 20f, 50f, 100f, float.PositiveInfinity };

    /// <summary>
    /// Builds the starless plate of <paramref name="image"/>: a linear master, one channel (mono) or three
    /// (demosaiced colour). A one-channel colour mosaic is refused; demosaic it first.
    /// </summary>
    public static async Task<StarlessPlate> BuildAsync(
        Image image, StarlessPlateOptions? options = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= StarlessPlateOptions.Default;
        var (channels, _, _) = image.Shape;
        if (channels == 1 && image.ImageMeta.SensorType is SensorType.RGGB)
        {
            throw new ArgumentException("A colour mosaic must be demosaiced before its starless plate is built.", nameof(image));
        }
        if (channels is not (1 or 3))
        {
            throw new ArgumentException($"A starless plate is built from one or three channels, not {channels}.", nameof(image));
        }

        var sw = Stopwatch.StartNew();
        var fwhm = new float[channels + 1];
        var beta = new float[channels + 1];
        for (var c = 0; c < channels; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (fwhm[c], beta[c]) = await MeasurePsfAsync(image, c, logger, cancellationToken);
        }
        fwhm[channels] = (float)fwhm.Take(channels).Average();
        beta[channels] = (float)beta.Take(channels).Average();
        // The stacked profile only seeds beta: it reads the grid's ceiling (24.95, a Gaussian) on half the store's
        // masters, and a seed that light would size every calibration window to the core alone.
        var seedBeta = Math.Min(beta[channels], MaxSeedBeta);
        var lumPsf = new MoffatPsf(MoffatPsf.AlphaFor(fwhm[channels], seedBeta), seedBeta);

        return await Task.Run(
            () => new Builder(image, options, lumPsf, fwhm, beta, logger, cancellationToken).Run(sw),
            cancellationToken);
    }

    private static async Task<(float Fwhm, float Beta)> MeasurePsfAsync(Image image, int channel, ILogger? logger, CancellationToken ct)
    {
        var stars = await image.FindStarsAsync(channel, snrMin: 20f, maxStars: 500, cancellationToken: ct);
        if (PsfProfileFit.Measure(image, channel, stars) is { } fit && fit.Fwhm > 0 && double.IsFinite(fit.MoffatBeta))
        {
            return ((float)fit.Fwhm, (float)fit.MoffatBeta);
        }
        var widths = stars.Select(static s => s.StarFWHM).Where(static f => f > 0f).OrderBy(static f => f).ToArray();
        var fwhm = widths.Length > 0 ? widths[widths.Length / 2] : DefaultFwhm;
        logger?.LogWarning(
            "ClassicalStarRemover: channel {Channel}: no stacked profile from {Stars} stars; FWHM {Fwhm:F2} px from their median, beta {Beta} assumed.",
            channel, stars.Count, fwhm, DefaultBeta);
        return (fwhm, DefaultBeta);
    }

    /// <summary>A fitted star's model, enough to put it back and take it out again.</summary>
    private struct Fit
    {
        public double X, Y, Width, Amplitude, Sky, SkyX, SkyY;
        public int WindowX, WindowY, FitRadius, SubtractRadius;
        public double[] ChannelAmplitudes;
        public double Plateau, Beta;
        public bool Saturated;
        public StarFitOutcome Outcome;
    }

    private sealed class Builder(
        Image image, StarlessPlateOptions options, MoffatPsf lumPsf, float[] fwhm, float[] beta,
        ILogger? logger, CancellationToken ct)
    {
        private readonly int _channels = image.Shape.ChannelCount;
        private readonly int _width = image.Shape.Width;
        private readonly int _height = image.Shape.Height;
        private readonly BitMatrix? _absent = image.AbsentPixels();
        private float[][] _work = Array.Empty<float[]>();
        private float[] _lum = Array.Empty<float>();
        private float[] _rms = Array.Empty<float>();
        private float[] _sky = Array.Empty<float>();
        private double _fieldScale = 1.0;
        private double _lumAlpha = lumPsf.Alpha;
        private double _beta = lumPsf.Beta;
        private RadialCorrection? _correction;
        private double[] _channelAlpha = Array.Empty<double>();
        private double[] _channelBeta = Array.Empty<double>();
        private RadialCorrection?[] _channelCorrection = Array.Empty<RadialCorrection?>();
        private float[][] _channelRms = Array.Empty<float[]>();

        // The field's PSF at a star's relative width: the luminance's, and each channel's at its measured FWHM ratio.
        private MoffatPsf LumPsf(double width, double beta = 0) => new MoffatPsf(_lumAlpha * width, beta > 0 ? beta : _beta);

        // Each colour channel has its own PSF, calibrated on its own plane: a refractor's red is often much wider than its
        // green (the SMC master's red star was a broad low disc beside a sharp green one), and red derived from the
        // luminance by one FWHM ratio was over-subtracted into a hole.
        private MoffatPsf ChannelPsf(int channel, double width, double beta = 0)
            => _channels == 1 ? LumPsf(width, beta)
                : _channelAlpha.Length == _channels ? new MoffatPsf(_channelAlpha[channel] * width, _channelBeta[channel])
                : new MoffatPsf(_lumAlpha * width * fwhm[channel] / fwhm[_channels], beta > 0 ? beta : _beta);

        private RadialCorrection? ChannelCorrection(int channel)
            => _channels == 1 ? _correction : _channelCorrection.Length == _channels ? _channelCorrection[channel] : _correction;

        private double ModelChannel(int channel, MoffatPsf psf, int x, int y, double cx, double cy)
            => ChannelCorrection(channel) is { } t ? t.Model(psf, x, y, cx, cy) : psf.PixelMean(x, y, cx, cy);

        // A star's FWHM over the field's: what the knot test reads, so a bright star whose wings want a lower beta is
        // judged by its core, not by how far a fixed-beta model had to widen to follow them.
        private double FwhmRatio(double width, double beta) => LumPsf(width, beta).Fwhm / LumPsf(1.0).Fwhm;

        private double BetaOf(in LinearResult r) => double.IsFinite(r.Beta) && r.Beta > 0 ? r.Beta : _beta;

        // A star's model at a pixel, peak 1: the Moffat with the field's residual table added once it exists.
        private double Model(MoffatPsf psf, int x, int y, double cx, double cy)
            => _correction is { } c ? c.Model(psf, x, y, cx, cy) : psf.PixelMean(x, y, cx, cy);

        // A fit window reaches where the star falls to the noise, sized as if beta were at most 4 and never under 4
        // FWHM: a window sized from a light-winged beta holds the core alone, and the wings that would correct beta
        // are never seen.
        private double WindowRadius(double amplitude, double sigma, double width = 1.0)
        {
            var fwhmPx = LumPsf(width).Fwhm;
            var sizing = Math.Min(_beta, SizingBeta);
            var psf = new MoffatPsf(MoffatPsf.AlphaFor(fwhmPx, sizing), sizing);
            return Math.Max(psf.RadiusAtLevel(amplitude, sigma), 4.0 * fwhmPx);
        }

        // A star's own beta is kept only where it describes a star: physical, and with an FWHM a star of this field can
        // have. On an undersampled star a free beta trades a tiny core against heavy wings (252 bright stars of the
        // Centaurus A master came out "too narrow" that way); the field's beta then decides.
        private LinearResult? AcceptedBetaFit(LinearResult? fit)
            => fit is { Beta: > 1.2 and < 20.0 } r && FwhmRatio(r.Width, r.Beta) is var ratio
                && ratio >= options.MinWidthScale && ratio <= options.MaxWidthScale
                ? r
                : null;

        public StarlessPlate Run(Stopwatch sw)
        {
            var n = _width * _height;
            _work = new float[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                _work[c] = image.GetChannelSpan(c).ToArray();
            }
            _lum = _channels == 1 ? _work[0] : new float[n];
            if (_channels > 1)
            {
                for (var i = 0; i < n; i++)
                {
                    _lum[i] = (_work[0][i] + _work[1][i] + _work[2][i]) / 3f;
                }
            }

            var (found, skyMap) = PointSourceFinder.Find(_lum, _width, _height, _absent, lumPsf.Fwhm, options.DetectionSigma);
            _rms = new float[n];
            _sky = new float[n];
            skyMap.FillRms(_rms);
            skyMap.FillBackground(_sky);
            ct.ThrowIfCancellationRequested();

            (_lumAlpha, _beta) = CalibratePlane(found, _lum, lumPsf);
            _fieldScale = _lumAlpha / lumPsf.Alpha;
            _correction = BuildRadialCorrection(found, _lum, LumPsf(1.0));
            CalibrateChannels(found);
            logger?.LogDebug("ClassicalStarRemover: radial correction from {Stars} stars, reach {Reach:F1} px.", _correction?.Stars ?? 0, _correction?.Reach ?? 0);
            var fits = new Fit[found.Length];
            var firstCrowded = Crowding(found);
            RunTiled(found.Length, i => (found[i].X, found[i].Y), i => fits[i] = FitAndSubtract(found[i], null, firstCrowded[i]));
            ct.ThrowIfCancellationRequested();

            var sources = found.ToList();
            var fitList = fits.ToList();
            var secondFrom = sources.Count;
            if (options.SecondPass)
            {
                // The matched filter merges a pair closer than about 1.5 FWHM into one peak; the fainter one shows on
                // the residual once the brighter is out.
                var (residualFound, _) = PointSourceFinder.Find(_lum, _width, _height, _absent, lumPsf.Fwhm, options.DetectionSigma);
                var firstPass = new PointGrid(found, lumPsf.Fwhm);
                var fresh = residualFound.Where(r => !firstPass.AnyWithin(r.X, r.Y, lumPsf.Fwhm)).ToArray();
                var freshFits = new Fit[fresh.Length];
                var freshCrowded = Crowding(fresh, found);
                RunTiled(fresh.Length, i => (fresh[i].X, fresh[i].Y), i => freshFits[i] = FitAndSubtract(fresh[i], null, freshCrowded[i]));
                sources.AddRange(fresh);
                fitList.AddRange(freshFits);
            }
            ct.ThrowIfCancellationRequested();

            // Refinement over everything found, twice: each star back in, fitted again with every neighbour (brighter
            // or fainter, first pass or second) already out, and its crowding judged on the complete list, so the
            // brighter of a pair the filter merged learns of its companion here.
            var all = sources.ToArray();
            var crowded = Crowding(all);
            for (var pass = 0; pass < RefinementPasses; pass++)
            {
                RunTiled(all.Length, i => (all[i].X, all[i].Y), i =>
                {
                    if (fitList[i].Outcome == StarFitOutcome.Subtracted)
                    {
                        AddModel(fitList[i], +1);
                        fitList[i] = FitAndSubtract(all[i], fitList[i], crowded[i]);
                    }
                });
                ct.ThrowIfCancellationRequested();
            }

            MergeCoreFragments(all, fitList);
            var inpainted = BuildInpaintMask(fitList);
            var subtracted = BuildSubtractedMask(fitList);
            var correlation = FillHoles(inpainted);
            RefreshLuminance();
            SweepHoles(fitList, inpainted);
            _channelRms = ChannelRms();

            var stars = Describe(sources, fitList, secondFrom, inpainted);
            var statistics = Measure(stars, inpainted, subtracted, correlation, sw);
            var plate = BuildPlate();
            logger?.LogInformation(
                "ClassicalStarRemover: {W}x{H}x{C}: {Found} point sources, {Subtracted} subtracted, {Knots} left as structure, inpaint {Inpaint:P2}, field width scale {Scale:F3}, {Ms} ms.",
                _width, _height, _channels, stars.Length, stars.Count(static s => s.Outcome == StarFitOutcome.Subtracted),
                stars.Count(static s => s.Outcome is StarFitOutcome.Knot or StarFitOutcome.TooNarrow), statistics.InpaintFraction,
                _fieldScale, sw.ElapsedMilliseconds);
            return new StarlessPlate(plate, subtracted, inpainted, stars, statistics);
        }

        // Four checkerboard phases of tiles; within a tile, the given order (the significance order).
        private void RunTiled(int count, Func<int, (float X, float Y)> position, Action<int> body)
        {
            var tilesX = (_width + TileSize - 1) / TileSize;
            var tilesY = (_height + TileSize - 1) / TileSize;
            var members = new List<int>[tilesX * tilesY];
            for (var i = 0; i < count; i++)
            {
                var (x, y) = position(i);
                var tx = Math.Clamp((int)(x / TileSize), 0, tilesX - 1);
                var ty = Math.Clamp((int)(y / TileSize), 0, tilesY - 1);
                (members[ty * tilesX + tx] ??= new List<int>()).Add(i);
            }
            for (var phase = 0; phase < 4; phase++)
            {
                ct.ThrowIfCancellationRequested();
                var tiles = new List<int>();
                for (var ty = phase >> 1; ty < tilesY; ty += 2)
                {
                    for (var tx = phase & 1; tx < tilesX; tx += 2)
                    {
                        if (members[ty * tilesX + tx] is not null)
                        {
                            tiles.Add(ty * tilesX + tx);
                        }
                    }
                }
                ParallelFor.Run(tiles.Count, t =>
                {
                    if (members[tiles[t]] is { } list)
                    {
                        foreach (var i in list)
                        {
                            body(i);
                        }
                    }
                });
            }
        }

        // The field's PSF in the model the builder subtracts with: the medians over bright, unsaturated, isolated stars of
        // a width and a beta fitted together, before anything is subtracted. The stacked profile only seeds it: measured
        // on pixel samples with alpha tied to its half-maximum crossing, it read beta 4.0 off a field rendered at 3.0, and
        // a beta that wrong leaves every bright star a ring.
        private (double Alpha, double Beta) CalibratePlane(PointSource[] found, float[] plane, MoffatPsf seed)
        {
            var isolation = 3.0 * lumPsf.Fwhm;
            var grid = new PointGrid(found, isolation);
            var picks = new List<PointSource>();
            for (var k = 0; k < found.Length && picks.Count < 300; k++)
            {
                var s = found[k];
                if (s.Significance is >= 30f and <= 1000f && !IsSaturated(s, out _) && !grid.AnyWithin(s.X, s.Y, isolation, except: k))
                {
                    picks.Add(s);
                }
            }
            if (picks.Count < 10)
            {
                return (seed.Alpha, seed.Beta);
            }
            var scales = new double[picks.Count];
            var betas = new double[picks.Count];
            ParallelFor.Run(picks.Count, i =>
            {
                var s = picks[i];
                var window = CutWindow(s, 1.5 * WindowRadius(s.Peak, Math.Max(_rms[s.PeakY * _width + s.PeakX], 1e-12)), saturationCut: double.PositiveInfinity, plane: plane);
                // Nothing is subtracted yet, so a window wide enough to see the wings also holds its neighbours' light:
                // pixels within 2 FWHM of any other source take no part (with them a field rendered at beta 3.0 read 3.31).
                if (window is { } cw)
                {
                    var self = Array.IndexOf(found, s);
                    for (var p = 0; p < cw.Weights.Length; p++)
                    {
                        if (cw.Weights[p] > 0 && grid.AnyWithin(cw.Xs[p], cw.Ys[p], 2.0 * lumPsf.Fwhm, except: self))
                        {
                            cw.Weights[p] = 0;
                        }
                    }
                }
                var fitted = window is { } w ? FitNonlinear(w, seed.Alpha, seed.Beta, s.X, s.Y, fitBeta: true) : null;
                (scales[i], betas[i]) = fitted is { } r ? (r.Width, r.Beta) : (double.NaN, double.NaN);
            });
            var good = Enumerable.Range(0, picks.Count)
                .Where(i => double.IsFinite(scales[i]) && scales[i] is > 0.3 and < 3.0 && betas[i] is > 1.0 and < 25.0)
                .ToArray();
            if (good.Length < 10)
            {
                return (seed.Alpha, seed.Beta);
            }
            var scale = good.Select(i => scales[i]).OrderBy(static v => v).ElementAt(good.Length / 2);
            var beta = good.Select(i => betas[i]).OrderBy(static v => v).ElementAt(good.Length / 2);
            logger?.LogDebug("ClassicalStarRemover: field PSF from {Stars} stars: width scale {Scale:F3}, beta {Beta:F2} (seed {Seed:F2}).",
                good.Length, scale, beta, seed.Beta);
            return (seed.Alpha * scale, beta);
        }

        // The field's residual table: bright, unsaturated, isolated stars fitted at the field's Moffat (their own centre and
        // width), each pixel's departure from it over the star's amplitude, against a sky read beyond the table's reach so
        // a halo the fit's sky would absorb is counted; pixels near any other source take no part.
        private RadialCorrection? BuildRadialCorrection(PointSource[] found, float[] plane, MoffatPsf field)
        {
            var fwhmPx = field.Fwhm;
            var reach = Math.Min(40.0, 10.0 * fwhmPx);
            var isolation = 3.0 * fwhmPx;
            var grid = new PointGrid(found, isolation);
            var picks = new List<int>();
            for (var k = 0; k < found.Length && picks.Count < 400; k++)
            {
                var s = found[k];
                if (s.Significance is >= 50f and <= 3000f && !IsSaturated(s, out _) && !grid.AnyWithin(s.X, s.Y, isolation, except: k))
                {
                    picks.Add(k);
                }
            }
            var perStar = new List<(float Distance, float Residual)>?[picks.Count];
            ParallelFor.Run(picks.Count, i =>
            {
                var k = picks[i];
                var s = found[k];
                if (CutWindow(s, reach + 8.0, double.PositiveInfinity, plane: plane) is not { } window)
                {
                    return;
                }
                for (var p = 0; p < window.Weights.Length; p++)
                {
                    if (window.Weights[p] > 0 && grid.AnyWithin(window.Xs[p], window.Ys[p], 2.0 * fwhmPx, except: k))
                    {
                        window.Weights[p] = 0;
                    }
                }
                if (FitNonlinear(window, field.Alpha, field.Beta, s.X, s.Y, fitBeta: false) is not { Amplitude: > 0 } fit
                    || fit.Width is < 0.7 or > 1.4)
                {
                    return;
                }
                var skyValues = new List<double>();
                for (var p = 0; p < window.Weights.Length; p++)
                {
                    var d = Math.Sqrt((window.Xs[p] - fit.X) * (window.Xs[p] - fit.X) + (window.Ys[p] - fit.Y) * (window.Ys[p] - fit.Y));
                    if (window.Weights[p] > 0 && d >= reach && d <= reach + 8.0)
                    {
                        skyValues.Add(window.Values[p]);
                    }
                }
                if (skyValues.Count < 30)
                {
                    return;
                }
                skyValues.Sort();
                var sky = skyValues[skyValues.Count / 2];
                var psf = field.Scaled(fit.Width);
                var samples = new List<(float, float)>();
                for (var p = 0; p < window.Weights.Length; p++)
                {
                    var d = Math.Sqrt((window.Xs[p] - fit.X) * (window.Xs[p] - fit.X) + (window.Ys[p] - fit.Y) * (window.Ys[p] - fit.Y));
                    if (window.Weights[p] > 0 && d < reach)
                    {
                        var model = psf.PixelMean(window.Xs[p], window.Ys[p], fit.X, fit.Y);
                        samples.Add(((float)(d / fit.Width), (float)((window.Values[p] - sky - fit.Amplitude * model) / fit.Amplitude)));
                    }
                }
                perStar[i] = samples;
            });
            var pooled = new List<(float Distance, float Residual)>();
            var stars = 0;
            foreach (var list in perStar)
            {
                if (list is { Count: > 0 })
                {
                    pooled.AddRange(list);
                    stars++;
                }
            }
            return RadialCorrection.Build(pooled, reach, field.Alpha, stars);
        }

        private void CalibrateChannels(PointSource[] found)
        {
            _channelAlpha = new double[_channels];
            _channelBeta = new double[_channels];
            _channelCorrection = new RadialCorrection?[_channels];
            if (_channels == 1)
            {
                _channelAlpha[0] = _lumAlpha;
                _channelBeta[0] = _beta;
                _channelCorrection[0] = _correction;
                return;
            }
            for (var c = 0; c < _channels; c++)
            {
                var seed = new MoffatPsf(_lumAlpha * fwhm[c] / fwhm[_channels], _beta);
                (_channelAlpha[c], _channelBeta[c]) = CalibratePlane(found, _work[c], seed);
                var field = new MoffatPsf(_channelAlpha[c], _channelBeta[c]);
                _channelCorrection[c] = BuildRadialCorrection(found, _work[c], field);
                logger?.LogDebug("ClassicalStarRemover: channel {Channel}: FWHM {Fwhm:F2} px, beta {Beta:F2}, table from {Stars} stars.",
                    c, field.Fwhm, field.Beta, _channelCorrection[c]?.Stars ?? 0);
            }
        }

        private bool IsSaturated(PointSource s, out double plateau)
        {
            var radius = Math.Max(2, (int)Math.Ceiling(lumPsf.Fwhm));
            var peak = double.NegativeInfinity;
            for (var y = Math.Max(0, s.PeakY - radius); y <= Math.Min(_height - 1, s.PeakY + radius); y++)
            {
                for (var x = Math.Max(0, s.PeakX - radius); x <= Math.Min(_width - 1, s.PeakX + radius); x++)
                {
                    peak = Math.Max(peak, _lum[y * _width + x]);
                }
            }
            plateau = peak;
            if (s.Significance < 50f)
            {
                return false;
            }
            var sky = _sky[s.PeakY * _width + s.PeakX];
            var near = peak - 0.02 * (peak - sky);
            var count = 0;
            for (var y = Math.Max(0, s.PeakY - radius); y <= Math.Min(_height - 1, s.PeakY + radius); y++)
            {
                for (var x = Math.Max(0, s.PeakX - radius); x <= Math.Min(_width - 1, s.PeakX + radius); x++)
                {
                    if (_lum[y * _width + x] >= near)
                    {
                        count++;
                    }
                }
            }
            return count >= 5;
        }

        // A source with another within 2.5 FWHM: subtracted at the field's width, so it cannot widen to take its neighbour.
        private bool[] Crowding(PointSource[] sources, PointSource[]? others = null)
        {
            var radius = 2.5 * lumPsf.Fwhm;
            var own = new PointGrid(sources, radius);
            var theirs = others is { Length: > 0 } o ? new PointGrid(o, radius) : null;
            var crowded = new bool[sources.Length];
            for (var k = 0; k < sources.Length; k++)
            {
                crowded[k] = own.AnyWithin(sources[k].X, sources[k].Y, radius, except: k)
                    || theirs is { } t && t.AnyWithin(sources[k].X, sources[k].Y, radius);
            }
            return crowded;
        }

        private Fit FitAndSubtract(PointSource s, Fit? previous, bool crowded)
        {
            var fit = new Fit { X = s.X, Y = s.Y, Width = 1.0, Outcome = StarFitOutcome.NoFit, ChannelAmplitudes = new double[_channels] };
            var index = s.PeakY * _width + s.PeakX;
            var sigma = Math.Max(_rms[index], 1e-12);
            var sky = _sky[index];
            fit.Saturated = IsSaturated(s, out var plateau);
            fit.Plateau = plateau;
            if (!fit.Saturated && s.Significance >= options.BetaFitSigma && CoreIsCompressed(s, sigma, sky, plateau))
            {
                fit.Saturated = true;
            }

            var startX = previous?.X ?? s.X;
            var startY = previous?.Y ?? s.Y;
            var amplitude = previous?.Amplitude ?? (fit.Saturated ? 4.0 * (plateau - sky) : Math.Max(s.Peak, sigma));
            var radius = WindowRadius(amplitude, sigma);
            if (fit.Saturated)
            {
                radius = Math.Max(radius, 4.0 * lumPsf.Fwhm + 3.0 * Math.Sqrt(CountPlateau(s, plateau, sky) / Math.PI));
            }
            var saturationCut = fit.Saturated ? sky + 0.7 * (plateau - sky) : double.PositiveInfinity;

            LinearResult? result = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (CutWindow(s, radius, saturationCut, startX, startY) is not { } window)
                {
                    return fit;
                }
                // A saturated star is fitted at the field's width: with its core excluded only the wings are left, and
                // far from the centre a Moffat is a power law in r, so width and amplitude trade freely (a free width
                // ran to 0.03 of the field's with an amplitude of 5e9). It has the field's PSF; only its centre and
                // amplitude are read from the wings.
                // From BetaFitSigma up a star's own beta is fitted too: a bright star's wings (halo, scatter, its
                // spikes) are wider than the field's mean profile, and at a fixed beta the fit can only follow them by
                // widening the core, which called an 11,833-sigma star a knot at 1.72 times the field's width.
                result = fit.Saturated
                    ? FitNonlinear(window, _lumAlpha, _beta, startX, startY, fitBeta: false, fitWidth: false, corr: _correction)
                    : s.Significance >= options.BetaFitSigma && _correction is null
                        ? AcceptedBetaFit(FitNonlinear(window, _lumAlpha, _beta, startX, startY, fitBeta: true))
                            ?? FitNonlinear(window, _lumAlpha, _beta, startX, startY, fitBeta: false)
                        : s.Significance >= options.NonlinearFitSigma
                            ? FitNonlinear(window, _lumAlpha, _beta, startX, startY, fitBeta: false, corr: _correction)
                            : FitOnGrid(window, _lumAlpha, _beta, startX, startY, out _, _correction);
                if (result is not { } r || !(r.Amplitude > 0))
                {
                    return fit;
                }
                var wanted = WindowRadius(r.Amplitude, sigma, r.Width);
                if (wanted <= radius * 1.3 || radius >= MaxFitRadius)
                {
                    break;
                }
                radius = wanted;
                startX = r.X;
                startY = r.Y;
            }
            if (result is not { } final)
            {
                return fit;
            }

            fit.X = final.X;
            fit.Y = final.Y;
            fit.Width = final.Width;
            fit.Beta = BetaOf(final);
            fit.Amplitude = final.Amplitude;
            fit.Sky = final.Sky;
            fit.SkyX = final.SkyX;
            fit.SkyY = final.SkyY;
            fit.WindowX = final.WindowX;
            fit.WindowY = final.WindowY;
            fit.FitRadius = final.Radius;

            if (Math.Abs(final.X - s.X) > 2.0 || Math.Abs(final.Y - s.Y) > 2.0)
            {
                return fit;
            }
            var ratio = FwhmRatio(final.Width, fit.Beta);
            if (ratio > options.MaxWidthScale)
            {
                fit.Outcome = StarFitOutcome.Knot;
                return fit;
            }
            if (ratio < options.MinWidthScale)
            {
                fit.Outcome = StarFitOutcome.TooNarrow;
                return fit;
            }
            if (!fit.Saturated)
            {
                var px = (int)Math.Round(final.X);
                var py = (int)Math.Round(final.Y);
                if (px >= 0 && px < _width && py >= 0 && py < _height)
                {
                    var observed = _lum[py * _width + px] - final.SkyAt(px, py);
                    var modelled = final.Amplitude * Model(LumPsf(final.Width, fit.Beta), px, py, final.X, final.Y);
                    if (observed > 0 && modelled < options.MinPeakExplained * observed)
                    {
                        fit.Outcome = StarFitOutcome.Knot;
                        return fit;
                    }
                }
            }

            // A crowded star is classified at its free width but subtracted at the field's: a free width lets the
            // brighter of a close pair widen and take part of its companion (1.30 times the field's and 86 percent of
            // the amplitude, on a pair 1.33 FWHM apart, before this).
            if (crowded && CutWindow(s, final.Radius, saturationCut, final.X, final.Y) is { } crowdedWindow)
            {
                var fixedWidth = s.Significance >= options.NonlinearFitSigma || fit.Saturated
                    ? FitNonlinear(crowdedWindow, _lumAlpha, _beta, final.X, final.Y, fitBeta: false, fitWidth: false, corr: _correction)
                    : SolveLinear(crowdedWindow, LumPsf(1.0), final.X, final.Y, _correction);
                if (fixedWidth is { } fw && fw.Amplitude > 0 && Math.Abs(fw.X - s.X) <= 2.0 && Math.Abs(fw.Y - s.Y) <= 2.0)
                {
                    final = fw with { Width = 1.0, Beta = _beta };
                    fit.X = final.X;
                    fit.Y = final.Y;
                    fit.Width = 1.0;
                    fit.Beta = _beta;
                    fit.Amplitude = final.Amplitude;
                    fit.Sky = final.Sky;
                    fit.SkyX = final.SkyX;
                    fit.SkyY = final.SkyY;
                    fit.WindowX = final.WindowX;
                    fit.WindowY = final.WindowY;
                }
            }

            fit.SubtractRadius = (int)Math.Clamp(
                Math.Ceiling(Math.Max(LumPsf(final.Width, fit.Beta).RadiusAtLevel(final.Amplitude, 0.1 * sigma),
                    _correction is { } table ? table.Reach * final.Width : 0.0)),
                final.Radius, MaxSubtractRadius);
            if (_channels == 1)
            {
                fit.ChannelAmplitudes[0] = final.Amplitude;
            }
            else
            {
                for (var c = 0; c < _channels; c++)
                {
                    fit.ChannelAmplitudes[c] = CutWindow(s, final.Radius, saturationCut, final.X, final.Y, _work[c]) is { } cw
                        ? SolveLinear(cw, ChannelPsf(c, final.Width, fit.Beta), final.X, final.Y, ChannelCorrection(c)).Amplitude
                        : 0.0;
                }
            }
            fit.Outcome = StarFitOutcome.Subtracted;
            AddModel(fit, -1);
            return fit;
        }

        // A core fainter than its own wings predict is saturated, hard or soft: the wings alone (below half the peak)
        // fitted at the field's PSF, their predicted peak against the observed one. The flat-top test needs five pixels
        // within 2 percent of the peak, which an undersampled star never has: an 11,833-sigma star at 2.3 px FWHM, its
        // fitted peak past the master's full scale, was fitted as a star 1.72 times too wide and left as a knot.
        private bool CoreIsCompressed(PointSource s, double sigma, double sky, double peak)
        {
            var above = peak - sky;
            if (!(above > 0))
            {
                return false;
            }
            var radius = LumPsf(1.0).RadiusAtLevel(above, sigma);
            if (CutWindow(s, radius, sky + 0.5 * above) is not { } wings
                || FitNonlinear(wings, _lumAlpha, _beta, s.X, s.Y, fitBeta: false, fitWidth: false, corr: _correction) is not { } w
                || !(w.Amplitude > 0))
            {
                return false;
            }
            var predicted = w.Amplitude * Model(LumPsf(1.0), s.PeakX, s.PeakY, w.X, w.Y) + w.SkyAt(s.PeakX, s.PeakY) - sky;
            return above < CompressedCoreFraction * predicted;
        }

        // A bright star's core has more than one local maximum (a saturated plateau's rim, a real PSF's lumps), and the
        // finder's one-FWHM merge leaves the outer ones: 252 on the Centaurus A master, every one 1.7 to 2.9 px from a star
        // of over 10,000 sigma. A non-star peak within 1.5 FWHM of a subtracted star ten times its significance (plus the
        // core, where that star is saturated) is part of it; counted as a source of its own it would fill the bright
        // band with false "too narrow" ones.
        private void MergeCoreFragments(PointSource[] sources, List<Fit> fits)
        {
            var reach = Math.Max(2.0, 1.5 * lumPsf.Fwhm);
            var grid = new PointGrid(sources, 8.0);
            for (var k = 0; k < sources.Length; k++)
            {
                var f = fits[k];
                if (f.Outcome != StarFitOutcome.Subtracted)
                {
                    continue;
                }
                var radius = reach;
                if (f.Saturated)
                {
                    var cx = (int)Math.Clamp(Math.Round(f.X), 0, _width - 1);
                    var cy = (int)Math.Clamp(Math.Round(f.Y), 0, _height - 1);
                    var sigma = Math.Max(_rms[cy * _width + cx], 1e-12);
                    radius += LumPsf(f.Width, f.Beta).RadiusAtLevel(f.Amplitude, Math.Max(f.Plateau - f.Sky, sigma));
                }
                var star = sources[k];
                grid.ForEachWithin(f.X, f.Y, radius, j =>
                {
                    if (j != k && fits[j].Outcome != StarFitOutcome.Subtracted && sources[j].Significance * 10f <= star.Significance)
                    {
                        var merged = fits[j];
                        merged.Outcome = StarFitOutcome.Merged;
                        fits[j] = merged;
                    }
                });
            }
        }

        private int CountPlateau(PointSource s, double plateau, double sky)
        {
            var near = plateau - 0.02 * (plateau - sky);
            var count = 0;
            var r = MaxFitRadius / 2;
            for (var y = Math.Max(0, s.PeakY - r); y <= Math.Min(_height - 1, s.PeakY + r); y++)
            {
                for (var x = Math.Max(0, s.PeakX - r); x <= Math.Min(_width - 1, s.PeakX + r); x++)
                {
                    if (_lum[y * _width + x] >= near)
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        // Adds (sign +1) or subtracts (sign -1) a star's model from every channel, and keeps the luminance the channels' mean.
        private void AddModel(in Fit fit, int sign)
        {
            var r = fit.SubtractRadius;
            var cx = (int)Math.Round(fit.X);
            var cy = (int)Math.Round(fit.Y);
            var y0 = Math.Max(0, cy - r);
            var y1 = Math.Min(_height - 1, cy + r);
            var x0 = Math.Max(0, cx - r);
            var x1 = Math.Min(_width - 1, cx + r);
            var r2 = (r + 0.5) * (r + 0.5);
            for (var c = 0; c < _channels; c++)
            {
                var a = fit.ChannelAmplitudes[c];
                if (a == 0)
                {
                    continue;
                }
                var psf = ChannelPsf(c, fit.Width, fit.Beta);
                var plane = _work[c];
                for (var y = y0; y <= y1; y++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        var dx = x - cx;
                        var dy = y - cy;
                        if (dx * dx + dy * dy > r2 || IsAbsent(x, y))
                        {
                            continue;
                        }
                        plane[y * _width + x] += (float)(sign * a * ModelChannel(c, psf, x, y, fit.X, fit.Y));
                    }
                }
            }
            if (_channels > 1)
            {
                for (var y = y0; y <= y1; y++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        var i = y * _width + x;
                        _lum[i] = (_work[0][i] + _work[1][i] + _work[2][i]) / 3f;
                    }
                }
            }
        }

        private bool IsAbsent(int x, int y) => _absent is { } a && a[y, x];

        private void RefreshLuminance()
        {
            if (_channels == 1)
            {
                return;
            }
            for (var i = 0; i < _lum.Length; i++)
            {
                _lum[i] = (_work[0][i] + _work[1][i] + _work[2][i]) / 3f;
            }
        }

        private float[][] ChannelRms()
        {
            var rms = new float[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                var map = BackgroundMap.Estimate(_work[c], _width, _height, _absent, new BackgroundMapOptions(BlockSize: PointSourceFinder.SkyBlockFor(lumPsf.Fwhm)));
                rms[c] = new float[_width * _height];
                map.FillRms(rms[c]);
            }
            return rms;
        }

        // The goal's guarantee: no star leaves a hole in any channel. After the fill, every subtracted star's core is read
        // per channel against the plate's own sky in an annulus beyond it; where it sits more than HoleSigma below, the
        // fill disc grows ring by ring until the plate is back at its sky, and is filled again from beyond that. The first
        // fill alone could not promise it: a pixel the mask missed at a star's centre, over-subtracted, was a known pixel to
        // the fill and dragged the whole disc down with it.
        private void SweepHoles(List<Fit> fits, BitMatrix inpainted)
        {
            for (var round = 0; round < HoleSweepRounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                var rms = ChannelRms();
                var discs = new System.Collections.Concurrent.ConcurrentBag<(int X, int Y, double R)>();
                ParallelFor.Run(fits.Count, k =>
                {
                    var f = fits[k];
                    if (f.Outcome != StarFitOutcome.Subtracted)
                    {
                        return;
                    }
                    var grow = 0.0;
                    for (var c = 0; c < _channels; c++)
                    {
                        if (HoleRadius(f, c, rms[c]) is { } r && r > grow)
                        {
                            grow = r;
                        }
                    }
                    if (grow > 0)
                    {
                        discs.Add(((int)Math.Round(f.X), (int)Math.Round(f.Y), grow));
                    }
                });
                if (discs.IsEmpty)
                {
                    return;
                }
                var added = new BitMatrix(_height, _width);
                foreach (var (x, y, r) in discs)
                {
                    StampDisc(added, x, y, r + 1.0);
                }
                for (var y = 0; y < _height; y++)
                {
                    for (var x = 0; x < _width; x++)
                    {
                        if (added[y, x])
                        {
                            inpainted[y, x] = true;
                        }
                    }
                }
                HoleFill.Fill(_work, _width, _height, added, _absent, lumPsf.Fwhm, options.Seed + round + 1, ct);
                RefreshLuminance();
                logger?.LogDebug("ClassicalStarRemover: hole sweep {Round}: {Holes} cores below their sky, refilled.", round + 1, discs.Count);
            }
        }

        // When a subtracted star's core in one channel sits more than HoleSigma (sigma over root n) below that channel's
        // median in an annulus beyond the star: the radius out to which the plate is still below its sky (rings averaging
        // under -0.5 sigma). Null when there is no hole.
        private double? HoleRadius(in Fit f, int channel, float[] rms)
        {
            var cx = (int)Math.Clamp(Math.Round(f.X), 0, _width - 1);
            var cy = (int)Math.Clamp(Math.Round(f.Y), 0, _height - 1);
            var sigma = rms[cy * _width + cx];
            if (!(sigma > 0))
            {
                return null;
            }
            var psf = ChannelPsf(channel, f.Width, f.Beta);
            var core = Math.Max(1.5, psf.Fwhm);
            if (f.Saturated)
            {
                core = Math.Max(core, LumPsf(f.Width, f.Beta).RadiusAtLevel(f.Amplitude, Math.Max(f.Plateau - f.Sky, _rms[cy * _width + cx])));
            }
            var amplitude = Math.Abs(f.ChannelAmplitudes[channel]);
            var annulus = Math.Clamp(psf.RadiusAtLevel(amplitude, sigma) + 2.0, core + 2.0, 40.0);
            var plane = _work[channel];
            var sky = AnnulusMedianPlane(plane, f.X, f.Y, annulus, annulus + 6.0);
            if (!double.IsFinite(sky))
            {
                return null;
            }
            if (RingMean(plane, f.X, f.Y, 0.0, core, sky, sigma, out var n) * Math.Sqrt(n) >= -HoleSigma || n == 0)
            {
                return null;
            }
            var radius = core;
            for (var ring = Math.Ceiling(core); ring < annulus; ring += 1.0)
            {
                radius = ring + 1.0;
                if (RingMean(plane, f.X, f.Y, ring, ring + 1.0, sky, sigma, out var m) >= -0.5 || m == 0)
                {
                    radius = ring;
                    break;
                }
            }
            return radius;
        }

        // The mean of (plane - sky) / sigma over pixels inner <= d < outer from the centre (inner 0 includes the centre).
        private double RingMean(float[] plane, double cx, double cy, double inner, double outer, double sky, double sigma, out int count)
        {
            double sum = 0;
            count = 0;
            var r = (int)Math.Ceiling(outer);
            var x0 = (int)Math.Round(cx);
            var y0 = (int)Math.Round(cy);
            for (var y = Math.Max(0, y0 - r); y <= Math.Min(_height - 1, y0 + r); y++)
            {
                for (var x = Math.Max(0, x0 - r); x <= Math.Min(_width - 1, x0 + r); x++)
                {
                    var d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    if (d2 >= inner * inner && d2 < outer * outer && !IsAbsent(x, y) && float.IsFinite(plane[y * _width + x]))
                    {
                        sum += (plane[y * _width + x] - sky) / sigma;
                        count++;
                    }
                }
            }
            return count > 0 ? sum / count : 0.0;
        }

        // The worst channel's core, filled or not, in sigma over root n against its own annulus sky.
        private double ChannelHoleDepth(in Fit f, double holeRadius)
        {
            var cx = (int)Math.Clamp(Math.Round(f.X), 0, _width - 1);
            var cy = (int)Math.Clamp(Math.Round(f.Y), 0, _height - 1);
            var worst = double.PositiveInfinity;
            for (var c = 0; c < _channels; c++)
            {
                var sigma = _channelRms[c][cy * _width + cx];
                if (!(sigma > 0))
                {
                    continue;
                }
                var psf = ChannelPsf(c, f.Width, f.Beta);
                var annulus = Math.Clamp(psf.RadiusAtLevel(Math.Abs(f.ChannelAmplitudes[c]), sigma) + 2.0, holeRadius + 2.0, 40.0);
                var sky = AnnulusMedianPlane(_work[c], f.X, f.Y, annulus, annulus + 6.0);
                if (!double.IsFinite(sky))
                {
                    continue;
                }
                var mean = RingMean(_work[c], f.X, f.Y, 0.0, holeRadius, sky, sigma, out var n);
                if (n > 0)
                {
                    worst = Math.Min(worst, mean * Math.Sqrt(n));
                }
            }
            return double.IsFinite(worst) ? worst : double.NaN;
        }

        /// <summary>A square window of a plane about a star: its pixels, their weights and their coordinates.</summary>
        private sealed record Window(int CentreX, int CentreY, int Radius, int[] Xs, int[] Ys, double[] Values, double[] Weights);

        private Window? CutWindow(PointSource s, double radius, double saturationCut, double? atX = null, double? atY = null, float[]? plane = null)
        {
            plane ??= _lum;
            var r = (int)Math.Clamp(Math.Ceiling(radius) + 2, Math.Ceiling(2.5 * lumPsf.Fwhm), MaxFitRadius);
            var cx = (int)Math.Round(atX ?? s.X);
            var cy = (int)Math.Round(atY ?? s.Y);
            var xs = new List<int>();
            var ys = new List<int>();
            var vs = new List<double>();
            var ws = new List<double>();
            var weighted = 0;
            for (var y = Math.Max(0, cy - r); y <= Math.Min(_height - 1, cy + r); y++)
            {
                for (var x = Math.Max(0, cx - r); x <= Math.Min(_width - 1, cx + r); x++)
                {
                    var i = y * _width + x;
                    var v = plane[i];
                    // Saturation is judged on the luminance even when a channel is being solved.
                    var usable = float.IsFinite(v) && !IsAbsent(x, y) && _lum[i] < saturationCut;
                    xs.Add(x);
                    ys.Add(y);
                    vs.Add(usable ? v : 0.0);
                    ws.Add(usable ? 1.0 : 0.0);
                    weighted += usable ? 1 : 0;
                }
            }
            return weighted >= 12 ? new Window(cx, cy, r, xs.ToArray(), ys.ToArray(), vs.ToArray(), ws.ToArray()) : null;
        }

        /// <summary>Point sources bucketed by position, for "is anything within r of here".</summary>
        private sealed class PointGrid
        {
            private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
            private readonly IReadOnlyList<PointSource> _points;
            private readonly double _cell;

            public PointGrid(IReadOnlyList<PointSource> points, double cell)
            {
                _points = points;
                _cell = Math.Max(1.0, cell);
                for (var i = 0; i < points.Count; i++)
                {
                    var key = Key((int)Math.Floor(points[i].X / _cell), (int)Math.Floor(points[i].Y / _cell));
                    if (!_cells.TryGetValue(key, out var list))
                    {
                        list = new List<int>();
                        _cells[key] = list;
                    }
                    list.Add(i);
                }
            }

            private static long Key(int cx, int cy) => ((long)cy << 32) ^ (uint)cx;

            public void ForEachWithin(double x, double y, double radius, Action<int> visit)
            {
                var reach = (int)Math.Ceiling(radius / _cell);
                var cx = (int)Math.Floor(x / _cell);
                var cy = (int)Math.Floor(y / _cell);
                var r2 = radius * radius;
                for (var gy = cy - reach; gy <= cy + reach; gy++)
                {
                    for (var gx = cx - reach; gx <= cx + reach; gx++)
                    {
                        if (!_cells.TryGetValue(Key(gx, gy), out var list))
                        {
                            continue;
                        }
                        foreach (var i in list)
                        {
                            var dx = _points[i].X - x;
                            var dy = _points[i].Y - y;
                            if (dx * dx + dy * dy <= r2)
                            {
                                visit(i);
                            }
                        }
                    }
                }
            }

            public bool AnyWithin(double x, double y, double radius, int except = -1)
            {
                var reach = (int)Math.Ceiling(radius / _cell);
                var cx = (int)Math.Floor(x / _cell);
                var cy = (int)Math.Floor(y / _cell);
                var r2 = radius * radius;
                for (var gy = cy - reach; gy <= cy + reach; gy++)
                {
                    for (var gx = cx - reach; gx <= cx + reach; gx++)
                    {
                        if (!_cells.TryGetValue(Key(gx, gy), out var list))
                        {
                            continue;
                        }
                        foreach (var i in list)
                        {
                            if (i == except)
                            {
                                continue;
                            }
                            var dx = _points[i].X - x;
                            var dy = _points[i].Y - y;
                            if (dx * dx + dy * dy < r2)
                            {
                                return true;
                            }
                        }
                    }
                }
                return false;
            }
        }

        /// <summary>A star model over a window: the PSF times an amplitude on a plane of sky.</summary>
        private readonly record struct LinearResult(
            double X, double Y, double Width, double Amplitude, double Sky, double SkyX, double SkyY,
            int WindowX, int WindowY, int Radius, double Cost, double Beta = double.NaN)
        {
            public double SkyAt(int x, int y) => Sky + SkyX * (x - WindowX) + SkyY * (y - WindowY);
        }

        // Centre and width (and with fitBeta, beta) by Levenberg-Marquardt, with amplitude and sky solved linearly inside
        // each evaluation. Width and beta are fitted as logarithms, so neither can go negative.
        private static LinearResult? FitNonlinear(Window w, double alpha, double beta, double x0, double y0, bool fitBeta, bool fitWidth = true, RadialCorrection? corr = null)
        {
            var n = w.Values.Length;
            MoffatPsf Psf(ReadOnlySpan<double> p) => new MoffatPsf(fitWidth ? alpha * Math.Exp(p[2]) : alpha, fitBeta ? Math.Exp(p[3]) : beta);
            void Residuals(ReadOnlySpan<double> p, Span<double> dst)
            {
                var r = SolveLinear(w, Psf(p), p[0], p[1], corr, dst);
                if (!double.IsFinite(r.Cost))
                {
                    dst.Fill(1e6);
                }
            }
            double[] initial = fitBeta ? new[] { x0, y0, 0.0, Math.Log(beta) } : fitWidth ? new[] { x0, y0, 0.0 } : new[] { x0, y0 };
            double[] step = fitBeta ? new[] { 0.02, 0.02, 0.01, 0.02 } : fitWidth ? new[] { 0.02, 0.02, 0.01 } : new[] { 0.02, 0.02 };
            var fitted = LevenbergMarquardt.Fit(initial, n, Residuals, step, maxIterations: 40, relativeTolerance: 1e-7);
            var p = fitted.Parameters;
            if (!p.All(double.IsFinite))
            {
                return null;
            }
            var psf = Psf(p);
            var final = SolveLinear(w, psf, p[0], p[1], corr);
            return final with { Width = fitWidth ? Math.Exp(p[2]) : 1.0, Beta = psf.Beta };
        }

        // Below the non-linear threshold: the found centre, and the best of a coarse grid of widths. A wide width wins
        // only when it is decisively better, so noise cannot call a faint star a knot.
        private static LinearResult? FitOnGrid(Window w, double alpha, double beta, double x0, double y0, out double gain, RadialCorrection? corr = null)
        {
            LinearResult? best = null;
            LinearResult? typical = null;
            foreach (var scale in WidthGrid)
            {
                var r = SolveLinear(w, new MoffatPsf(alpha * scale, beta), x0, y0, corr) with { Width = scale };
                if (scale == 1.0)
                {
                    typical = r;
                }
                if (best is not { } b || r.Cost < b.Cost)
                {
                    best = r;
                }
            }
            gain = typical is { } t && best is { } bb ? t.Cost - bb.Cost : 0.0;
            // A width under the knot gate is taken as fitted. One at or past it calls the source structure, so it must
            // beat the best star-like width decisively (Cost is half the weighted sum of squares in the plane's units,
            // compared in the window's own noise), or noise would call a faint star a knot.
            if (best is { Width: >= 1.4 } wide)
            {
                LinearResult? starLike = null;
                foreach (var scale in WidthGrid)
                {
                    if (scale >= 1.4)
                    {
                        continue;
                    }
                    var r = SolveLinear(w, new MoffatPsf(alpha * scale, beta), x0, y0, corr) with { Width = scale };
                    if (starLike is not { } sl || r.Cost < sl.Cost)
                    {
                        starLike = r;
                    }
                }
                if (starLike is { } star)
                {
                    var noise2 = Math.Max(star.Cost * 2.0 / Math.Max(1, w.Weights.Count(static v => v > 0)), 1e-30);
                    if ((star.Cost - wide.Cost) * 2.0 / noise2 < 25.0)
                    {
                        return star;
                    }
                }
            }
            return best;
        }

        // Solves amplitude, sky level and sky slope by weighted least squares for a fixed PSF and centre; writes the
        // weighted residuals into destination when given.
        private static LinearResult SolveLinear(Window w, MoffatPsf psf, double x0, double y0, RadialCorrection? corr = null, Span<double> destination = default)
        {
            Span<double> ata = stackalloc double[16];
            Span<double> atb = stackalloc double[4];
            ata.Clear();
            atb.Clear();
            var n = w.Values.Length;
            var basis = new double[n];
            Span<double> row = stackalloc double[4];
            for (var i = 0; i < n; i++)
            {
                if (w.Weights[i] <= 0)
                {
                    continue;
                }
                var p = corr is null ? psf.PixelMean(w.Xs[i], w.Ys[i], x0, y0) : corr.Model(psf, w.Xs[i], w.Ys[i], x0, y0);
                basis[i] = p;
                row[0] = p;
                row[1] = 1.0;
                row[2] = w.Xs[i] - w.CentreX;
                row[3] = w.Ys[i] - w.CentreY;
                for (var a = 0; a < 4; a++)
                {
                    atb[a] += row[a] * w.Values[i];
                    for (var b = 0; b < 4; b++)
                    {
                        ata[a * 4 + b] += row[a] * row[b];
                    }
                }
            }
            Span<double> sol = stackalloc double[4];
            if (!Solve(ata, atb, sol, 4))
            {
                // A window too small or too flat for a slope: amplitude and level alone.
                Span<double> ata2 = stackalloc double[] { ata[0], ata[1], ata[4], ata[5] };
                Span<double> atb2 = stackalloc double[] { atb[0], atb[1] };
                Span<double> sol2 = stackalloc double[2];
                if (!Solve(ata2, atb2, sol2, 2))
                {
                    return new LinearResult(x0, y0, 1.0, double.NaN, double.NaN, 0, 0, w.CentreX, w.CentreY, w.Radius, double.NaN);
                }
                sol[0] = sol2[0];
                sol[1] = sol2[1];
                sol[2] = 0;
                sol[3] = 0;
            }
            var cost = 0.0;
            for (var i = 0; i < n; i++)
            {
                var resid = 0.0;
                if (w.Weights[i] > 0)
                {
                    var model = sol[0] * basis[i] + sol[1] + sol[2] * (w.Xs[i] - w.CentreX) + sol[3] * (w.Ys[i] - w.CentreY);
                    resid = w.Values[i] - model;
                    cost += resid * resid;
                }
                if (!destination.IsEmpty)
                {
                    destination[i] = resid;
                }
            }
            return new LinearResult(x0, y0, 1.0, sol[0], sol[1], sol[2], sol[3], w.CentreX, w.CentreY, w.Radius, 0.5 * cost);
        }

        // Gaussian elimination with partial pivoting on an m x m system; false when singular.
        private static bool Solve(Span<double> a, Span<double> b, Span<double> x, int m)
        {
            Span<double> mat = stackalloc double[m * m];
            Span<double> rhs = stackalloc double[m];
            a[..(m * m)].CopyTo(mat);
            b[..m].CopyTo(rhs);
            var scale = 0.0;
            for (var i = 0; i < m; i++)
            {
                scale = Math.Max(scale, Math.Abs(mat[i * m + i]));
            }
            for (var col = 0; col < m; col++)
            {
                var pivot = col;
                for (var r = col + 1; r < m; r++)
                {
                    if (Math.Abs(mat[r * m + col]) > Math.Abs(mat[pivot * m + col]))
                    {
                        pivot = r;
                    }
                }
                if (!(Math.Abs(mat[pivot * m + col]) > 1e-12 * Math.Max(scale, 1e-300)))
                {
                    return false;
                }
                if (pivot != col)
                {
                    for (var k = 0; k < m; k++)
                    {
                        (mat[col * m + k], mat[pivot * m + k]) = (mat[pivot * m + k], mat[col * m + k]);
                    }
                    (rhs[col], rhs[pivot]) = (rhs[pivot], rhs[col]);
                }
                for (var r = col + 1; r < m; r++)
                {
                    var f = mat[r * m + col] / mat[col * m + col];
                    for (var k = col; k < m; k++)
                    {
                        mat[r * m + k] -= f * mat[col * m + k];
                    }
                    rhs[r] -= f * rhs[col];
                }
            }
            for (var r = m - 1; r >= 0; r--)
            {
                var sum = rhs[r];
                for (var k = r + 1; k < m; k++)
                {
                    sum -= mat[r * m + k] * x[k];
                }
                x[r] = sum / mat[r * m + r];
            }
            return true;
        }

        private BitMatrix BuildSubtractedMask(List<Fit> fits)
        {
            var mask = new BitMatrix(_height, _width);
            foreach (var f in fits)
            {
                if (f.Outcome != StarFitOutcome.Subtracted)
                {
                    continue;
                }
                var cx = (int)Math.Round(f.X);
                var cy = (int)Math.Round(f.Y);
                var sigma = Math.Max(_rms[Math.Clamp(cy, 0, _height - 1) * _width + Math.Clamp(cx, 0, _width - 1)], 1e-12);
                var r = (int)Math.Ceiling(LumPsf(f.Width, f.Beta).RadiusAtLevel(f.Amplitude, 0.1 * sigma));
                StampDisc(mask, cx, cy, Math.Min(r, f.SubtractRadius));
            }
            return mask;
        }

        private void StampDisc(BitMatrix mask, int cx, int cy, double radius)
        {
            var r = (int)Math.Ceiling(radius);
            var r2 = radius * radius;
            for (var y = Math.Max(0, cy - r); y <= Math.Min(_height - 1, cy + r); y++)
            {
                for (var x = Math.Max(0, cx - r); x <= Math.Min(_width - 1, cx + r); x++)
                {
                    var dx = x - cx;
                    var dy = y - cy;
                    if (dx * dx + dy * dy <= r2 && !IsAbsent(x, y))
                    {
                        mask[y, x] = true;
                    }
                }
            }
        }

        // Per subtracted star: the smoothed residual beyond the threshold inside where its model passes one sigma, and
        // for a saturated star the core where its wing model passes the plateau; grown by a pixel.
        private BitMatrix BuildInpaintMask(List<Fit> fits)
        {
            var n = _width * _height;
            var above = new float[n];
            for (var i = 0; i < n; i++)
            {
                above[i] = _rms[i] > 0 ? (_lum[i] - _sky[i]) / _rms[i] : 0f;
            }
            var smoothed = BoxMean3(above);
            var smoothNoise = PointSourceFinder.RobustSigma(smoothed, _absent, _width);
            var threshold = options.InpaintSigma * (float.IsFinite(smoothNoise) && smoothNoise > 0 ? smoothNoise : 1f / 3f);

            var mask = new BitMatrix(_height, _width);
            foreach (var f in fits)
            {
                if (f.Outcome != StarFitOutcome.Subtracted)
                {
                    continue;
                }
                var cx = (int)Math.Round(f.X);
                var cy = (int)Math.Round(f.Y);
                var centre = Math.Clamp(cy, 0, _height - 1) * _width + Math.Clamp(cx, 0, _width - 1);
                var sigma = Math.Max(_rms[centre], 1e-12);
                var psf = LumPsf(f.Width, f.Beta);
                var reach = Math.Max(psf.RadiusAtLevel(f.Amplitude, sigma), 1.5 * psf.Fwhm);
                var r = (int)Math.Ceiling(reach);
                for (var y = Math.Max(0, cy - r); y <= Math.Min(_height - 1, cy + r); y++)
                {
                    for (var x = Math.Max(0, cx - r); x <= Math.Min(_width - 1, cx + r); x++)
                    {
                        var dx = x - cx;
                        var dy = y - cy;
                        if (dx * dx + dy * dy > reach * reach || IsAbsent(x, y))
                        {
                            continue;
                        }
                        // Against the star's own sky, not the map's: a star on a nebula is judged against the nebula
                        // under it. Both skies are smooth over three pixels, so the smoothed residual shifts by their
                        // difference.
                        var i = y * _width + x;
                        var ownSky = f.Sky + f.SkyX * (x - f.WindowX) + f.SkyY * (y - f.WindowY);
                        var ownSmoothed = _rms[i] > 0 ? smoothed[i] + (_sky[i] - ownSky) / _rms[i] : 0.0;
                        if (Math.Abs(ownSmoothed) > threshold)
                        {
                            mask[y, x] = true;
                        }
                    }
                }
                if (f.Saturated)
                {
                    var core = psf.RadiusAtLevel(f.Amplitude, Math.Max(f.Plateau - f.Sky, sigma));
                    StampDisc(mask, cx, cy, core + 2.0);
                }
                else if (CoreBelowSky(f, psf, sigma) is { } holeRadius)
                {
                    // The goal's one forbidden artefact: a core left below the plate's own sky around it. Filled from
                    // around instead of left as subtracted.
                    StampDisc(mask, cx, cy, holeRadius + 1.0);
                }
            }
            mask.DilateSquare(1);
            if (_absent is { } absent)
            {
                for (var y = 0; y < _height; y++)
                {
                    for (var x = 0; x < _width; x++)
                    {
                        if (absent[y, x])
                        {
                            mask[y, x] = false;
                        }
                    }
                }
            }
            return mask;
        }

        private float[] BoxMean3(float[] src)
        {
            var dst = new float[src.Length];
            for (var y = 0; y < _height; y++)
            {
                for (var x = 0; x < _width; x++)
                {
                    double sum = 0;
                    var count = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var yy = y + dy;
                        if (yy < 0 || yy >= _height)
                        {
                            continue;
                        }
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var xx = x + dx;
                            if (xx < 0 || xx >= _width)
                            {
                                continue;
                            }
                            sum += src[yy * _width + xx];
                            count++;
                        }
                    }
                    dst[y * _width + x] = (float)(sum / count);
                }
            }
            return dst;
        }

        // The one fill (HoleFill): push-pull, then the plate's grain.
        private ImmutableArray<float> FillHoles(BitMatrix holes)
            => HoleFill.Fill(_work, _width, _height, holes, _absent, lumPsf.Fwhm, options.Seed, ct);

        private ImmutableArray<FittedStar> Describe(List<PointSource> sources, List<Fit> fits, int secondFrom, BitMatrix inpainted)
        {
            var stars = ImmutableArray.CreateBuilder<FittedStar>(sources.Count);
            for (var k = 0; k < sources.Count; k++)
            {
                var s = sources[k];
                var f = fits[k];
                var cx = (int)Math.Round(f.X);
                var cy = (int)Math.Round(f.Y);
                var inside = cx >= 0 && cx < _width && cy >= 0 && cy < _height;
                var sigma = inside ? _rms[cy * _width + cx] : float.NaN;
                var psf = LumPsf(f.Width, f.Beta);
                var touched = false;
                var residual = float.NaN;
                var bias = float.NaN;
                var holeDepth = float.NaN;
                if (f.Outcome == StarFitOutcome.Subtracted && inside && sigma > 0)
                {
                    // The residual and bias read the subtraction where nothing was filled (one FWHM); the hole depth reads
                    // what the plate shows where the star was, filled or not, over its whole core. Both against the
                    // plate's own sky in an annulus beyond the star: the sky the star's fit found absorbs whatever halo
                    // the model lacks, and against it almost any core read as a hole (the first ten-master run).
                    var radius = Math.Max(1.0, psf.Fwhm);
                    var holeRadius = f.Saturated ? Math.Max(radius, psf.RadiusAtLevel(f.Amplitude, Math.Max(f.Plateau - f.Sky, sigma))) : radius;
                    var annulus = Math.Clamp(psf.RadiusAtLevel(f.Amplitude, sigma) + 2.0, Math.Max(holeRadius + 2.0, 3.0 * psf.Fwhm), 40.0);
                    var plateSky = AnnulusMedian(f.X, f.Y, annulus, annulus + 6.0);
                    var r = (int)Math.Ceiling(holeRadius);
                    double sum = 0, sum2 = 0, holeSum = 0;
                    int count = 0, holeCount = 0;
                    for (var y = Math.Max(0, cy - r); y <= Math.Min(_height - 1, cy + r); y++)
                    {
                        for (var x = Math.Max(0, cx - r); x <= Math.Min(_width - 1, cx + r); x++)
                        {
                            var dx = x - f.X;
                            var dy = y - f.Y;
                            var d2 = dx * dx + dy * dy;
                            if (d2 > holeRadius * holeRadius || IsAbsent(x, y))
                            {
                                continue;
                            }
                            var v = (_lum[y * _width + x] - plateSky) / sigma;
                            holeSum += v;
                            holeCount++;
                            if (d2 > radius * radius)
                            {
                                continue;
                            }
                            if (inpainted[y, x])
                            {
                                touched = true;
                                continue;
                            }
                            sum += v;
                            sum2 += v * v;
                            count++;
                        }
                    }
                    if (!touched && count > 0)
                    {
                        residual = (float)Math.Sqrt(sum2 / count);
                        bias = (float)(sum / Math.Sqrt(count));
                    }
                    if (holeCount > 0)
                    {
                        holeDepth = (float)ChannelHoleDepth(f, holeRadius);
                    }
                }
                stars.Add(new FittedStar(
                    (float)f.X, (float)f.Y, s.Significance, (float)f.Amplitude, (float)FwhmRatio(f.Width, f.Beta), (float)f.Sky, sigma,
                    f.Outcome, f.Saturated, touched || (f.Outcome == StarFitOutcome.Subtracted && inside && inpainted[cy, cx]),
                    residual, bias, k >= secondFrom, holeDepth));
            }
            return stars.MoveToImmutable();
        }

        // The core radius when a subtracted star's core, on the working plate, sits more than HoleSigma (in sigma over
        // root n) below the plate's median in an annulus beyond the star; null when it does not.
        private double? CoreBelowSky(in Fit f, MoffatPsf psf, double sigma)
        {
            var radius = Math.Max(1.5, psf.Fwhm);
            var annulus = Math.Clamp(psf.RadiusAtLevel(f.Amplitude, sigma) + 2.0, radius + 2.0, 40.0);
            var sky = AnnulusMedian(f.X, f.Y, annulus, annulus + 6.0);
            if (!double.IsFinite(sky))
            {
                return null;
            }
            double sum = 0;
            var n = 0;
            var r = (int)Math.Ceiling(radius);
            var cx = (int)Math.Round(f.X);
            var cy = (int)Math.Round(f.Y);
            for (var y = Math.Max(0, cy - r); y <= Math.Min(_height - 1, cy + r); y++)
            {
                for (var x = Math.Max(0, cx - r); x <= Math.Min(_width - 1, cx + r); x++)
                {
                    if ((x - f.X) * (x - f.X) + (y - f.Y) * (y - f.Y) <= radius * radius && !IsAbsent(x, y))
                    {
                        sum += (_lum[y * _width + x] - sky) / sigma;
                        n++;
                    }
                }
            }
            return n > 0 && sum / Math.Sqrt(n) < -HoleSigma ? radius : null;
        }

        // The plate's median over an annulus, absent pixels left out; NaN where nothing is present.
        private double AnnulusMedian(double cx, double cy, double inner, double outer) => AnnulusMedianPlane(_lum, cx, cy, inner, outer);

        private double AnnulusMedianPlane(float[] plane, double cx, double cy, double inner, double outer)
        {
            var values = new List<float>();
            var r = (int)Math.Ceiling(outer);
            var x0 = (int)Math.Round(cx);
            var y0 = (int)Math.Round(cy);
            for (var y = Math.Max(0, y0 - r); y <= Math.Min(_height - 1, y0 + r); y++)
            {
                for (var x = Math.Max(0, x0 - r); x <= Math.Min(_width - 1, x0 + r); x++)
                {
                    var d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    if (d2 >= inner * inner && d2 <= outer * outer && !IsAbsent(x, y) && float.IsFinite(plane[y * _width + x]))
                    {
                        values.Add(plane[y * _width + x]);
                    }
                }
            }
            if (values.Count == 0)
            {
                return double.NaN;
            }
            values.Sort();
            return values[values.Count / 2];
        }

        // The hole test's false-alarm rate on this plate: the same core aperture and annulus at random places no star
        // touched, the fraction reading below -HoleSigma. A master's noise is correlated, so sigma over root n
        // understates a mean's scatter; holes count only as an excess over this.
        private float HoleNullRate(BitMatrix subtracted, BitMatrix inpainted)
        {
            var rng = new Random(options.Seed);
            var radius = Math.Max(1.0, LumPsf(1.0).Fwhm);
            var annulus = 3.0 * LumPsf(1.0).Fwhm;
            var margin = (int)Math.Ceiling(annulus + 7.0);
            if (_width <= 2 * margin || _height <= 2 * margin)
            {
                return float.NaN;
            }
            int tried = 0, below = 0;
            for (var attempt = 0; attempt < 20000 && tried < 2000; attempt++)
            {
                var cx = rng.Next(margin, _width - margin);
                var cy = rng.Next(margin, _height - margin);
                if (subtracted[cy, cx] || inpainted[cy, cx] || IsAbsent(cx, cy))
                {
                    continue;
                }
                var worst = double.PositiveInfinity;
                for (var c = 0; c < _channels && double.IsFinite(worst); c++)
                {
                    var sky = AnnulusMedianPlane(_work[c], cx, cy, annulus, annulus + 6.0);
                    var sigma = _channelRms[c][cy * _width + cx];
                    if (!double.IsFinite(sky) || !(sigma > 0))
                    {
                        worst = double.NaN;
                        break;
                    }
                    var mean = RingMean(_work[c], cx, cy, 0.0, radius, sky, sigma, out var n);
                    worst = n > 0 ? Math.Min(worst, mean * Math.Sqrt(n)) : double.NaN;
                }
                if (!double.IsFinite(worst))
                {
                    continue;
                }
                tried++;
                if (worst < -HoleSigma)
                {
                    below++;
                }
            }
            return tried > 0 ? (float)below / tried : float.NaN;
        }

        private StarlessPlateStatistics Measure(ImmutableArray<FittedStar> stars, BitMatrix inpainted, BitMatrix subtracted, ImmutableArray<float> correlation, Stopwatch sw)
        {
            var (leftovers, _) = PointSourceFinder.Find(_lum, _width, _height, _absent, lumPsf.Fwhm, options.LeftoverSigma);
            var bands = ImmutableArray.CreateBuilder<StarlessBand>(BandEdges.Length - 1);
            for (var b = 0; b < BandEdges.Length - 1; b++)
            {
                var lo = BandEdges[b];
                var hi = BandEdges[b + 1];
                var inBand = stars.Where(s => !s.SecondPass && s.Outcome != StarFitOutcome.Merged && s.Significance >= lo && s.Significance < hi).ToArray();
                var clean = inBand.Where(static s => s.Outcome == StarFitOutcome.Subtracted && float.IsFinite(s.CoreResidual)).ToArray();
                bands.Add(new StarlessBand(
                    lo, hi, inBand.Length,
                    inBand.Count(static s => s.Outcome == StarFitOutcome.Subtracted),
                    inBand.Count(static s => s.Outcome is StarFitOutcome.Knot or StarFitOutcome.TooNarrow),
                    inBand.Count(static s => s.Outcome == StarFitOutcome.Subtracted && s.Inpainted),
                    inBand.Count(static s => s.HoleDepth < -HoleSigma),
                    leftovers.Count(l => l.Significance >= lo && l.Significance < hi),
                    leftovers.Count(l => l.Significance >= lo && l.Significance < hi && !subtracted[l.PeakY, l.PeakX]),
                    Median(clean.Select(static s => s.CoreResidual)),
                    Median(clean.Select(static s => s.CoreBias))));
            }

            var present = _width * _height - (_absent is { } a ? a.PopCount() : 0);
            var halfDiagonal = 0.5 * Math.Sqrt((double)_width * _width + (double)_height * _height);
            float Faint(Func<double, bool> where) => Median(stars
                .Where(s => s.Outcome == StarFitOutcome.Subtracted && float.IsFinite(s.CoreResidual) && s.Significance is >= 5f and < 20f)
                .Where(s => where(Math.Sqrt(Math.Pow(s.X - 0.5 * (_width - 1), 2) + Math.Pow(s.Y - 0.5 * (_height - 1), 2)) / halfDiagonal))
                .Select(static s => s.CoreResidual));

            var fwhmOut = ImmutableArray.Create(fwhm);
            var betaOut = ImmutableArray.Create(beta);
            return new StarlessPlateStatistics(
                bands.MoveToImmutable(),
                present > 0 ? (float)inpainted.PopCount() / present : 0f,
                leftovers.Count(l => l.Significance < BandEdges[0]),
                Faint(static r => r < 1.0 / 3.0),
                Faint(static r => r > 2.0 / 3.0),
                fwhmOut, betaOut, (float)_fieldScale, (float)_beta, HoleNullRate(subtracted, inpainted), correlation, sw.Elapsed.TotalSeconds);
        }

        private static float Median(IEnumerable<float> values)
        {
            var arr = values.Where(float.IsFinite).OrderBy(static v => v).ToArray();
            return arr.Length == 0 ? float.NaN : arr[arr.Length / 2];
        }

        private Image BuildPlate()
        {
            var planes = Image.CreateChannelData(_channels, _height, _width);
            var min = float.PositiveInfinity;
            var max = float.NegativeInfinity;
            for (var c = 0; c < _channels; c++)
            {
                var src = _work[c];
                for (var y = 0; y < _height; y++)
                {
                    for (var x = 0; x < _width; x++)
                    {
                        var v = src[y * _width + x];
                        planes[c][y, x] = v;
                        if (float.IsFinite(v))
                        {
                            min = Math.Min(min, v);
                            max = Math.Max(max, v);
                        }
                    }
                }
            }
            if (min > max)
            {
                min = max = 0f;
            }
            return new Image(planes, BitDepth.Float32, max, min, image.Pedestal, image.ImageMeta);
        }
    }
}
