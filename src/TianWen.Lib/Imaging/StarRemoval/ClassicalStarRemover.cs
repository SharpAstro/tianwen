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
    private const float DefaultBeta = 3f;
    private const float DefaultFwhm = 2.5f;
    private static readonly double[] WidthGrid = { 0.7, 1.0, 1.4, 2.0 };
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
        var lumPsf = new MoffatPsf(MoffatPsf.AlphaFor(fwhm[channels], beta[channels]), beta[channels]);

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
        public double Plateau;
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

        // The field's PSF at a star's relative width: the luminance's, and each channel's at its measured FWHM ratio.
        private MoffatPsf LumPsf(double width) => new MoffatPsf(_lumAlpha * width, _beta);

        private MoffatPsf ChannelPsf(int channel, double width)
            => _channels == 1 ? LumPsf(width) : new MoffatPsf(_lumAlpha * width * fwhm[channel] / fwhm[_channels], _beta);

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

            (_fieldScale, _beta) = CalibrateFieldPsf(found);
            _lumAlpha = lumPsf.Alpha * _fieldScale;
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

            var inpainted = BuildInpaintMask(fitList);
            var subtracted = BuildSubtractedMask(fitList);
            var correlation = FillHoles(inpainted);
            if (_channels > 1)
            {
                for (var i = 0; i < n; i++)
                {
                    _lum[i] = (_work[0][i] + _work[1][i] + _work[2][i]) / 3f;
                }
            }

            var stars = Describe(sources, fitList, secondFrom, inpainted);
            var statistics = Measure(stars, inpainted, correlation, sw);
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
        private (double Scale, double Beta) CalibrateFieldPsf(PointSource[] found)
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
                return (1.0, lumPsf.Beta);
            }
            var scales = new double[picks.Count];
            var betas = new double[picks.Count];
            ParallelFor.Run(picks.Count, i =>
            {
                var s = picks[i];
                var window = CutWindow(s, 1.5 * lumPsf.RadiusAtLevel(s.Peak, Math.Max(_rms[s.PeakY * _width + s.PeakX], 1e-12)), saturationCut: double.PositiveInfinity);
                var fitted = window is { } w ? FitNonlinear(w, lumPsf.Alpha, lumPsf.Beta, s.X, s.Y, fitBeta: true) : null;
                (scales[i], betas[i]) = fitted is { } r ? (r.Width, r.Beta) : (double.NaN, double.NaN);
            });
            var good = Enumerable.Range(0, picks.Count)
                .Where(i => double.IsFinite(scales[i]) && scales[i] is > 0.3 and < 3.0 && betas[i] is > 1.0 and < 25.0)
                .ToArray();
            if (good.Length < 10)
            {
                return (1.0, lumPsf.Beta);
            }
            var scale = good.Select(i => scales[i]).OrderBy(static v => v).ElementAt(good.Length / 2);
            var beta = good.Select(i => betas[i]).OrderBy(static v => v).ElementAt(good.Length / 2);
            logger?.LogDebug("ClassicalStarRemover: field PSF from {Stars} stars: width scale {Scale:F3}, beta {Beta:F2} (stacked profile {Seed:F2}).",
                good.Length, scale, beta, lumPsf.Beta);
            return (scale, beta);
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

            var startX = previous?.X ?? s.X;
            var startY = previous?.Y ?? s.Y;
            var amplitude = previous?.Amplitude ?? (fit.Saturated ? 4.0 * (plateau - sky) : Math.Max(s.Peak, sigma));
            var radius = LumPsf(1.0).RadiusAtLevel(amplitude, sigma);
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
                result = fit.Saturated
                    ? FitNonlinear(window, _lumAlpha, _beta, startX, startY, fitBeta: false, fitWidth: false)
                    : s.Significance >= options.NonlinearFitSigma
                        ? FitNonlinear(window, _lumAlpha, _beta, startX, startY, fitBeta: false)
                        : FitOnGrid(window, _lumAlpha, _beta, startX, startY, out _);
                if (result is not { } r || !(r.Amplitude > 0))
                {
                    return fit;
                }
                var wanted = LumPsf(r.Width).RadiusAtLevel(r.Amplitude, sigma);
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
            if (final.Width > options.MaxWidthScale)
            {
                fit.Outcome = StarFitOutcome.Knot;
                return fit;
            }
            if (final.Width < options.MinWidthScale)
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
                    var modelled = final.Amplitude * LumPsf(final.Width).PixelMean(px, py, final.X, final.Y);
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
                    ? FitNonlinear(crowdedWindow, _lumAlpha, _beta, final.X, final.Y, fitBeta: false, fitWidth: false)
                    : SolveLinear(crowdedWindow, LumPsf(1.0), final.X, final.Y);
                if (fixedWidth is { } fw && fw.Amplitude > 0 && Math.Abs(fw.X - s.X) <= 2.0 && Math.Abs(fw.Y - s.Y) <= 2.0)
                {
                    final = fw with { Width = 1.0 };
                    fit.X = final.X;
                    fit.Y = final.Y;
                    fit.Width = 1.0;
                    fit.Amplitude = final.Amplitude;
                    fit.Sky = final.Sky;
                    fit.SkyX = final.SkyX;
                    fit.SkyY = final.SkyY;
                    fit.WindowX = final.WindowX;
                    fit.WindowY = final.WindowY;
                }
            }

            fit.SubtractRadius = (int)Math.Clamp(
                Math.Ceiling(LumPsf(final.Width).RadiusAtLevel(final.Amplitude, 0.1 * sigma)),
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
                        ? SolveLinear(cw, ChannelPsf(c, final.Width), final.X, final.Y).Amplitude
                        : 0.0;
                }
            }
            fit.Outcome = StarFitOutcome.Subtracted;
            AddModel(fit, -1);
            return fit;
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
                var psf = ChannelPsf(c, fit.Width);
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
                        plane[y * _width + x] += (float)(sign * a * psf.PixelMean(x, y, fit.X, fit.Y));
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
        private static LinearResult? FitNonlinear(Window w, double alpha, double beta, double x0, double y0, bool fitBeta, bool fitWidth = true)
        {
            var n = w.Values.Length;
            MoffatPsf Psf(ReadOnlySpan<double> p) => new MoffatPsf(fitWidth ? alpha * Math.Exp(p[2]) : alpha, fitBeta ? Math.Exp(p[3]) : beta);
            void Residuals(ReadOnlySpan<double> p, Span<double> dst)
            {
                var r = SolveLinear(w, Psf(p), p[0], p[1], dst);
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
            var final = SolveLinear(w, psf, p[0], p[1]);
            return final with { Width = fitWidth ? Math.Exp(p[2]) : 1.0, Beta = psf.Beta };
        }

        // Below the non-linear threshold: the found centre, and the best of a coarse grid of widths. A wide width wins
        // only when it is decisively better, so noise cannot call a faint star a knot.
        private static LinearResult? FitOnGrid(Window w, double alpha, double beta, double x0, double y0, out double gain)
        {
            LinearResult? best = null;
            LinearResult? typical = null;
            foreach (var scale in WidthGrid)
            {
                var r = SolveLinear(w, new MoffatPsf(alpha * scale, beta), x0, y0) with { Width = scale };
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
            // Cost is half the weighted sum of squares in the plane's units; compare in the window's own noise.
            if (best is { } chosen && typical is { } typ && chosen.Width != 1.0)
            {
                var noise2 = Math.Max(typ.Cost * 2.0 / Math.Max(1, w.Weights.Count(static v => v > 0)), 1e-30);
                if (gain * 2.0 / noise2 < 25.0)
                {
                    return typ;
                }
            }
            return best;
        }

        // Solves amplitude, sky level and sky slope by weighted least squares for a fixed PSF and centre; writes the
        // weighted residuals into destination when given.
        private static LinearResult SolveLinear(Window w, MoffatPsf psf, double x0, double y0, Span<double> destination = default)
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
                var p = psf.PixelMean(w.Xs[i], w.Ys[i], x0, y0);
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
                var r = (int)Math.Ceiling(LumPsf(f.Width).RadiusAtLevel(f.Amplitude, 0.1 * sigma));
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
                var psf = LumPsf(f.Width);
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

        // Push-pull per channel, then noise at the channel's local rms with its measured lag-1 correlation.
        private ImmutableArray<float> FillHoles(BitMatrix holes)
        {
            var correlation = new float[_channels];
            if (!holes.Any())
            {
                return correlation.ToImmutableArray();
            }
            var excluded = new BitMatrix(_height, _width);
            for (var y = 0; y < _height; y++)
            {
                for (var x = 0; x < _width; x++)
                {
                    excluded[y, x] = holes[y, x] || IsAbsent(x, y);
                }
            }
            for (var c = 0; c < _channels; c++)
            {
                ct.ThrowIfCancellationRequested();
                var plane = _work[c];
                var skyMap = BackgroundMap.Estimate(plane, _width, _height, excluded, new BackgroundMapOptions(BlockSize: PointSourceFinder.SkyBlockFor(lumPsf.Fwhm)));
                var rms = new float[plane.Length];
                skyMap.FillRms(rms);
                var rho = Math.Clamp(LagOneCorrelation(plane, rms, excluded), 0f, 0.7f);
                correlation[c] = rho;
                var a = rho > 1e-3f ? (1.0 - Math.Sqrt(1.0 - 2.0 * rho * rho)) / (2.0 * rho) : 0.0;
                var norm = 1.0 + 2.0 * a * a;
                PushPullFill.Fill(plane, _width, _height, holes, _absent);
                for (var y = 0; y < _height; y++)
                {
                    for (var x = 0; x < _width; x++)
                    {
                        if (!holes[y, x] || IsAbsent(x, y))
                        {
                            continue;
                        }
                        double g = 0;
                        for (var j = -1; j <= 1; j++)
                        {
                            var ky = j == 0 ? 1.0 : a;
                            for (var i = -1; i <= 1; i++)
                            {
                                var kx = i == 0 ? 1.0 : a;
                                if (kx * ky != 0)
                                {
                                    g += kx * ky * HashGaussian(options.Seed, c, x + i, y + j);
                                }
                            }
                        }
                        plane[y * _width + x] += (float)(rms[y * _width + x] * g / norm);
                    }
                }
            }
            return correlation.ToImmutableArray();
        }

        // 1 - var(first difference) / (2 var) over present, unexcluded sky, both robust and in the rms map's units.
        private float LagOneCorrelation(float[] plane, float[] rms, BitMatrix excluded)
        {
            var diffs = new List<float>();
            for (var y = 0; y < _height; y += 2)
            {
                for (var x = 0; x + 1 < _width; x += 2)
                {
                    if (excluded[y, x] || excluded[y, x + 1] || !(rms[y * _width + x] > 0))
                    {
                        continue;
                    }
                    diffs.Add((plane[y * _width + x + 1] - plane[y * _width + x]) / rms[y * _width + x]);
                }
            }
            var s = PointSourceFinder.RobustSigma(diffs.ToArray(), null, 1);
            return float.IsFinite(s) ? 1f - s * s / 2f : 0f;
        }

        // A standard normal from a counter-based hash (SplitMix64 then Box-Muller): the same pixel draws the same value
        // on any thread.
        private static double HashGaussian(int seed, int channel, int x, int y)
        {
            var key = unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL ^ ((ulong)(uint)channel << 58) ^ ((ulong)(uint)y << 29) ^ (uint)x);
            var a = SplitMix(ref key);
            var b = SplitMix(ref key);
            var u1 = ((a >> 11) + 0.5) / (1UL << 53);
            var u2 = (b >> 11) / (double)(1UL << 53);
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private static ulong SplitMix(ref ulong state)
        {
            var z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

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
                var psf = LumPsf(f.Width);
                var touched = false;
                var residual = float.NaN;
                var bias = float.NaN;
                if (f.Outcome == StarFitOutcome.Subtracted && inside && sigma > 0)
                {
                    var radius = Math.Max(1.0, psf.Fwhm);
                    var r = (int)Math.Ceiling(radius);
                    double sum = 0, sum2 = 0;
                    var count = 0;
                    for (var y = Math.Max(0, cy - r); y <= Math.Min(_height - 1, cy + r) && !touched; y++)
                    {
                        for (var x = Math.Max(0, cx - r); x <= Math.Min(_width - 1, cx + r); x++)
                        {
                            var dx = x - f.X;
                            var dy = y - f.Y;
                            if (dx * dx + dy * dy > radius * radius || IsAbsent(x, y))
                            {
                                continue;
                            }
                            if (inpainted[y, x])
                            {
                                touched = true;
                                break;
                            }
                            var v = (_lum[y * _width + x] - (f.Sky + f.SkyX * (x - f.WindowX) + f.SkyY * (y - f.WindowY))) / sigma;
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
                }
                stars.Add(new FittedStar(
                    (float)f.X, (float)f.Y, s.Significance, (float)f.Amplitude, (float)f.Width, (float)f.Sky, sigma,
                    f.Outcome, f.Saturated, touched || (f.Outcome == StarFitOutcome.Subtracted && inside && inpainted[cy, cx]),
                    residual, bias, k >= secondFrom));
            }
            return stars.MoveToImmutable();
        }

        private StarlessPlateStatistics Measure(ImmutableArray<FittedStar> stars, BitMatrix inpainted, ImmutableArray<float> correlation, Stopwatch sw)
        {
            var (leftovers, _) = PointSourceFinder.Find(_lum, _width, _height, _absent, lumPsf.Fwhm, options.LeftoverSigma);
            var bands = ImmutableArray.CreateBuilder<StarlessBand>(BandEdges.Length - 1);
            for (var b = 0; b < BandEdges.Length - 1; b++)
            {
                var lo = BandEdges[b];
                var hi = BandEdges[b + 1];
                var inBand = stars.Where(s => !s.SecondPass && s.Significance >= lo && s.Significance < hi).ToArray();
                var clean = inBand.Where(static s => s.Outcome == StarFitOutcome.Subtracted && float.IsFinite(s.CoreResidual)).ToArray();
                bands.Add(new StarlessBand(
                    lo, hi, inBand.Length,
                    inBand.Count(static s => s.Outcome == StarFitOutcome.Subtracted),
                    inBand.Count(static s => s.Outcome is StarFitOutcome.Knot or StarFitOutcome.TooNarrow),
                    inBand.Count(static s => s.Outcome == StarFitOutcome.Subtracted && s.Inpainted),
                    leftovers.Count(l => l.Significance >= lo && l.Significance < hi),
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
                fwhmOut, betaOut, (float)_fieldScale, (float)_beta, correlation, sw.Elapsed.TotalSeconds);
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
