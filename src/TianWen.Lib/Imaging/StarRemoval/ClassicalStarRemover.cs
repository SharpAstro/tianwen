using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Geometry;
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
/// <para><b>Parallel by tiles that cannot touch, by construction (#2378).</b> Stars are grouped into tiles of twice
/// <see cref="MaxSubtractRadius"/> by where they were found, and the tiles run in four phases of a checkerboard. A
/// tile's work reads and writes the working planes on its GROUND alone (<see cref="TileGround"/>): the tile grown by
/// <see cref="MaxSubtractRadius"/> on each side, cut to the frame. Two tiles of one phase are a whole tile apart, so
/// their grounds share no pixel, and no tile of a phase reads what another writes, on any number of threads and for
/// every kind of star. The order inside a tile is the significance order.</para>
/// <para><b>Why the ground is a clip and not a promise.</b> The tile's size counts <see cref="MaxSubtractRadius"/>
/// from the STAR, but a tile's work reaches from wherever a fit is CENTRED (#2374). With the half pixel a centre is
/// rounded by: a fit's window reaches 129 px from its star (<see cref="MaxFitRadius"/> from a centre that may be
/// <see cref="MaxRecentre"/> away); the subtraction of a fit accepted on the frame 131 px (from a centre within the
/// drift allowance, 2 px); that of a star the edge or the absent canvas cut 161 px along the edge (held by
/// <see cref="HasLeftItsStar"/> alone, 32 px along it); a source fitted as two stars (<c>FitPair</c>) 131 px plus 2.5
/// of the image's FWHM; and a saturated star is worked from its plateau's centre, which the finder's peak can be a
/// plateau's radius from. So two stars of one phase, each close to a FACING tile edge, could meet by a few pixels, or
/// by tens, and where they did two threads wrote one pixel or one read what the other wrote. Every routine that reads
/// or writes the planes on a tile's behalf (the window, both subtractions, the plateau's centre and count, the
/// saturation test, the knot test's centre pixel, a giant's profile and its halo search, the ring medians) now takes
/// the ground and loses what lies past it: a window or a subtraction that reached past its ground lacks those columns
/// or rows there. Where no work reached past its ground a plate is the same to the bit as before; the test fields'
/// plates were. What no tile writes (the sky map, its noise, the absent mask) is read freely.</para>
/// <para><b>Why a fit that ran away is still refused (#2352).</b> A fit that ends further than <see cref="MaxRecentre"/>
/// from its star is refused there and never followed (<see cref="RanAway"/>): it is not that star's fit. Before the
/// ground, one that was followed 364 px read what another tile of its phase was writing, and its row differed from
/// run to run; the ground would now hold such a window to its tile, but the fit would still be another star's.</para>
/// <para>Checked, not argued: a DEBUG build records every rectangle a routine asks for through Square or Bound, and what
/// it was cut to, with its ground (<see cref="TileAccessRecorder"/>), and a test asserts on a field of stars at facing
/// tile edges that every such request is cut to its ground. That every read and write goes through those two is the
/// code review's claim, not the test's.</para>
/// <para>Deterministic: the fill's noise comes from a counter-based hash of the seed, the channel and the pixel, and no
/// two tiles of a phase share a pixel, so the same image and seed give the same plate on any number of threads.</para>
/// </remarks>
public static class ClassicalStarRemover
{
    internal const int MaxSubtractRadius = 128;

    // The factor an unsaturated amplitude may stand over the brightest covered sample less the sky. Amplitude is the
    // continuous Moffat peak and the sample is a pixel MEAN, so a sharp star centred on a pixel corner has a brightest
    // pixel of only 60 percent of its peak (1.7 times); a diverged fit is 1e12 off, so 4 refuses it and never a star.
    internal const double OvershootMargin = 4.0;

    // Whether an unsaturated fit's amplitude in any channel is not a number or above OvershootMargin times that channel's brightest
    // covered sample. Each channel against its own ceiling, with no sky taken off: a luminance sky taken off a colour channel's
    // ceiling left a channel whose brightest sample sat under the other channels' sky a threshold of 0, and refused nearly every star
    // of a field with unequal channel skies (#1873's review: 286 of 297 NoFit). The margin dwarfs any sky against a divergence of 1e12.
    internal static bool AmplitudeOvershoots(ReadOnlySpan<double> amplitudes, ReadOnlySpan<float> fullScale)
    {
        for (var c = 0; c < amplitudes.Length; c++)
        {
            if (!double.IsFinite(amplitudes[c]) || amplitudes[c] > OvershootMargin * fullScale[c])
            {
                return true;
            }
        }
        return false;
    }

    private const int MaxFitRadius = 96;
    internal const int TileSize = 2 * MaxSubtractRadius;

    /// <summary>
    /// How far from its star, on either axis, a fit may end and still be that star's. A fit's window reaches
    /// <see cref="MaxFitRadius"/> from its centre and a tile's work is held to <see cref="MaxSubtractRadius"/> beyond its
    /// tile (<see cref="TileGround"/>), so a window cut around a centre further off than their difference would reach
    /// past the ground and lose what lies there.
    /// </summary>
    internal const int MaxRecentre = MaxSubtractRadius - MaxFitRadius;

    /// <summary>
    /// The ground of the tile at column <paramref name="tx"/> and row <paramref name="ty"/> of a frame of
    /// <paramref name="width"/> by <paramref name="height"/>: the tile of <see cref="TileSize"/> grown by
    /// <see cref="MaxSubtractRadius"/> on each side and cut to the frame, which is all its work may read or write
    /// (#2378). The grounds of two tiles of one phase, two tiles apart, meet edge to edge and share no pixel.
    /// </summary>
    internal static PixelRect TileGround(int tx, int ty, int width, int height)
        => PixelRect.Intersect(
            new PixelRect(0, 0, width, height),
            PixelRect.FromLTRB(tx * TileSize - MaxSubtractRadius, ty * TileSize - MaxSubtractRadius,
                (tx + 1) * TileSize + MaxSubtractRadius, (ty + 1) * TileSize + MaxSubtractRadius));

    /// <summary>
    /// Whether a fit centred at (<paramref name="x"/>, <paramref name="y"/>) has left the star it was started on at
    /// (<paramref name="starX"/>, <paramref name="starY"/>): further on either axis than <see cref="MaxRecentre"/> and
    /// than the star's own <paramref name="drift"/> allowance (a saturated star's is its plateau's radius, which can be
    /// the larger). A centre that is not a number has left it too.
    /// </summary>
    internal static bool RanAway(double x, double y, double starX, double starY, double drift)
    {
        var allowed = Math.Max(drift, MaxRecentre);
        return !(Math.Abs(x - starX) <= allowed && Math.Abs(y - starY) <= allowed);
    }

    /// <summary>
    /// Whether a fit centred at (<paramref name="x"/>, <paramref name="y"/>) has left the star it was started on, on a
    /// frame of <paramref name="width"/> by <paramref name="height"/> of which <paramref name="covered"/> names the
    /// covered pixels. On the covered frame that is <see cref="RanAway"/> of the centre. Off it, an UNSATURATED star's
    /// centre is judged where it stands on the cover (<see cref="NearestCovered"/>, #2374): a star the edge cut has its
    /// centre straight out from its peak, and only a fit that went ALONG the edge has left it.
    /// </summary>
    /// <remarks>
    /// <para>A SATURATED star is held on the centre itself, off the frame as on it. Its plateau is in view, so its
    /// centre is by its peak and not at a depth; with half a plane of wing to go by, a fit trades depth against
    /// amplitude, and the amplitude ceiling that would catch that is for unsaturated stars alone. Past the bound it
    /// gets its profile, as it does on the frame.</para>
    /// <para>For an unsaturated star the rule sets NO DEPTH. A centre past the frame's edge is brought straight into
    /// the frame first and the cover is looked for within <see cref="MaxSubtractRadius"/> of THAT point, so beyond an
    /// edge with no absent canvas a centre stands on the edge however far out it is. It stands nowhere, and has left,
    /// only where more absent canvas than that lies between it and the cover. What holds the depth comes after this
    /// rule, and is reach: a window is cut <see cref="MaxFitRadius"/> at most about a centre, so more than that outside
    /// the cover a second fit that is wanted has no pixel to read and the source ends with no fit, as it did before
    /// #2374, and a channel's fit has none and gives no amplitude; a subtraction more than
    /// <see cref="MaxSubtractRadius"/> outside writes nothing.</para>
    /// <para>A static of its own so that a test can place a centre: on a field, only a star straight out from the edge
    /// is easily made, and that reaches one answer of this rule.</para>
    /// </remarks>
    internal static bool HasLeftItsStar(double x, double y, double starX, double starY, double drift, bool saturated,
        int width, int height, Func<int, int, bool> covered)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return true;
        }
        var px = Math.Round(x);
        var py = Math.Round(y);
        if (saturated || (px >= 0 && px < width && py >= 0 && py < height && covered((int)px, (int)py)))
        {
            return RanAway(x, y, starX, starY, drift);
        }
        return NearestCovered(x, y, width, height, covered, MaxSubtractRadius) is not { } stands
            || RanAway(stands.X, stands.Y, starX, starY, drift);
    }

    /// <summary>
    /// The covered pixel nearest to (<paramref name="x"/>, <paramref name="y"/>): the point itself, to the pixel, when it
    /// is on the frame and covered; else the nearest covered pixel of the frame within <paramref name="reach"/> pixels
    /// of the point BROUGHT INTO THE FRAME (a point past the frame's edge is taken straight in first, however far out
    /// it is); and null when there is none that near, or the point is not a number.
    /// </summary>
    /// <remarks>
    /// What <see cref="RanAway"/> is asked about a centre OFF the covered frame (<see cref="HasLeftItsStar"/>, #2374). A
    /// star the edge or the absent canvas cut has its true centre straight out from where the finder took its peak: on a
    /// test field a fit finds one 40 px outside the cover within 4 px. Such a fit has not left its star, and what it
    /// reads and writes on the frame lies beside the star however deep the centre is, and is less the deeper it is
    /// (<see cref="HasLeftItsStar"/> says where it ends). A fit that ran ALONG the edge has left, and the
    /// covered pixel nearest its centre is as far from the star as it ran. Asked about the centre itself, the rule
    /// refused the first past 32 px of depth together with the second.
    /// <para>
    /// "Nearest" is taken ring by ring of whole pixels about the point brought into the frame, and within the first
    /// ring that holds a covered pixel the one nearest the point as given. That is near enough for a bound of 32 px.
    /// </para>
    /// </remarks>
    internal static (int X, int Y)? NearestCovered(double x, double y, int width, int height, Func<int, int, bool> covered, int reach)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return null;
        }
        var cx = (int)Math.Clamp(Math.Round(x), 0, width - 1);
        var cy = (int)Math.Clamp(Math.Round(y), 0, height - 1);
        if (covered(cx, cy))
        {
            return (cx, cy);
        }
        for (var d = 1; d <= reach; d++)
        {
            (int X, int Y)? best = null;
            var bestDistance = double.PositiveInfinity;
            for (var py = Math.Max(0, cy - d); py <= Math.Min(height - 1, cy + d); py++)
            {
                // The ring's top and bottom rows are whole rows; every row between holds its two ends only.
                var step = py == cy - d || py == cy + d ? 1 : 2 * d;
                for (var px = cx - d; px <= cx + d; px += step)
                {
                    if (px < 0 || px >= width || !covered(px, py))
                    {
                        continue;
                    }
                    var distance = (px - x) * (px - x) + (py - y) * (py - y);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = (px, py);
                    }
                }
            }
            if (best is not null)
            {
                return best;
            }
        }
        return null;
    }
    private const int RefinementPasses = 2;
    private const int HoleSweepRounds = 3;
    private const float RemnantSigma = 50f;
    private const float RemnantCleanSigma = 10f;
    private const double GiantPlateauRadius = 3.0;
    private const double HaloSkySigma = 5.0;
    private const double FullScaleFraction = 0.95;
    private const double ProfileFlagSigma = 10.0;
    private const int MaxResidualPasses = 4;
    private const double PixelFlagSigma = 5.0;
    private const int FlatRings = 8;
    private const double ProfileClampSigma = 2.0;
    private const double FlatDrop = 0.25;
    private const double FindPassStop = 0.1;
    private const double HaloSearchPlateauRadius = 5.0;
    private const int HaloSearchOffset = 12;
    private const double CompressedCoreFraction = 0.8;
    internal const float HoleSigma = 3f;

    // The hole sweep's second trigger, a dug core (#2449). The hole test reads a core's MEAN over a disc of max(1.5, FWHM)
    // px, so one pixel the subtraction dug never trips it: at an FWHM of 3.8 px the disc holds about 45 pixels, and one
    // pixel 5 sigma under the sky moves the mean's z by under 1. Measured on the store's Tarantula master (ASI585MC,
    // FWHM 3.8 px): a core pixel (within 1.4 px) 5 sigma under the sky round it on 13.8 percent of the 10 to 30 sigma
    // and 33.1 percent of the 30 to 100 sigma subtracted stars whose core was not inpainted, against 3.5 percent at
    // random star-free spots. The fit over-takes the peak pixel of an undersampled star and leaves one dark pixel, the
    // black pit a 1:1 crop shows, which a model trained on the plate learns to dig at every faint star. So a core pixel
    // within DugCoreSearch px of the fit's centre that sits more than DugPixelSigma of the pixel-to-pixel noise (the
    // annulus's neighbour differences; DugCore says why not the sweep's rms) under the annulus sky, where the subtraction
    // took more than DugPixelSigma of that noise, has the disc of DugCoreRadius px about the centre refilled, as a hole
    // is. The disc holds every pixel within DugCoreSearch of a centre a rounding away from it.
    internal const double DugPixelSigma = 4.0;
    internal const double DugCoreSearch = 1.5;
    internal const double DugCoreRadius = 2.0;
    // The fewest neighbour pairs an annulus may read its pixel-to-pixel noise from (DugCore); an annulus holds some 250.
    private const int MinNoisePairs = 32;

    private const double SkyAnnulusMax = 40.0;
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
    public static Task<StarlessPlate> BuildAsync(
        Image image, StarlessPlateOptions? options = null, ILogger? logger = null, CancellationToken cancellationToken = default)
        => BuildAsync(image, options, logger, tileThreads: -1, recorder: null, fillDugCores: true, cancellationToken);

    /// <summary>
    /// <see cref="BuildAsync(Image, StarlessPlateOptions, ILogger, CancellationToken)"/> with the tiles of a phase fitted
    /// on at most <paramref name="tileThreads"/> threads at once (-1: as many as the pool runs), and every rectangle the
    /// build reads or writes recorded into <paramref name="recorder"/> in a DEBUG build: what the tests of #2378 drive.
    /// <paramref name="fillDugCores"/> false leaves out the hole sweep's dug-core trigger (#2449), so a test can show the
    /// pixel it fills and what it leaves alone; every plate is built with it.
    /// </summary>
    internal static Task<StarlessPlate> BuildAsync(
        Image image, StarlessPlateOptions? options, ILogger? logger, int tileThreads, TileAccessRecorder? recorder,
        bool fillDugCores, CancellationToken cancellationToken)
        => BuildAsync(image, options, logger, tileThreads, recorder, fillDugCores, retestDugCores: false, cancellationToken);

    /// <summary>
    /// The same, with <paramref name="retestDugCores"/> true testing a star for a dug pixel in every round of the hole
    /// sweep, also once it was refilled for one, so a test can show the redraws the sweep's guard prevents; no plate is
    /// built that way.
    /// </summary>
    internal static async Task<StarlessPlate> BuildAsync(
        Image image, StarlessPlateOptions? options, ILogger? logger, int tileThreads, TileAccessRecorder? recorder,
        bool fillDugCores, bool retestDugCores, CancellationToken cancellationToken)
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
        var builder = await CreateBuilderAsync(image, options, logger, cancellationToken);
        builder.TileThreads = tileThreads;
        builder.Recorder = recorder;
        builder.FillDugCores = fillDugCores;
        builder.RetestDugCores = retestDugCores;
        return await Task.Run(() => builder.Run(sw), cancellationToken);
    }

    /// <summary>
    /// The field profile <see cref="BuildAsync(Image, StarlessPlateOptions, ILogger, CancellationToken)"/> would subtract
    /// every star of <paramref name="image"/> with, measured as it measures it (the same calibration on the same found
    /// stars, so the same numbers) without fitting a star: what a plate store built before profiles were kept is given
    /// one with.
    /// </summary>
    public static async Task<StarlessFieldProfile> MeasureFieldProfileAsync(
        Image image, StarlessPlateOptions? options = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var builder = await CreateBuilderAsync(image, options ?? StarlessPlateOptions.Default, logger, cancellationToken);
        return await Task.Run(builder.MeasureFieldProfile, cancellationToken);
    }

    private static async Task<Builder> CreateBuilderAsync(Image image, StarlessPlateOptions options, ILogger? logger, CancellationToken cancellationToken)
    {
        var channels = image.Shape.ChannelCount;
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
        return new Builder(image, options, lumPsf, fwhm, beta, logger, cancellationToken);
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
        public double Plateau, Beta, Significance, PlateauRadius;
        public float[][]? Profile, HaloProfile;
        public double HaloX, HaloY;
        // The levels a profile and its halo were measured against, and how far below them a subtraction may take a pixel.
        public double[]? ProfileFloor, HaloFloor;
        public double ProfileMargin;
        // A blend's second star (FitPair), at the primary's width and beta.
        public double CompanionX, CompanionY;
        public double[]? CompanionAmplitudes;
        public bool Saturated;
        public StarFitOutcome Outcome;
        // The ground of the tile it was fitted in (#2378): its model is taken out and put back there and nowhere else,
        // so a put-back outside the tile passes (DropDuplicates) restores exactly what the subtraction took.
        public PixelRect Ground;
    }

    private sealed class Builder(
        Image image, StarlessPlateOptions options, MoffatPsf lumPsf, float[] fwhm, float[] beta,
        ILogger? logger, CancellationToken ct)
    {
        private readonly int _channels = image.Shape.ChannelCount;
        private readonly int _width = image.Shape.Width;
        private readonly int _height = image.Shape.Height;
        private readonly BitMatrix? _absent = image.AbsentPixels();
        // The ground of the work outside the tile passes (calibration, the masks, the report): the whole frame.
        private readonly PixelRect _frame = new PixelRect(0, 0, image.Shape.Width, image.Shape.Height);
        // The tile pass and its phase that is running, for the recorder; -1 outside the passes. Written only between the
        // passes' parallel loops.
        private int _pass = -1;
        private int _phase = -1;
        private int _passes;

        /// <summary>At most this many tiles of a phase at once; -1 for as many as the pool runs.</summary>
        public int TileThreads { get; set; } = -1;

        /// <summary>Where a DEBUG build records every rectangle of the working planes it reads or writes; null for none.</summary>
        public TileAccessRecorder? Recorder { get; set; }

        /// <summary>Whether the hole sweep refills a dug core (#2449); false only in a test that shows what it does.</summary>
        public bool FillDugCores { get; set; } = true;

        /// <summary>
        /// Whether the hole sweep tests a star for a dug pixel again once it refilled it for one; true only in a test
        /// that shows the redraws the guard prevents.
        /// </summary>
        public bool RetestDugCores { get; set; }

        // The cover as HasLeftItsStar is handed it, made at its first use and kept: the rule is asked for every fit of
        // every star, on every tile's thread. Two threads that make it at once make two that answer alike, and either
        // may stand.
        private Func<int, int, bool>? _covered;
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
        // The luminance's noise for the hole tests: the sky map's spread capped by the pixels' own differences
        // (PointSourceFinder.CapByDifferenceNoise). Only a test of a core against the plate round it reads it: a dip is
        // what it asks about, which a nebula's texture never is; the flags against the smooth sky keep the map's spread,
        // which shields that texture from being taken for residual.
        private float[] _holeNoise = Array.Empty<float>();
        // The master's background texture (its sky map's spread and _holeNoise), which FittedStar.Texture reads at each
        // star and the speckle measure classes every site and null position by.
        private TextureField _texture;
        private float[] _fullScale = Array.Empty<float>();
        private float[][] _original = Array.Empty<float[]>();
        private float[][]? _ceiling;

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

        // The square of half-width r about (cx, cy) that a routine reads or writes, cut to its ground (#2378): the pixels
        // it may touch. Every read and write of the working planes made on a star's behalf goes through here or Bound.
        private PixelRect Square(in PixelRect ground, int cx, int cy, int r, bool write, [CallerMemberName] string site = "")
            => Bound(ground, PixelRect.FromLTRB(cx - r, cy - r, cx + r + 1, cy + r + 1), write, site);

        private PixelRect Bound(in PixelRect ground, PixelRect reach, bool write, [CallerMemberName] string site = "")
        {
            var touched = PixelRect.Intersect(ground, reach);
            Record(site, ground, reach, touched, write);
            return touched;
        }

        // What a DEBUG build records of it (TileAccessRecorder): the reach cut to the frame alone, which is what it asked
        // for, beside what the ground let it touch. Compiled out of Release, arguments and all.
        [Conditional("DEBUG")]
        private void Record(string site, in PixelRect ground, PixelRect reach, PixelRect touched, bool write)
            => Recorder?.Add(new TileAccess(site, _pass, _phase, ground, PixelRect.Intersect(_frame, reach), touched, write));

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
            var found = Calibrate();
            var fits = new Fit[found.Length];
            var firstCrowded = Crowding(found);
            var giants = FindGiants(found);
            RunTiled(found.Length, i => (found[i].X, found[i].Y), (i, ground) => fits[i] = InsideGiant(found[i], giants)
                ? new Fit { X = found[i].X, Y = found[i].Y, Width = 1.0, Outcome = StarFitOutcome.Merged, ChannelAmplitudes = new double[_channels], Significance = found[i].Significance, Ground = ground }
                : FitAndSubtract(found[i], null, firstCrowded[i], ground));
            ct.ThrowIfCancellationRequested();

            var sources = found.ToList();
            var fitList = fits.ToList();
            var secondFrom = sources.Count;
            // The matched filter merges a pair closer than about 1.5 FWHM into one peak; the fainter one shows on the
            // residual once the brighter is out. And in a crowded field the filtered plane's noise is the faint stars
            // themselves (eta Car's Milky Way: 132 against a pixel noise of 92, the filter buying nothing), so each
            // residual's floor is lower and shows stars the last one hid: found again until a pass adds under
            // FindPassStop of what the one before it added.
            var previousAdded = found.Length;
            for (var pass = 0; options.SecondPass && pass < MaxResidualPasses; pass++)
            {
                var (residualFound, _) = PointSourceFinder.Find(_lum, _width, _height, _absent, lumPsf.Fwhm, options.DetectionSigma);
                var known = sources.ToArray();
                var seen = new PointGrid(known, lumPsf.Fwhm);
                var fresh = residualFound.Where(r => !seen.AnyWithin(r.X, r.Y, lumPsf.Fwhm)).ToArray();
                var freshFits = new Fit[fresh.Length];
                var freshCrowded = Crowding(fresh, known);
                RunTiled(fresh.Length, i => (fresh[i].X, fresh[i].Y), (i, ground) => freshFits[i] = InsideGiant(fresh[i], giants)
                    ? new Fit { X = fresh[i].X, Y = fresh[i].Y, Width = 1.0, Outcome = StarFitOutcome.Merged, ChannelAmplitudes = new double[_channels], Significance = fresh[i].Significance, Ground = ground }
                    : FitAndSubtract(fresh[i], null, freshCrowded[i], ground));
                sources.AddRange(fresh);
                fitList.AddRange(freshFits);
                logger?.LogDebug("ClassicalStarRemover: residual pass {Pass}: {Fresh} new sources.", pass + 1, fresh.Length);
                ct.ThrowIfCancellationRequested();
                if (fresh.Length < FindPassStop * previousAdded)
                {
                    break;
                }
                previousAdded = fresh.Length;
            }
            ct.ThrowIfCancellationRequested();

            // Refinement over everything found, twice: each star back in, fitted again with every neighbour (brighter
            // or fainter, first pass or second) already out, and its crowding judged on the complete list, so the
            // brighter of a pair the filter merged learns of its companion here.
            var all = sources.ToArray();
            DropDuplicates(all, fitList);
            var crowded = Crowding(all);
            for (var pass = 0; pass < RefinementPasses; pass++)
            {
                RunTiled(all.Length, i => (all[i].X, all[i].Y), (i, ground) =>
                {
                    if (fitList[i].Outcome == StarFitOutcome.Subtracted && fitList[i].Profile is null)
                    {
                        // Its ground is this tile's: a source is tiled by where it was found, in every pass.
                        var before = fitList[i];
                        AddModel(before, +1);
                        var refit = FitAndSubtract(all[i], before, crowded[i], ground);
                        if (refit.Outcome == StarFitOutcome.NoFit)
                        {
                            // A refit that failed is no evidence against the fit it was to improve: that one goes back
                            // (the Orion master's 700-sigma star at the frame's edge was left whole by its refinement).
                            AddModel(before, -1);
                            refit = before;
                        }
                        fitList[i] = refit;
                    }
                    else if (fitList[i].Outcome == StarFitOutcome.NoFit)
                    {
                        // Given up beside a brighter star, which is out now: tried again.
                        fitList[i] = FitAndSubtract(all[i], null, crowded[i], ground);
                    }
                });
                ct.ThrowIfCancellationRequested();
            }

            MergeCoreFragments(all, fitList);
            var inpainted = BuildInpaintMask(fitList);
            var subtracted = BuildSubtractedMask(fitList);
            var correlation = FillHoles(inpainted);
            RefreshLuminance();
            var dugCores = SweepHoles(fitList, inpainted);
            CleanRemnants(subtracted, inpainted);
            _channelRms = ChannelRms();

            var stars = Describe(sources, fitList, secondFrom, inpainted);
            var statistics = Measure(stars, inpainted, subtracted, correlation, dugCores, sw);
            var plate = BuildPlate();
            logger?.LogInformation(
                "ClassicalStarRemover: {W}x{H}x{C}: {Found} point sources, {Subtracted} subtracted, {Knots} left as structure, inpaint {Inpaint:P2}, field width scale {Scale:F3}, {Ms} ms.",
                _width, _height, _channels, stars.Length, stars.Count(static s => s.Outcome == StarFitOutcome.Subtracted),
                stars.Count(static s => s.Outcome is StarFitOutcome.Knot or StarFitOutcome.TooNarrow), statistics.InpaintFraction,
                _fieldScale, sw.ElapsedMilliseconds);
            return new StarlessPlate(plate, subtracted, inpainted, stars, statistics, FieldProfile());
        }

        public StarlessFieldProfile MeasureFieldProfile()
        {
            Calibrate();
            return FieldProfile();
        }

        // The profile every star is subtracted with, per channel and for the luminance (StarlessFieldProfile).
        private StarlessFieldProfile FieldProfile()
        {
            static FieldChannelProfile Of(double alpha, double beta, RadialCorrection? table)
                => new FieldChannelProfile(alpha, beta, table?.ReferenceAlpha ?? alpha, table?.Table ?? ImmutableArray<float>.Empty);
            return new StarlessFieldProfile(
                [.. Enumerable.Range(0, _channels).Select(c => Of(_channelAlpha[c], _channelBeta[c], _channelCorrection[c]))],
                Of(_lumAlpha, _beta, _correction));
        }

        // Everything before the first star is fitted: the working planes, the luminance, the found sources, the sky and its
        // texture, the field's PSF and residual table, per channel and for the luminance.
        private PointSource[] Calibrate()
        {
            var n = _width * _height;
            _work = new float[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                _work[c] = image.GetChannelSpan(c).ToArray();
            }
            _original = new float[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                _original[c] = image.GetChannelSpan(c).ToArray();
            }
            _fullScale = new float[_channels];
            for (var c = 0; c < _channels; c++)
            {
                var top = float.NegativeInfinity;
                var plane = _work[c];
                for (var i = 0; i < n; i++)
                {
                    if (plane[i] > top && !IsAbsent(i % _width, i / _width))
                    {
                        top = plane[i];
                    }
                }
                _fullScale[c] = top;
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
            _texture = TextureField.FromSkyMap(skyMap, _lum, _width, _height, _absent, lumPsf.Fwhm);
            _rms = _texture.Spread;
            _holeNoise = _texture.Noise;
            _sky = new float[n];
            skyMap.FillBackground(_sky);
            ct.ThrowIfCancellationRequested();

            (_lumAlpha, _beta) = CalibratePlane(found, _lum, lumPsf);
            _fieldScale = _lumAlpha / lumPsf.Alpha;
            _correction = BuildRadialCorrection(found, _lum, LumPsf(1.0));
            CalibrateChannels(found);
            logger?.LogDebug("ClassicalStarRemover: radial correction from {Stars} stars, reach {Reach:F1} px.", _correction?.Stars ?? 0, _correction?.Reach ?? 0);
            return found;
        }

        // Four checkerboard phases of tiles; within a tile, the given order (the significance order). The body is handed the
        // tile's ground, and the work it does for a star reads and writes the planes there and nowhere else (#2378).
        private void RunTiled(int count, Func<int, (float X, float Y)> position, Action<int, PixelRect> body)
        {
            _pass = _passes++;
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
                _phase = phase;
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
                ParallelFor.Run(tiles.Count, TileThreads, t =>
                {
                    if (members[tiles[t]] is { } list)
                    {
                        var ground = TileGround(tiles[t] % tilesX, tiles[t] / tilesX, _width, _height);
                        foreach (var i in list)
                        {
                            body(i, ground);
                        }
                    }
                });
            }
            _pass = _phase = -1;
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
                if (s.Significance is >= 30f and <= 1000f && !IsSaturated(s, out _, _frame) && !grid.AnyWithin(s.X, s.Y, isolation, except: k))
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
                var window = CutWindow(s, 1.5 * WindowRadius(s.Peak, Math.Max(_rms[s.PeakY * _width + s.PeakX], 1e-12)), double.PositiveInfinity, _frame, plane: plane);
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
                if (s.Significance is >= 50f and <= 3000f && !IsSaturated(s, out _, _frame) && !grid.AnyWithin(s.X, s.Y, isolation, except: k))
                {
                    picks.Add(k);
                }
            }
            var perStar = new List<(float Distance, float Residual)>?[picks.Count];
            ParallelFor.Run(picks.Count, i =>
            {
                var k = picks[i];
                var s = found[k];
                if (CutWindow(s, reach + 8.0, double.PositiveInfinity, _frame, plane: plane) is not { } window)
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
                var sky = StatisticsHelper.NthSmallest(CollectionsMarshal.AsSpan(skyValues), skyValues.Count / 2);
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

        // A source within half a pixel of an earlier one is that source found twice (a pass boundary, a plateau): merged,
        // its model put back if it had been subtracted, so no star is taken out twice.
        private void DropDuplicates(PointSource[] sources, List<Fit> fits)
        {
            var grid = new PointGrid(sources, 2.0);
            for (var i = 0; i < sources.Length; i++)
            {
                var earlier = false;
                grid.ForEachWithin(sources[i].X, sources[i].Y, 0.5, j => earlier |= j < i && fits[j].Outcome != StarFitOutcome.Merged);
                if (!earlier)
                {
                    continue;
                }
                var f = fits[i];
                if (f.Outcome == StarFitOutcome.Subtracted)
                {
                    AddModel(f, +1);
                }
                f.Outcome = StarFitOutcome.Merged;
                fits[i] = f;
            }
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

        private bool IsSaturated(PointSource s, out double plateau, in PixelRect ground)
        {
            var radius = Math.Max(2, (int)Math.Ceiling(lumPsf.Fwhm));
            var box = Square(ground, s.PeakX, s.PeakY, radius, write: false);
            var peak = double.NegativeInfinity;
            for (var y = box.Top; y < box.Bottom; y++)
            {
                for (var x = box.Left; x < box.Right; x++)
                {
                    // Not Math.Max: one NaN of the absent canvas beside a star at the frame's edge made its plateau NaN,
                    // its saturation cut NaN and its every window empty, and a 700-sigma star of the Orion master was left.
                    var v = _lum[y * _width + x];
                    if (v > peak)
                    {
                        peak = v;
                    }
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
            for (var y = box.Top; y < box.Bottom; y++)
            {
                for (var x = box.Left; x < box.Right; x++)
                {
                    if (_lum[y * _width + x] >= near)
                    {
                        count++;
                    }
                }
            }
            return count >= 5 || NearFullScale(box);
        }

        // A core within FullScaleFraction of its channel's brightest pixel is at the master's clip level whatever its
        // shape: a clipped star stacked over sub-pixel shifts has a rounded top, no flat one (9 Sgr in the Lagoon master,
        // 0.98 against a full scale of 0.99, was fitted unsaturated at 1.61 times the field's width and left as a knot).
        // The box is IsSaturated's, already cut to the star's ground.
        private bool NearFullScale(in PixelRect box)
        {
            for (var c = 0; c < _channels; c++)
            {
                var plane = _work[c];
                var level = FullScaleFraction * _fullScale[c];
                if (!(level > 0))
                {
                    continue;
                }
                for (var y = box.Top; y < box.Bottom; y++)
                {
                    for (var x = box.Left; x < box.Right; x++)
                    {
                        if (plane[y * _width + x] >= level)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
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

        // Everything it reads and writes of the working planes is on its tile's ground (#2378), which the fit keeps.
        private Fit FitAndSubtract(PointSource s, Fit? previous, bool crowded, in PixelRect ground)
        {
            var fit = new Fit { X = s.X, Y = s.Y, Width = 1.0, Outcome = StarFitOutcome.NoFit, ChannelAmplitudes = new double[_channels], Significance = s.Significance, Ground = ground };
            var index = s.PeakY * _width + s.PeakX;
            var sigma = Math.Max(_rms[index], 1e-12);
            var sky = _sky[index];
            fit.Saturated = IsSaturated(s, out var plateau, ground);
            fit.Plateau = plateau;
            if (!fit.Saturated && s.Significance >= options.BetaFitSigma && CoreIsCompressed(s, sigma, sky, plateau, ground))
            {
                fit.Saturated = true;
            }

            // A saturated star's centre is its plateau's: on a flat top the finder's maximum lands wherever ties fall in
            // raster order, 4.9 px from Antares' centre and 9 px from Alnitak's, and a fit that found the true centre was
            // given up as having wandered. Its drift allowance is the plateau's radius.
            var (refX, refY, drift, extent) = fit.Saturated ? PlateauCentre(s, plateau, sky, ground) : (s.X, s.Y, 2.0, 0.0);
            var coreRadius = Math.Max(Math.Max(drift - 1.0, 1.0), extent);
            if (fit.Saturated && previous is null && drift - 1.0 >= GiantPlateauRadius)
            {
                return SubtractProfile(fit, refX, refY, coreRadius, QuietSigma(refX, refY, sigma));
            }
            var startX = previous?.X ?? refX;
            var startY = previous?.Y ?? refY;
            var amplitude = previous?.Amplitude ?? (fit.Saturated ? 4.0 * (plateau - sky) : Math.Max(s.Peak, sigma));
            var radius = WindowRadius(amplitude, sigma);
            if (fit.Saturated)
            {
                radius = Math.Max(radius, 4.0 * lumPsf.Fwhm + 3.0 * Math.Sqrt(CountPlateau(s, plateau, sky, ground) / Math.PI));
            }
            var saturationCut = fit.Saturated ? sky + 0.7 * (plateau - sky) : double.PositiveInfinity;

            LinearResult? result = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (CutWindow(s, radius, saturationCut, ground, startX, startY) is not { } window)
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
                    // A big star's light is mostly halo no field Moffat describes, and the wing fit's sky takes it.
                    return fit.Saturated && previous is null ? SubtractProfile(fit, refX, refY, coreRadius, QuietSigma(refX, refY, sigma)) : fit;
                }
                if (HasLeftItsStar(r.X, r.Y, refX, refY, drift, fit.Saturated))
                {
                    // A fit this far from its star is not that star's. It is refused here and never followed: the next
                    // window would be cut where it ran to, about another star (#2352: on a 919-star field two sources on
                    // saturated stars' rims ran 328 and 364 px on their first fit, and before every tile's work was held
                    // to its ground, #2378, their second fits read what a tile beside was writing and the refused row
                    // that came of it differed from run to run). None of its numbers is taken: the row says where the
                    // star was found and that it has no fit.
                    // NO TEST REACHES THE SATURATED ARM of the line below (#2374). No saturated star's first fit left
                    // its star on any field of ClassicalStarRemoverTests, nor on three real masters of 148,008 sources
                    // (two ringed composites and a ringless drizzle), counted with this branch made to count and go
                    // on. The count was taken at the rule a saturated star is held by now, its centre itself: that
                    // was every star's rule when it was taken (#2363), before #2374 gave an unsaturated star another,
                    // and what it answers for a saturated one is pinned off this path, by HasLeftItsStar's own rows.
                    // Where one did, the answer would be the profile that the drift test below gives after a
                    // second fit; what this arm saves is that second fit's window.
                    return fit.Saturated && previous is null ? SubtractProfile(fit, refX, refY, coreRadius, QuietSigma(refX, refY, sigma)) : fit;
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

            if ((Math.Abs(final.X - refX) > drift || Math.Abs(final.Y - refY) > drift) && !OffCanvas(final.X, final.Y))
            {
                // A saturated star's wing fit pulled off its plateau by a bright neighbour: its symmetric profile is blind
                // to one neighbour (the second-lowest of eight sectors), where the fit was not (a 6,383-sigma star of the
                // Horsehead master, 10 px from an 820-sigma one, was left whole).
                return fit.Saturated && previous is null ? SubtractProfile(fit, refX, refY, coreRadius, QuietSigma(refX, refY, sigma)) : fit;
            }
            if (fit.Saturated
                && Math.Abs(final.SkyAt((int)Math.Round(final.X), (int)Math.Round(final.Y)) - AnnulusMedian(final.X, final.Y, final.Radius, final.Radius + 6.0, ground)) > HaloSkySigma * sigma)
            {
                // A wing fit whose sky is far from the level just past its window has light the field's Moffat cannot
                // hold: a halo it took by lowering its sky and raising its peak (the Horsehead master's 4,933-sigma star,
                // its sky 1,100 below that level at 2e6 for a 65,000 plateau, left a blob and four over-subtracted lobes).
                // The ring, not the background map at the star: a bright star raises the map under itself, and against
                // it an Orion star with no halo at all read as having one.
                return SubtractProfile(fit, refX, refY, coreRadius, QuietSigma(refX, refY, sigma));
            }
            var ratio = FwhmRatio(final.Width, fit.Beta);
            if (ratio > options.MaxWidthScale)
            {
                // Before a wide source is left as structure: a pair the matched filter merged into one peak is two stars
                // at the field's width (the two brightest "knots" of the Centaurus A master, 672 and 402 sigma at 1.63
                // and 1.61 times the field's width, are doubles 3 px apart).
                if (!fit.Saturated && s.Significance >= options.NonlinearFitSigma && FitPair(s, final, sigma, ground) is { } pair)
                {
                    return pair;
                }
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
                if (!Square(ground, px, py, 0, write: false).IsEmpty)
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
            if (crowded && CutWindow(s, final.Radius, saturationCut, ground, final.X, final.Y) is { } crowdedWindow)
            {
                var fixedWidth = s.Significance >= options.NonlinearFitSigma || fit.Saturated
                    ? FitNonlinear(crowdedWindow, _lumAlpha, _beta, final.X, final.Y, fitBeta: false, fitWidth: false, corr: _correction)
                    : SolveLinear(crowdedWindow, LumPsf(1.0), final.X, final.Y, _correction);
                if (fixedWidth is { } fw && fw.Amplitude > 0 && (Math.Abs(fw.X - refX) <= drift && Math.Abs(fw.Y - refY) <= drift || OffCanvas(fw.X, fw.Y)))
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
                    fit.ChannelAmplitudes[c] = CutWindow(s, final.Radius, saturationCut, ground, final.X, final.Y, _work[c]) is { } cw
                        ? SolveLinear(cw, ChannelPsf(c, final.Width, fit.Beta), final.X, final.Y, ChannelCorrection(c)).Amplitude
                        : 0.0;
                }
            }
            // A diverged fit is refused before it is subtracted (#1867): an unsaturated star cannot peak far above its channel's
            // brightest covered sample, so an amplitude past OvershootMargin times that (or not a number) is a fit that ran away
            // (one at 1e12 on a frame's edge once dominated the injector's pool, and subtracting it would have written a huge
            // hole into the plate). It is left as NoFit, which no reader takes for a star. A fit centred outside the frame with a sane
            // amplitude is still subtracted, saturated or not: on a ringless master that is a real star the frame's edge cut, and
            // leaving it would put half a star on the starless plate. Its row's LocalSigma is NaN, and that is the marker the next
            // reader sees: the injector refuses a NaN-sigma row from its pool (InjectionPopulation.Diverged).
            if (!fit.Saturated && Overshoots(fit))
            {
                fit.Outcome = StarFitOutcome.NoFit;
                return fit;
            }
            fit.Outcome = StarFitOutcome.Subtracted;
            AddModel(fit, -1);
            return fit;
        }

        private bool Overshoots(Fit fit) => AmplitudeOvershoots(fit.ChannelAmplitudes, _fullScale);

        // A core fainter than its own wings predict is saturated, hard or soft: the wings alone (below half the peak)
        // fitted at the field's PSF, their predicted peak against the observed one. The flat-top test needs five pixels
        // within 2 percent of the peak, which an undersampled star never has: an 11,833-sigma star at 2.3 px FWHM, its
        // fitted peak past the master's full scale, was fitted as a star 1.72 times too wide and left as a knot.
        private bool CoreIsCompressed(PointSource s, double sigma, double sky, double peak, in PixelRect ground)
        {
            var above = peak - sky;
            if (!(above > 0))
            {
                return false;
            }
            var radius = LumPsf(1.0).RadiusAtLevel(above, sigma);
            if (CutWindow(s, radius, sky + 0.5 * above, ground) is not { } wings
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
                    radius += CoreRadius(f, sigma);
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

        // The centroid of the plateau connected to the star's brightest pixel (pixels within 2 percent of the plateau's
        // height above sky), a drift allowance of its equal-area radius plus one, and its extent (its farthest pixel from
        // the centroid, which a giant's core fill must reach: an elongated plateau filled as its equal-area disc left
        // its ends as a bright rim in the Horsehead master); the found centre, 2 px and 0 when there is no plateau.
        private (double X, double Y, double Drift, double Extent) PlateauCentre(PointSource s, double plateau, double sky, in PixelRect ground)
        {
            var near = plateau - 0.02 * (plateau - sky);
            var reach = 64;
            var seedX = s.PeakX;
            var seedY = s.PeakY;
            var best = double.NegativeInfinity;
            var look = Math.Max(2, (int)Math.Ceiling(2.0 * lumPsf.Fwhm));
            var lookBox = Square(ground, s.PeakX, s.PeakY, look, write: false);
            for (var y = lookBox.Top; y < lookBox.Bottom; y++)
            {
                for (var x = lookBox.Left; x < lookBox.Right; x++)
                {
                    if (_lum[y * _width + x] > best)
                    {
                        best = _lum[y * _width + x];
                        seedX = x;
                        seedY = y;
                    }
                }
            }
            if (!(best >= near))
            {
                return (s.X, s.Y, 2.0, 0.0);
            }
            // The flood stays within reach of its seed and on the ground.
            var flood = Square(ground, seedX, seedY, reach, write: false);
            var seen = new HashSet<long>();
            var stack = new Stack<(int X, int Y)>();
            stack.Push((seedX, seedY));
            seen.Add((long)seedY * _width + seedX);
            double sumX = 0, sumY = 0;
            var count = 0;
            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                sumX += x;
                sumY += y;
                count++;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        var ny = y + dy;
                        if (!flood.Contains(nx, ny))
                        {
                            continue;
                        }
                        if (_lum[ny * _width + nx] >= near && seen.Add((long)ny * _width + nx))
                        {
                            stack.Push((nx, ny));
                        }
                    }
                }
            }
            if (count < 3)
            {
                return (s.X, s.Y, 2.0, 0.0);
            }
            var cx = sumX / count;
            var cy = sumY / count;
            var extent = 0.0;
            foreach (var key in seen)
            {
                var px = key % _width;
                var py = key / _width;
                extent = Math.Max(extent, Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) + 0.5);
            }
            return (cx, cy, Math.Max(2.0, Math.Sqrt(count / Math.PI) + 1.0), extent);
        }

        // The radius a saturated star's core is filled out to: its plateau where its own profile was subtracted, else
        // where its Moffat passes the plateau's height.
        private double CoreRadius(in Fit f, double sigma)
            => f.Profile is not null ? f.PlateauRadius : LumPsf(f.Width, f.Beta).RadiusAtLevel(f.Amplitude, Math.Max(f.Plateau - f.Sky, sigma));

        // Where a star falls below a level, in the luminance (channel -1) or one channel: its own profile, or its Moffat.
        private double ReachAt(in Fit f, double level, int channel = -1)
        {
            if (f.Profile is { } profile)
            {
                var bins = profile[0].Length;
                for (var b = 0; b < bins; b++)
                {
                    var value = 0.0;
                    if (channel < 0)
                    {
                        foreach (var p in profile)
                        {
                            value += p[b];
                        }
                        value /= profile.Length;
                    }
                    else
                    {
                        value = profile[channel][b];
                    }
                    if (b > f.PlateauRadius && value < level)
                    {
                        return b;
                    }
                }
                return bins;
            }
            return channel < 0
                ? LumPsf(f.Width, f.Beta).RadiusAtLevel(f.Amplitude, level)
                : ChannelPsf(channel, f.Width, f.Beta).RadiusAtLevel(Math.Abs(f.ChannelAmplitudes[channel]), level);
        }

        // A big saturated star taken out by its own azimuthal median profile, per channel, about its plateau's centre: the
        // median in 1 px rings out to MaxSubtractRadius, less the sky in the outermost rings, cut where the luminance
        // profile stays under a quarter sigma. Empirical, so it holds whatever halo the star has; a median ignores the
        // neighbours and the thin spikes, which stay (spikes are left whole, the plan's decision). The plateau is filled.
        private float[][] SymmetricProfile(double cx, double cy, double plateauRadius, double sigma, bool monotone, in PixelRect ground, out double[] skyOut)
        {
            const int Sectors = 8;
            var reachMax = MaxSubtractRadius;
            var bins = new List<float>?[_channels, reachMax, Sectors];
            var box = Square(ground, (int)Math.Round(cx), (int)Math.Round(cy), reachMax, write: false);
            for (var y = box.Top; y < box.Bottom; y++)
            {
                for (var x = box.Left; x < box.Right; x++)
                {
                    var dx = x - cx;
                    var dy = y - cy;
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    var b = (int)d;
                    if (b >= reachMax || IsAbsent(x, y))
                    {
                        continue;
                    }
                    var sector = (int)((Math.Atan2(dy, dx) + Math.PI) / (2.0 * Math.PI) * Sectors) % Sectors;
                    for (var c = 0; c < _channels; c++)
                    {
                        var v = _work[c][y * _width + x];
                        if (float.IsFinite(v))
                        {
                            (bins[c, b, sector] ??= new List<float>()).Add(v);
                        }
                    }
                }
            }
            static float MedianOf(List<float> list)
            {
                return StatisticsHelper.NthSmallest(CollectionsMarshal.AsSpan(list), list.Count / 2);
            }
            // The star's SYMMETRIC light: per ring, the second-lowest of eight sector medians. A star's halo, a filter's
            // reflection ring, a bloom are the same all round; a nebula, a neighbour, a spike are not, and the second-lowest
            // sector is blind to them (a spike lights four sectors, one bright patch one). A profile ended where it first
            // flattened left Alnitak's reflection ring in the starless plate, and one ended at a far sky took IC 434 out with
            // it; this keeps the ring with the star and the nebula in the plate.
            var symmetric = new float[_channels][];
            var sectorValues = new List<float>(Sectors);
            for (var c = 0; c < _channels; c++)
            {
                symmetric[c] = new float[reachMax];
                for (var b = 0; b < reachMax; b++)
                {
                    sectorValues.Clear();
                    for (var k = 0; k < Sectors; k++)
                    {
                        if (bins[c, b, k] is { Count: > 0 } list)
                        {
                            sectorValues.Add(MedianOf(list));
                        }
                    }
                    // The second-lowest (NaN for none, the one for one).
                    symmetric[c][b] = StatisticsHelper.NthSmallest(CollectionsMarshal.AsSpan(sectorValues), 1);
                }
            }
            // The sky is the symmetric profile's own level in its outermost rings; the star reaches out to the last ring
            // still half a sigma above it (smoothed over five rings), plus four, and is tapered over its last fifth.
            const int SkyRings = 24;
            var farSky = new double[_channels];
            for (var c = 0; c < _channels; c++)
            {
                var tail = new List<float>();
                for (var b = reachMax - SkyRings; b < reachMax; b++)
                {
                    if (float.IsFinite(symmetric[c][b]))
                    {
                        tail.Add(symmetric[c][b]);
                    }
                }
                farSky[c] = tail.Count > 0 ? MedianOf(tail) : 0.0;
            }
            var excess = new double[reachMax];
            for (var b = 0; b < reachMax; b++)
            {
                var sum = 0.0;
                var n = 0;
                for (var c = 0; c < _channels; c++)
                {
                    if (float.IsFinite(symmetric[c][b]))
                    {
                        sum += symmetric[c][b] - farSky[c];
                        n++;
                    }
                }
                excess[b] = n > 0 ? sum / n : double.NaN;
            }
            var minimum = (int)Math.Ceiling(plateauRadius) + 3;
            var reach = minimum;
            for (var b = reachMax - SkyRings - 1; b >= minimum; b--)
            {
                var window = new List<double>(5);
                for (var k = Math.Max(0, b - 2); k <= Math.Min(reachMax - 1, b + 2); k++)
                {
                    if (double.IsFinite(excess[k]))
                    {
                        window.Add(excess[k]);
                    }
                }
                if (window.Count > 0 && StatisticsHelper.NthSmallest(CollectionsMarshal.AsSpan(window), window.Count / 2) > 0.5 * sigma)
                {
                    reach = Math.Min(b + 4, reachMax - SkyRings);
                    break;
                }
            }
            if (monotone)
            {
                return MonotoneProfile(symmetric, minimum, reachMax - SkyRings, sigma, out skyOut);
            }
            var profile = new float[_channels][];
            var taperFrom = 0.8 * reach;
            for (var c = 0; c < _channels; c++)
            {
                profile[c] = new float[reach];
                for (var b = 0; b < reach; b++)
                {
                    var taper = b < taperFrom ? 1.0 : Math.Max(0.0, (reach - b) / (reach - taperFrom));
                    profile[c][b] = float.IsFinite(symmetric[c][b]) ? (float)(Math.Max(0.0, symmetric[c][b] - farSky[c]) * taper) : 0f;
                }
            }
            skyOut = farSky;
            return profile;
        }

        // A star's own light only falls with radius, so its profile is the running minimum of the symmetric one over
        // five rings' median, and it ends where that stops falling (judged nine rings smoothed, so the noise cannot end it): the first ring past the plateau over whose last FlatRings rings it dropped less
        // than FlatDrop sigma. It is measured against the level it flattens to (the median of the next FlatRings rings),
        // not a far sky: a giant inside a bright nebula (eta Carinae, at 600 and at 24 mm; 9 Sgr in the Lagoon) is
        // surrounded by nebula brighter than any sky 100 px out, and a profile down to that sky took the nebula into the
        // stars plate and left a dark disc. A displaced reflection ring is the halo search's (FindHaloCentre), whose
        // profile keeps its free shape.
        private float[][] MonotoneProfile(float[][] symmetric, int minimum, int limit, double sigma, out double[] floor)
        {
            var bins = symmetric[0].Length;
            var smooth = new double[_channels][];
            var medians = new double[_channels][];
            var lum = new double[bins];
            var counts = new int[bins];
            for (var c = 0; c < _channels; c++)
            {
                var median = new double[bins];
                var window = new List<float>(5);
                for (var b = 0; b < bins; b++)
                {
                    window.Clear();
                    for (var k = Math.Max(0, b - 2); k <= Math.Min(bins - 1, b + 2); k++)
                    {
                        if (float.IsFinite(symmetric[c][k]))
                        {
                            window.Add(symmetric[c][k]);
                        }
                    }
                    median[b] = window.Count > 0 ? StatisticsHelper.NthSmallest(CollectionsMarshal.AsSpan(window), window.Count / 2) : double.NaN;
                }
                medians[c] = median;
                smooth[c] = new double[bins];
                for (var b = 0; b < bins; b++)
                {
                    // Inside the plateau a mean would carry the saturated level outward; only beyond it is the profile smoothed.
                    if (b <= minimum)
                    {
                        smooth[c][b] = median[b];
                        continue;
                    }
                    double sum = 0;
                    var n = 0;
                    for (var k = Math.Max(minimum, b - 4); k <= Math.Min(bins - 1, b + 4); k++)
                    {
                        if (double.IsFinite(median[k]))
                        {
                            sum += median[k];
                            n++;
                        }
                    }
                    smooth[c][b] = n > 0 ? sum / n : double.NaN;
                }
                for (var b = 0; b < bins; b++)
                {
                    if (double.IsFinite(smooth[c][b]))
                    {
                        lum[b] += smooth[c][b];
                        counts[b]++;
                    }
                }
            }
            for (var b = 0; b < bins; b++)
            {
                lum[b] = counts[b] > 0 ? lum[b] / counts[b] : double.NaN;
            }
            static double[] RunningMinimum(double[] values)
            {
                var m = new double[values.Length];
                var low = double.PositiveInfinity;
                for (var b = 0; b < values.Length; b++)
                {
                    if (double.IsFinite(values[b]) && values[b] < low)
                    {
                        low = values[b];
                    }
                    m[b] = low;
                }
                return m;
            }
            var lumMin = RunningMinimum(lum);
            var end = limit;
            for (var b = minimum + FlatRings; b < limit; b++)
            {
                if (double.IsFinite(lumMin[b]) && double.IsFinite(lumMin[b - FlatRings]) && lumMin[b - FlatRings] - lumMin[b] < FlatDrop * sigma)
                {
                    end = b;
                    break;
                }
            }
            var reach = Math.Max(minimum, Math.Min(end + 4, limit));
            floor = new double[_channels];
            var profile = new float[_channels][];
            var taperFrom = 0.8 * reach;
            for (var c = 0; c < _channels; c++)
            {
                var tail = new List<double>();
                for (var b = end; b < Math.Min(bins, end + FlatRings); b++)
                {
                    if (double.IsFinite(smooth[c][b]))
                    {
                        tail.Add(smooth[c][b]);
                    }
                }
                floor[c] = tail.Count > 0 ? StatisticsHelper.NthSmallest(CollectionsMarshal.AsSpan(tail), tail.Count / 2) : 0.0;
                // The minimum of the medians, never of the smoothed values: a mean over a steep halo sits above it (the
                // halo is convex), and subtracted it dug a disc 0.7 sigma deep round the Horsehead master's brightest star.
                // A minimum of noisy values errs low, which leaves light rather than taking it.
                var channelMin = RunningMinimum(medians[c]);
                // No halo falls slower than the inverse square of its radius (a stellar aureole's far wing; a Moffat's
                // falls as r^-2beta), so past the plateau the profile is bounded by r^-2 from its value just outside it.
                // A nebula that falls away from its star more slowly is not the star's: eta Carinae's Keyhole, at 600 mm,
                // went into the stars plate under a profile that only had to fall.
                var anchor = minimum < bins && double.IsFinite(channelMin[minimum]) ? Math.Max(0.0, channelMin[minimum] - floor[c]) : double.PositiveInfinity;
                profile[c] = new float[reach];
                for (var b = 0; b < reach; b++)
                {
                    var taper = b < taperFrom ? 1.0 : Math.Max(0.0, (reach - b) / (reach - taperFrom));
                    // The plateau and the ring beside it keep the measured values, unsmoothed: they are filled anyway, and
                    // the amplitude is read off the first.
                    var value = b <= minimum && float.IsFinite(symmetric[c][b]) ? symmetric[c][b] : channelMin[b];
                    var excess = double.IsFinite(value) ? Math.Max(0.0, value - floor[c]) : 0.0;
                    if (b > minimum)
                    {
                        excess = Math.Min(excess, anchor * ((double)minimum / b) * ((double)minimum / b));
                    }
                    profile[c][b] = (float)(excess * taper);
                }
            }
            return profile;
        }

        // A big saturated star taken out by its own symmetric light (SymmetricProfile) about its plateau's centre, then,
        // for the biggest, a reflection halo about its own centre: a filter's ring is usually displaced from its star,
        // so a profile centred on the star smears it over the sectors and leaves it (Alnitak's, behind an L-Quad).
        // Read, searched and subtracted on the fit's ground (#2378): a giant is worked about its plateau's centre, which can
        // stand a plateau's radius from where the finder put it, so its profile's rings are what reach furthest past a tile.
        private Fit SubtractProfile(Fit fit, double cx, double cy, double plateauRadius, double sigma)
        {
            var profile = SymmetricProfile(cx, cy, plateauRadius, sigma, monotone: true, fit.Ground, out var farSky);
            fit.ProfileFloor = farSky;
            fit.ProfileMargin = ProfileClampSigma * sigma;
            var reach = profile[0].Length;
            var x0 = (int)Math.Round(cx);
            var y0 = (int)Math.Round(cy);
            var peak = 0.0;
            for (var c = 0; c < _channels; c++)
            {
                fit.ChannelAmplitudes[c] = profile[c].Length > 0 ? profile[c][0] : 0.0;
                peak += fit.ChannelAmplitudes[c] / _channels;
            }
            fit.Profile = profile;
            fit.PlateauRadius = plateauRadius;
            fit.X = cx;
            fit.Y = cy;
            fit.Width = 1.0;
            fit.Beta = _beta;
            fit.Amplitude = peak;
            fit.Sky = farSky.Average();
            fit.SkyX = fit.SkyY = 0.0;
            fit.WindowX = x0;
            fit.WindowY = y0;
            fit.FitRadius = reach;
            fit.SubtractRadius = reach;
            fit.Outcome = StarFitOutcome.Subtracted;
            AddModel(fit, -1);
            if (plateauRadius >= HaloSearchPlateauRadius && FindHaloCentre(cx, cy, plateauRadius, sigma, fit.Ground) is { } halo)
            {
                var haloProfile = SymmetricProfile(halo.X, halo.Y, 0.0, sigma, monotone: false, fit.Ground, out var haloFloor);
                if (haloProfile[0].Length > 0)
                {
                    fit.HaloProfile = haloProfile;
                    fit.HaloX = halo.X;
                    fit.HaloY = halo.Y;
                    fit.HaloFloor = haloFloor;
                    AddProfileAt(halo.X, halo.Y, haloProfile, -1, fit.Ground, haloFloor, fit.ProfileMargin);
                }
            }
            return fit;
        }

        // Where the light left around a giant, once its own profile is out, is most symmetric: centres within
        // HaloSearchOffset px of the star on a 2 px grid, each scored by the rings' symmetric excess (the second-lowest
        // of eight sectors' trimmed means, every second pixel, against the outer rings' level), summed over the rings past
        // the plateau. A ring counts only where at least four consecutive rings stand half a sigma above the sky; null when
        // no centre has one.
        // Every second pixel, on the lattice the frame alone would start it on: the ground cuts rows and columns off, and
        // the samples it leaves are the ones read before #2378.
        private (double X, double Y)? FindHaloCentre(double cx, double cy, double plateauRadius, double sigma, in PixelRect ground)
        {
            const int Sectors = 8;
            const int Rings = 100;
            const int SkyRings = 20;
            var inner = (int)Math.Ceiling(plateauRadius) + 3;
            (double X, double Y)? best = null;
            var bestScore = 0.0;
            var sums = new double[Rings, Sectors];
            var counts = new int[Rings, Sectors];
            var symmetric = new double[Rings];
            var sectorMeans = new List<double>(Sectors);
            for (var oy = -HaloSearchOffset; oy <= HaloSearchOffset; oy += 2)
            {
                for (var ox = -HaloSearchOffset; ox <= HaloSearchOffset; ox += 2)
                {
                    var hx = cx + ox;
                    var hy = cy + oy;
                    Array.Clear(sums);
                    Array.Clear(counts);
                    var x0 = (int)Math.Round(hx);
                    var y0 = (int)Math.Round(hy);
                    var box = Square(ground, x0, y0, Rings, write: false);
                    var firstY = OnLattice(Math.Max(0, y0 - Rings), box.Top);
                    var firstX = OnLattice(Math.Max(0, x0 - Rings), box.Left);
                    for (var y = firstY; y < box.Bottom; y += 2)
                    {
                        for (var x = firstX; x < box.Right; x += 2)
                        {
                            var dx = x - hx;
                            var dy = y - hy;
                            var b = (int)Math.Sqrt(dx * dx + dy * dy);
                            var v = _lum[y * _width + x];
                            if (b >= Rings || IsAbsent(x, y) || !float.IsFinite(v))
                            {
                                continue;
                            }
                            var k = (int)((Math.Atan2(dy, dx) + Math.PI) / (2.0 * Math.PI) * Sectors) % Sectors;
                            sums[b, k] += v;
                            counts[b, k]++;
                        }
                    }
                    for (var b = 0; b < Rings; b++)
                    {
                        sectorMeans.Clear();
                        for (var k = 0; k < Sectors; k++)
                        {
                            if (counts[b, k] > 0)
                            {
                                sectorMeans.Add(sums[b, k] / counts[b, k]);
                            }
                        }
                        symmetric[b] = StatisticsHelper.NthSmallest(CollectionsMarshal.AsSpan(sectorMeans), 1);
                    }
                    var tail = new List<double>();
                    for (var b = Rings - SkyRings; b < Rings; b++)
                    {
                        if (double.IsFinite(symmetric[b]))
                        {
                            tail.Add(symmetric[b]);
                        }
                    }
                    if (tail.Count == 0)
                    {
                        continue;
                    }
                    var sky = StatisticsHelper.NthSmallest(CollectionsMarshal.AsSpan(tail), tail.Count / 2);
                    double score = 0;
                    var run = 0;
                    var longest = 0;
                    for (var b = inner; b < Rings - SkyRings; b++)
                    {
                        var excess = double.IsFinite(symmetric[b]) ? symmetric[b] - sky : 0.0;
                        if (excess > 0.5 * sigma)
                        {
                            score += excess;
                            run++;
                            longest = Math.Max(longest, run);
                        }
                        else
                        {
                            run = 0;
                        }
                    }
                    if (longest >= 4 && score > bestScore)
                    {
                        bestScore = score;
                        best = (hx, hy);
                    }
                }
            }
            return best;

            // The first index from `from` on that is an even step from `anchor`, which is never past it.
            static int OnLattice(int anchor, int from) => anchor + (from - anchor + 1) / 2 * 2;
        }

        // The sky noise where a giant's light no longer reaches: the background map's rms in a ring 100 to 128 px out
        // (its own cells near the star hold the star, and half a sigma of that inflated rms was more than Alnitak's whole
        // reflection ring). The given sigma when the ring holds nothing.
        private double QuietSigma(double cx, double cy, double fallback)
        {
            var values = new List<float>();
            var x0 = (int)Math.Round(cx);
            var y0 = (int)Math.Round(cy);
            for (var y = Math.Max(0, y0 - 128); y <= Math.Min(_height - 1, y0 + 128); y += 4)
            {
                for (var x = Math.Max(0, x0 - 128); x <= Math.Min(_width - 1, x0 + 128); x += 4)
                {
                    var d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    if (d2 >= 100.0 * 100.0 && d2 <= 128.0 * 128.0 && !IsAbsent(x, y) && _rms[y * _width + x] > 0)
                    {
                        values.Add(_rms[y * _width + x]);
                    }
                }
            }
            if (values.Count == 0)
            {
                return fallback;
            }
            return Math.Min(fallback, StatisticsHelper.NthSmallest(CollectionsMarshal.AsSpan(values), values.Count / 2));
        }

        // The giants (saturated with a plateau of GiantPlateauRadius or more), brightest first, each with its plateau's
        // centre and radius: a giant's plateau and rim hold several local maxima, and a fainter one fitted as a star of its
        // own put a huge Moffat on top of the giant's profile (the Horsehead's, found three times).
        private List<(double X, double Y, double Radius, int SeedX, int SeedY)> FindGiants(PointSource[] found)
        {
            var giants = new List<(double X, double Y, double Radius, int SeedX, int SeedY)>();
            foreach (var s in found)
            {
                if (s.Significance < 50f || !IsSaturated(s, out var plateau, _frame) || giants.Any(g => (g.X - s.X) * (g.X - s.X) + (g.Y - s.Y) * (g.Y - s.Y) <= g.Radius * g.Radius))
                {
                    continue;
                }
                var (cx, cy, drift, _) = PlateauCentre(s, plateau, _sky[s.PeakY * _width + s.PeakX], _frame);
                if (drift - 1.0 >= GiantPlateauRadius)
                {
                    giants.Add((cx, cy, drift - 1.0 + 2.0 * lumPsf.Fwhm, s.PeakX, s.PeakY));
                }
            }
            return giants;
        }

        // Inside a giant's plateau and rim, and not the giant's own detection (the brightest there, which FindGiants met
        // first and centred on).
        private static bool InsideGiant(PointSource s, List<(double X, double Y, double Radius, int SeedX, int SeedY)> giants)
        {
            foreach (var g in giants)
            {
                var d2 = (g.X - s.X) * (g.X - s.X) + (g.Y - s.Y) * (g.Y - s.Y);
                if (d2 <= g.Radius * g.Radius)
                {
                    return !(s.PeakX == g.SeedX && s.PeakY == g.SeedY);
                }
            }
            return false;
        }

        private int CountPlateau(PointSource s, double plateau, double sky, in PixelRect ground)
        {
            var near = plateau - 0.02 * (plateau - sky);
            var count = 0;
            var box = Square(ground, s.PeakX, s.PeakY, MaxFitRadius / 2, write: false);
            for (var y = box.Top; y < box.Bottom; y++)
            {
                for (var x = box.Left; x < box.Right; x++)
                {
                    if (_lum[y * _width + x] >= near)
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        // Adds (sign +1) or subtracts (sign -1) a star's model from every channel, and keeps the luminance the channels' mean;
        // on the fit's ground alone (#2378), so a put-back restores exactly what the subtraction took.
        private void AddModel(in Fit fit, int sign)
        {
            if (fit.Profile is { } profile)
            {
                AddProfileAt(fit.X, fit.Y, profile, sign, fit.Ground, fit.ProfileFloor, fit.ProfileMargin);
                if (fit.HaloProfile is { } halo)
                {
                    AddProfileAt(fit.HaloX, fit.HaloY, halo, sign, fit.Ground, fit.HaloFloor, fit.ProfileMargin);
                }
                return;
            }
            if (fit.CompanionAmplitudes is { } companionAmplitudes)
            {
                var companion = fit;
                companion.X = fit.CompanionX;
                companion.Y = fit.CompanionY;
                companion.ChannelAmplitudes = companionAmplitudes;
                companion.CompanionAmplitudes = null;
                AddModel(companion, sign);
            }
            var r = fit.SubtractRadius;
            var cx = (int)Math.Round(fit.X);
            var cy = (int)Math.Round(fit.Y);
            var box = Square(fit.Ground, cx, cy, r, write: true);
            var (y0, y1, x0, x1) = (box.Top, box.Bottom - 1, box.Left, box.Right - 1);
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

        // Whether a fit of this plate, centred at (x, y), has left the star it was started on: the rule and its reasons
        // are ClassicalStarRemover.HasLeftItsStar's, asked here about this frame and its cover.
        private bool HasLeftItsStar(double x, double y, double starX, double starY, double drift, bool saturated)
            => ClassicalStarRemover.HasLeftItsStar(x, y, starX, starY, drift, saturated, _width, _height,
                _covered ??= (cx, cy) => !IsAbsent(cx, cy));

        // A centre past the frame's edge or on its absent canvas: where a star cut by the edge has its true centre (the
        // Orion master's 700-sigma star, its peak in the last present column, fitted 3 px beyond it and given up as
        // having wandered), and where no neighbour can have pulled a fit.
        private bool OffCanvas(double x, double y)
        {
            var px = (int)Math.Round(x);
            var py = (int)Math.Round(y);
            return px < 0 || py < 0 || px >= _width || py >= _height || IsAbsent(px, py);
        }

        // A profile star's model: each channel's ring medians, linear between ring centres.
        // A subtraction with a floor takes from each pixel at most what stands above that floor less the margin: a giant's
        // symmetric light (the second-lowest of eight sectors) still holds some of its spikes' light, and between eighteen
        // spikes of the Antares-field giant it dug notches 4 to 20 sigma deep where there was no star light left to take.
        // A put-back adds the whole model; only DropDuplicates puts a profile star back.
        private void AddProfileAt(double fx, double fy, float[][] profile, int sign, in PixelRect ground, double[]? floor = null, double margin = 0.0)
        {
            var reach = profile[0].Length;
            var box = Square(ground, (int)Math.Round(fx), (int)Math.Round(fy), reach, write: true);
            for (var y = box.Top; y < box.Bottom; y++)
            {
                for (var x = box.Left; x < box.Right; x++)
                {
                    var d = Math.Sqrt((x - fx) * (x - fx) + (y - fy) * (y - fy));
                    if (d >= reach || IsAbsent(x, y))
                    {
                        continue;
                    }
                    var position = d - 0.5;
                    var i0 = Math.Clamp((int)Math.Floor(position), 0, reach - 1);
                    var i1 = Math.Min(i0 + 1, reach - 1);
                    var t = Math.Clamp(position - i0, 0.0, 1.0);
                    var i = y * _width + x;
                    for (var c = 0; c < _channels; c++)
                    {
                        var model = (1 - t) * profile[c][i0] + t * profile[c][i1];
                        if (sign < 0 && floor is { } f)
                        {
                            model = Math.Min(model, Math.Max(0.0, _work[c][i] - (f[c] - margin)));
                        }
                        _work[c][i] += (float)(sign * model);
                    }
                    if (_channels > 1)
                    {
                        _lum[i] = (_work[0][i] + _work[1][i] + _work[2][i]) / 3f;
                    }
                }
            }
        }

        // The fill's ceiling (HoleFill): the image the plate was made from, but never below the local sky (its background map
        // at the fill's scale). A star only adds light, so a fill above what was observed is no estimate; but a stack's own
        // undershoot at a bright core is not an observation of the sky either: an interpolation kernel ringing at a near-step
        // edge, or a black-cored star, its saturated subs over-corrected. As a ceiling it held the fill down at the defect:
        // on the Leo Triplet master 439 of the 450 dark filled pixels at its bright stars sat on it, 4 to 11 sigma under
        // their own sky (and 8 above the frame's darkest, so a frame-wide floor could not see them). The rim round the
        // Trapezium, which pulled a fill a hundred sigma above the dark gaps and is what the ceiling is for, stays capped:
        // at most the local sky, not the rim.
        private float[][] Ceiling()
        {
            if (_ceiling is { } built)
            {
                return built;
            }
            var ceiling = new float[_channels][];
            var n = _width * _height;
            for (var c = 0; c < _channels; c++)
            {
                var map = BackgroundMap.Estimate(_original[c], _width, _height, _absent, new BackgroundMapOptions(BlockSize: PointSourceFinder.SkyBlockFor(lumPsf.Fwhm)));
                var background = new float[n];
                map.FillBackground(background);
                float[] plane = [.. _original[c]];
                for (var i = 0; i < n; i++)
                {
                    if (float.IsFinite(background[i]) && plane[i] < background[i])
                    {
                        plane[i] = background[i];
                    }
                }
                ceiling[c] = plane;
            }
            _ceiling = ceiling;
            return ceiling;
        }

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
                // The hole tests' noise (the sweep, the reported depth): a dug core on a nebula sat 5 sigma of the pixels'
                // own noise under the plate round it and read as 3 against the map's spread, so it was left.
                PointSourceFinder.CapByDifferenceNoise(rms[c], _work[c], _width, _height, _absent, PointSourceFinder.SkyBlockFor(lumPsf.Fwhm));
            }
            return rms;
        }

        // The goal's guarantee: no star leaves a hole in any channel. After the fill, every subtracted star's core is read
        // per channel against the plate's own sky in an annulus beyond it; where it sits more than HoleSigma below, the
        // fill disc grows ring by ring until the plate is back at its sky, and is filled again from beyond that. The first
        // fill alone could not promise it: a pixel the mask missed at a star's centre, over-subtracted, was a known pixel to
        // the fill and dragged the whole disc down with it. A core whose mean passes but where the subtraction dug one
        // pixel (DugCore, #2449) has its DugCoreRadius disc filled in the same round. Returns how many stars were refilled
        // for a dug pixel alone.
        private int SweepHoles(List<Fit> fits, BitMatrix inpainted)
        {
            // Each index is written by its own iteration only.
            var dugAlone = new bool[fits.Count];
            for (var round = 0; round < HoleSweepRounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                var rms = ChannelRms();
                var discs = new System.Collections.Concurrent.ConcurrentBag<(int X, int Y, double R)>();
                var dug = new System.Collections.Concurrent.ConcurrentBag<(int X, int Y)>();
                ParallelFor.Run(fits.Count, k =>
                {
                    var f = fits[k];
                    if (f.Outcome != StarFitOutcome.Subtracted)
                    {
                        return;
                    }
                    var grow = 0.0;
                    var dugCore = false;
                    for (var c = 0; c < _channels; c++)
                    {
                        var (hole, dugHere) = CoreBelowItsSky(f, c, rms[c], testDug: RetestDugCores || !dugAlone[k]);
                        if (hole is { } r && r > grow)
                        {
                            grow = r;
                        }
                        dugCore |= dugHere;
                    }
                    if (grow > 0)
                    {
                        discs.Add(((int)Math.Round(f.X), (int)Math.Round(f.Y), grow));
                    }
                    else if (dugCore)
                    {
                        // A hole's disc, about the same centre, is never under 3 px and holds this one already.
                        dug.Add(((int)Math.Round(f.X), (int)Math.Round(f.Y)));
                        dugAlone[k] = true;
                    }
                });
                if (discs.IsEmpty && dug.IsEmpty)
                {
                    break;
                }
                var added = new BitMatrix(_height, _width);
                foreach (var (x, y, r) in discs)
                {
                    StampDisc(added, x, y, r + 1.0);
                }
                foreach (var (x, y) in dug)
                {
                    StampDisc(added, x, y, DugCoreRadius);
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
                HoleFill.Fill(_work, _width, _height, added, _absent, lumPsf.Fwhm, options.Seed + round + 1, Ceiling(), ct);
                RefreshLuminance();
                logger?.LogDebug("ClassicalStarRemover: hole sweep {Round}: {Holes} cores below their sky, {Dug} dug cores, refilled.", round + 1, discs.Count, dug.Count);
            }
            return dugAlone.Count(static d => d);
        }

        // The last safety net, which no fit's centre can miss: the finder run on the finished plate, and every point source
        // of RemnantCleanSigma or more inside a subtracted star's footprint taken as that star's remnant and refilled out
        // to where it falls to the noise. A bright star found as several pieces, its brightest fitted 9 px off its centre
        // and the others called a knot and given up, left a bright blob the per-star sweep (which reads each star's own
        // core) never looked at. A compact source away from every star (a knot) is not touched. Two rounds.
        private void CleanRemnants(BitMatrix subtracted, BitMatrix inpainted)
        {
            for (var round = 0; round < 2; round++)
            {
                ct.ThrowIfCancellationRequested();
                var (left, sky) = PointSourceFinder.Find(_lum, _width, _height, _absent, lumPsf.Fwhm, RemnantCleanSigma);
                var added = new BitMatrix(_height, _width);
                var any = false;
                foreach (var l in left)
                {
                    if (!subtracted[l.PeakY, l.PeakX])
                    {
                        continue;
                    }
                    var sigma = Math.Max(sky.RmsAt(l.PeakX, l.PeakY), 1e-12);
                    var radius = Math.Max(2.0 * lumPsf.Fwhm, LumPsf(1.0).RadiusAtLevel(l.Peak, sigma)) + 1.0;
                    StampDisc(added, (int)Math.Round(l.X), (int)Math.Round(l.Y), Math.Min(radius, 40.0));
                    any = true;
                }
                if (!any)
                {
                    return;
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
                HoleFill.Fill(_work, _width, _height, added, _absent, lumPsf.Fwhm, options.Seed + 100 + round, Ceiling(), ct);
                RefreshLuminance();
            }
        }

        // Where a star's sky annulus starts: past where its light falls to the level asked about, at most SkyAnnulusMax px out,
        // and never inside what it must clear. A giant's plateau can pass the cap on its own (eta Carinae at 288 mm, 67 px),
        // where a clamp whose floor passed its ceiling threw and failed the master. Below the cap it IS that clamp.
        private static double SkyAnnulus(double reach, double clear) => Math.Max(clear, Math.Min(reach, SkyAnnulusMax));

        // The sweep's two tests of a subtracted star's core in one channel, both against that channel's median in an annulus
        // beyond the star and in the per-pixel noise at its centre: a hole (HoleRadius) and a dug pixel (DugCore). No hole
        // and no dug pixel where that noise or that sky is unknown. A star already refilled for a dug pixel alone is not
        // tested for one again (testDug false): the fill's own grain can sit under the gate, and redrawing the same disc
        // every round would only reroll it.
        private (double? HoleRadius, bool Dug) CoreBelowItsSky(in Fit f, int channel, float[] rms, bool testDug = true)
        {
            var cx = (int)Math.Clamp(Math.Round(f.X), 0, _width - 1);
            var cy = (int)Math.Clamp(Math.Round(f.Y), 0, _height - 1);
            var sigma = rms[cy * _width + cx];
            if (!(sigma > 0))
            {
                return (null, false);
            }
            var psf = ChannelPsf(channel, f.Width, f.Beta);
            var core = Math.Max(1.5, psf.Fwhm);
            if (f.Saturated)
            {
                core = Math.Max(core, CoreRadius(f, _rms[cy * _width + cx]));
            }
            var annulus = SkyAnnulus(ReachAt(f, sigma, channel) + 2.0, core + 2.0);
            var plane = _work[channel];
            var sky = AnnulusMedianPlane(plane, f.X, f.Y, annulus, annulus + 6.0, _frame);
            if (!double.IsFinite(sky))
            {
                return (null, false);
            }
            return (HoleRadius(f, plane, core, annulus, sky, sigma), FillDugCores && testDug && DugCore(f, channel, sky, annulus));
        }

        // Whether the subtraction dug a core pixel (#2449): a pixel within DugCoreSearch px of the fit's centre that sits
        // more than DugPixelSigma of the pixel-to-pixel noise under the annulus sky, where the subtraction took more than
        // DugPixelSigma of it. What the subtraction took is the input less the plate, every model subtracted at that pixel
        // (and where the first fill or an earlier round already replaced it, the input less that fill). A dark pixel the
        // input itself holds, where no model took light, is not one the fit dug and is left alone. The hole test cannot see
        // this: one pixel barely moves the mean over a core.
        // The noise is the annulus's own neighbour differences (NeighbourDifferenceNoise), not the sweep's per-pixel rms.
        // That rms counts the master's correlated noise, and where a crowd or a bright nebula raises it, 2.3 to 7.3 times
        // the neighbour differences at the deepest pits the first version of this trigger left on the Tarantula master: a
        // single dug pixel read against it fell under 4 where the pixel-to-pixel noise put it at 5 and more.
        private bool DugCore(in Fit f, int channel, double sky, double annulus)
        {
            var plate = _work[channel];
            var input = _original[channel];
            var x0 = (int)Math.Round(f.X);
            var y0 = (int)Math.Round(f.Y);
            var r = (int)Math.Ceiling(DugCoreSearch);
            var (top, bottom) = (Math.Max(0, y0 - r), Math.Min(_height - 1, y0 + r));
            var (left, right) = (Math.Max(0, x0 - r), Math.Min(_width - 1, x0 + r));
            // A precheck that skips little: the minimum of the roughly 7 pixels in the search disc is under the annulus
            // MEDIAN almost every time, so the noise below is measured for nearly every subtracted star, channel and round.
            // It only spares a core whose every pixel is at or over its sky.
            var lowest = double.PositiveInfinity;
            for (var y = top; y <= bottom; y++)
            {
                for (var x = left; x <= right; x++)
                {
                    if (InDugCoreSearch(f, x, y))
                    {
                        lowest = Math.Min(lowest, plate[y * _width + x]);
                    }
                }
            }
            if (!(lowest < sky))
            {
                return false;
            }
            var noise = NeighbourDifferenceNoise(plate, f.X, f.Y, annulus, annulus + 6.0);
            if (!(noise > 0))
            {
                return false;
            }
            var depth = DugPixelSigma * noise;
            for (var y = top; y <= bottom; y++)
            {
                for (var x = left; x <= right; x++)
                {
                    var i = y * _width + x;
                    if (InDugCoreSearch(f, x, y) && plate[i] < sky - depth && input[i] - plate[i] > depth)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private bool InDugCoreSearch(in Fit f, int x, int y)
            => (x - f.X) * (x - f.X) + (y - f.Y) * (y - f.Y) <= DugCoreSearch * DugCoreSearch && !IsAbsent(x, y);

        // The pixel-to-pixel noise of a plane over an annulus: 1.4826 times the median magnitude of the differences between
        // horizontal neighbours both inside it, over root 2, the noise #2449 read its pits in. NaN with fewer than
        // MinNoisePairs pairs.
        private double NeighbourDifferenceNoise(float[] plane, double cx, double cy, double inner, double outer)
        {
            var r = (int)Math.Ceiling(outer);
            var x0 = (int)Math.Round(cx);
            var y0 = (int)Math.Round(cy);
            var (top, bottom) = (Math.Max(0, y0 - r), Math.Min(_height - 1, y0 + r));
            var (left, right) = (Math.Max(0, x0 - r), Math.Min(_width - 2, x0 + r - 1));
            using var buffer = ArrayPoolHelper.Rent<float>((2 * r + 1) * (2 * r + 1));
            var diffs = buffer.AsSpan(0, buffer.Length);
            var n = 0;
            bool Inside(int x, int y)
            {
                var d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                return d2 >= inner * inner && d2 <= outer * outer && !IsAbsent(x, y) && float.IsFinite(plane[y * _width + x]);
            }
            for (var y = top; y <= bottom; y++)
            {
                for (var x = left; x <= right; x++)
                {
                    if (Inside(x, y) && Inside(x + 1, y))
                    {
                        diffs[n++] = Math.Abs(plane[y * _width + x + 1] - plane[y * _width + x]);
                    }
                }
            }
            if (n < MinNoisePairs)
            {
                return double.NaN;
            }
            return 1.4826 * StatisticsHelper.NthSmallest(diffs[..n], n / 2) / Math.Sqrt(2.0);
        }

        // When a subtracted star's core in one channel sits more than HoleSigma (sigma over root n) below that channel's
        // median in an annulus beyond the star (sky): the radius out to which the plate is still below its sky (rings
        // averaging under -0.5 sigma). Null when there is no hole.
        private double? HoleRadius(in Fit f, float[] plane, double core, double annulus, double sky, double sigma)
        {
            var z = RingMean(plane, f.X, f.Y, 0.0, core, sky, sigma, out var n) * Math.Sqrt(n);
            // A core above its sky is a remnant (the glow disc a bright star's halo left); refilled like a hole on stars
            // bright enough to leave one, where on a faint star a positive core is as likely the structure under it.
            var remnant = z > HoleSigma && f.Significance >= RemnantSigma;
            if (n == 0 || (z >= -HoleSigma && !remnant))
            {
                return null;
            }
            var radius = core;
            for (var ring = Math.Ceiling(core); ring < annulus; ring += 1.0)
            {
                radius = ring + 1.0;
                var ringMean = RingMean(plane, f.X, f.Y, ring, ring + 1.0, sky, sigma, out var m);
                if (m == 0 || (remnant ? ringMean <= 0.5 : ringMean >= -0.5))
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

        // The worst channel's core, filled or not, in sigma over root n against its own annulus sky: the sweep's own test
        // (its core per channel, a saturated star's plateau), so the report counts what the sweep could see.
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
                var core = Math.Max(Math.Max(1.5, psf.Fwhm), f.Saturated ? holeRadius : 0.0);
                var annulus = SkyAnnulus(ReachAt(f, sigma, c) + 2.0, core + 2.0);
                var sky = AnnulusMedianPlane(_work[c], f.X, f.Y, annulus, annulus + 6.0, _frame);
                if (!double.IsFinite(sky))
                {
                    continue;
                }
                var mean = RingMean(_work[c], f.X, f.Y, 0.0, core, sky, sigma, out var n);
                if (n > 0)
                {
                    worst = Math.Min(worst, mean * Math.Sqrt(n));
                }
            }
            return double.IsFinite(worst) ? worst : double.NaN;
        }

        /// <summary>A square window of a plane about a star: its pixels, their weights and their coordinates.</summary>
        private sealed record Window(int CentreX, int CentreY, int Radius, int[] Xs, int[] Ys, double[] Values, double[] Weights);

        // Cut to the ground (#2378): a window that reaches past it lacks those rows and columns, as one at the frame's edge does.
        private Window? CutWindow(PointSource s, double radius, double saturationCut, in PixelRect ground, double? atX = null, double? atY = null, float[]? plane = null)
        {
            plane ??= _lum;
            var r = (int)Math.Clamp(Math.Ceiling(radius) + 2, Math.Ceiling(2.5 * lumPsf.Fwhm), MaxFitRadius);
            var cx = (int)Math.Round(atX ?? s.X);
            var cy = (int)Math.Round(atY ?? s.Y);
            // The window's rows and columns are known before a pixel is read, so its four arrays are made at their size.
            // Grown as lists and then copied, they cost three times that for every window of every star (#2345).
            var box = Square(ground, cx, cy, r, write: false);
            var (top, bottom) = (box.Top, box.Bottom - 1);
            var (left, right) = (box.Left, box.Right - 1);
            var count = box.Width * box.Height;
            var xs = new int[count];
            var ys = new int[count];
            var vs = new double[count];
            var ws = new double[count];
            var weighted = 0;
            var k = 0;
            for (var y = top; y <= bottom; y++)
            {
                for (var x = left; x <= right; x++)
                {
                    var i = y * _width + x;
                    var v = plane[i];
                    // Saturation is judged on the luminance even when a channel is being solved.
                    var usable = float.IsFinite(v) && !IsAbsent(x, y) && _lum[i] < saturationCut;
                    xs[k] = x;
                    ys[k] = y;
                    vs[k] = usable ? v : 0.0;
                    ws[k] = usable ? 1.0 : 0.0;
                    weighted += usable ? 1 : 0;
                    k++;
                }
            }
            return weighted >= 12 ? new Window(cx, cy, r, xs, ys, vs, ws) : null;
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

        /// <summary>Two stars of one PSF on a plane of sky.</summary>
        private readonly record struct PairResult(double A1, double A2, double Sky, double SkyX, double SkyY, double Cost);

        // A blend the matched filter merged: two stars at the field's PSF, started either side of the blob's centroid
        // along its long axis (two equal points 2d apart have second moments sigma^2 + d^2 and sigma^2), their centres by
        // Levenberg-Marquardt with both amplitudes and the sky solved linearly. Taken only where both are real stars at
        // a sane separation and the pair explains the window decisively better than one star at the field's width and no
        // worse than the wide fit that called the source a knot; the pair is then subtracted, its fainter star as the
        // fit's companion. Null otherwise, and the source stays a knot.
        private Fit? FitPair(PointSource s, LinearResult wide, double sigma, in PixelRect ground)
        {
            var fwhmPx = LumPsf(1.0).Fwhm;
            var radius = Math.Max(wide.Radius, 4.0 * fwhmPx);
            if (CutWindow(s, radius, double.PositiveInfinity, ground, wide.X, wide.Y) is not { } window
                || FitNonlinear(window, _lumAlpha, _beta, wide.X, wide.Y, fitBeta: false, fitWidth: false, corr: _correction) is not { } single)
            {
                return null;
            }
            var near = 2.0 * fwhmPx;
            double sw = 0, sx = 0, sy = 0;
            for (var i = 0; i < window.Values.Length; i++)
            {
                var dx = window.Xs[i] - wide.X;
                var dy = window.Ys[i] - wide.Y;
                if (window.Weights[i] <= 0 || dx * dx + dy * dy > near * near)
                {
                    continue;
                }
                var v = Math.Max(0.0, window.Values[i] - single.SkyAt(window.Xs[i], window.Ys[i]));
                sw += v;
                sx += v * window.Xs[i];
                sy += v * window.Ys[i];
            }
            if (!(sw > 0))
            {
                return null;
            }
            var mx = sx / sw;
            var my = sy / sw;
            double cxx = 0, cyy = 0, cxy = 0;
            for (var i = 0; i < window.Values.Length; i++)
            {
                var dx = window.Xs[i] - wide.X;
                var dy = window.Ys[i] - wide.Y;
                if (window.Weights[i] <= 0 || dx * dx + dy * dy > near * near)
                {
                    continue;
                }
                var v = Math.Max(0.0, window.Values[i] - single.SkyAt(window.Xs[i], window.Ys[i]));
                cxx += v * (window.Xs[i] - mx) * (window.Xs[i] - mx);
                cyy += v * (window.Ys[i] - my) * (window.Ys[i] - my);
                cxy += v * (window.Xs[i] - mx) * (window.Ys[i] - my);
            }
            cxx /= sw;
            cyy /= sw;
            cxy /= sw;
            // The two eigenvalues differ by twice this; that difference is d^2 for a pair 2d apart.
            var spread = Math.Sqrt(0.25 * (cxx - cyy) * (cxx - cyy) + cxy * cxy);
            var d = Math.Sqrt(2.0 * spread);
            if (d < 0.25 * fwhmPx)
            {
                return null;
            }
            var angle = 0.5 * Math.Atan2(2.0 * cxy, cxx - cyy);
            var ux = Math.Cos(angle);
            var uy = Math.Sin(angle);
            var psf = LumPsf(1.0);
            void Residuals(ReadOnlySpan<double> p, Span<double> dst)
            {
                var r = SolvePair(window, psf, p[0], p[1], p[2], p[3], _correction, dst);
                if (!double.IsFinite(r.Cost))
                {
                    dst.Fill(1e6);
                }
            }
            var fitted = LevenbergMarquardt.Fit(
                new[] { mx + d * ux, my + d * uy, mx - d * ux, my - d * uy }, window.Values.Length, Residuals,
                new[] { 0.02, 0.02, 0.02, 0.02 }, maxIterations: 40, relativeTolerance: 1e-7);
            var q = fitted.Parameters;
            if (!q.All(double.IsFinite))
            {
                return null;
            }
            var pair = SolvePair(window, psf, q[0], q[1], q[2], q[3], _correction);
            var separation = Math.Sqrt((q[0] - q[2]) * (q[0] - q[2]) + (q[1] - q[3]) * (q[1] - q[3]));
            var used = Math.Max(1, window.Weights.Count(static v => v > 0));
            var noise2 = Math.Max(pair.Cost * 2.0 / used, 1e-30);
            if (!(pair.A1 > 3.0 * sigma && pair.A2 > 3.0 * sigma)
                || separation < 0.5 * fwhmPx || separation > 3.0 * fwhmPx
                || Math.Abs(q[0] - wide.X) > 2.5 * fwhmPx || Math.Abs(q[1] - wide.Y) > 2.5 * fwhmPx
                || Math.Abs(q[2] - wide.X) > 2.5 * fwhmPx || Math.Abs(q[3] - wide.Y) > 2.5 * fwhmPx
                || (single.Cost - pair.Cost) * 2.0 / noise2 < 25.0
                || pair.Cost > wide.Cost)
            {
                return null;
            }
            var (px, py, cx2, cy2) = pair.A1 >= pair.A2 ? (q[0], q[1], q[2], q[3]) : (q[2], q[3], q[0], q[1]);
            var fit = new Fit
            {
                X = px,
                Y = py,
                Width = 1.0,
                Beta = _beta,
                Amplitude = Math.Max(pair.A1, pair.A2),
                Sky = pair.Sky,
                SkyX = pair.SkyX,
                SkyY = pair.SkyY,
                WindowX = window.CentreX,
                WindowY = window.CentreY,
                FitRadius = window.Radius,
                Significance = s.Significance,
                Plateau = double.NaN,
                CompanionX = cx2,
                CompanionY = cy2,
                ChannelAmplitudes = new double[_channels],
                CompanionAmplitudes = new double[_channels],
                Ground = ground,
            };
            fit.SubtractRadius = (int)Math.Clamp(
                Math.Ceiling(Math.Max(psf.RadiusAtLevel(fit.Amplitude, 0.1 * sigma), _correction is { } table ? table.Reach : 0.0)),
                window.Radius, MaxSubtractRadius);
            for (var c = 0; c < _channels; c++)
            {
                if (CutWindow(s, window.Radius, double.PositiveInfinity, ground, px, py, _work[c]) is { } cw)
                {
                    var r = SolvePair(cw, ChannelPsf(c, 1.0), px, py, cx2, cy2, ChannelCorrection(c));
                    fit.ChannelAmplitudes[c] = double.IsFinite(r.A1) ? r.A1 : 0.0;
                    fit.CompanionAmplitudes[c] = double.IsFinite(r.A2) ? r.A2 : 0.0;
                }
            }
            // The pair passes the same overshoot guard as a single fit (#1867): either star past OvershootMargin times its channel's
            // ceiling is a pair that ran away, not two stars, and the source stays a knot.
            if (AmplitudeOvershoots(fit.ChannelAmplitudes, _fullScale) || AmplitudeOvershoots(fit.CompanionAmplitudes, _fullScale))
            {
                return null;
            }
            fit.Outcome = StarFitOutcome.Subtracted;
            AddModel(fit, -1);
            return fit;
        }

        // Both amplitudes, the sky level and its slope by weighted least squares for two fixed centres of one PSF;
        // writes the weighted residuals into destination when given. NaN amplitudes when the system is singular.
        private static PairResult SolvePair(Window w, MoffatPsf psf, double x1, double y1, double x2, double y2, RadialCorrection? corr, Span<double> destination = default)
        {
            const int M = 5;
            Span<double> ata = stackalloc double[M * M];
            Span<double> atb = stackalloc double[M];
            Span<double> row = stackalloc double[M];
            ata.Clear();
            atb.Clear();
            var n = w.Values.Length;
            // A solve is one evaluation of a fit and a fit evaluates some hundreds of times a star, so rows made per solve
            // were most of what the remover allocated (#2345). The rented rows are NOT cleared: b1 and b2 are written before
            // they are read, and both only where the pixel's weight is above zero.
            using var rows = ArrayPoolHelper.Rent<double>(2 * n);
            var b1 = rows.AsSpan(0, n);
            var b2 = rows.AsSpan(n, n);
            for (var i = 0; i < n; i++)
            {
                if (w.Weights[i] <= 0)
                {
                    continue;
                }
                b1[i] = corr is null ? psf.PixelMean(w.Xs[i], w.Ys[i], x1, y1) : corr.Model(psf, w.Xs[i], w.Ys[i], x1, y1);
                b2[i] = corr is null ? psf.PixelMean(w.Xs[i], w.Ys[i], x2, y2) : corr.Model(psf, w.Xs[i], w.Ys[i], x2, y2);
                row[0] = b1[i];
                row[1] = b2[i];
                row[2] = 1.0;
                row[3] = w.Xs[i] - w.CentreX;
                row[4] = w.Ys[i] - w.CentreY;
                for (var a = 0; a < M; a++)
                {
                    atb[a] += row[a] * w.Values[i];
                    for (var b = 0; b < M; b++)
                    {
                        ata[a * M + b] += row[a] * row[b];
                    }
                }
            }
            Span<double> sol = stackalloc double[M];
            if (!Solve(ata, atb, sol, M))
            {
                return new PairResult(double.NaN, double.NaN, double.NaN, 0, 0, double.NaN);
            }
            var cost = 0.0;
            for (var i = 0; i < n; i++)
            {
                var resid = 0.0;
                if (w.Weights[i] > 0)
                {
                    resid = w.Values[i] - (sol[0] * b1[i] + sol[1] * b2[i] + sol[2] + sol[3] * (w.Xs[i] - w.CentreX) + sol[4] * (w.Ys[i] - w.CentreY));
                    cost += resid * resid;
                }
                if (!destination.IsEmpty)
                {
                    destination[i] = resid;
                }
            }
            return new PairResult(sol[0], sol[1], sol[2], sol[3], sol[4], 0.5 * cost);
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
            // Rented, not cleared (see SolvePair, #2345): basis[i] is written before it is read, only where the weight is above zero.
            using var rows = ArrayPoolHelper.Rent<double>(n);
            var basis = rows.AsSpan();
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
                var r = (int)Math.Ceiling(ReachAt(f, 0.1 * sigma));
                StampDisc(mask, cx, cy, Math.Min(r, f.SubtractRadius));
                if (f.CompanionAmplitudes is not null)
                {
                    StampDisc(mask, (int)Math.Round(f.CompanionX), (int)Math.Round(f.CompanionY), Math.Min(r, f.SubtractRadius));
                }
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
                // A profile star's model is its own light, so only its residual where it dominates is a failure worth
                // filling, judged against the plate around that: flagged out to its full reach against the far sky its
                // profile was measured from, the M42 nebula under an Orion star was filled as a disc 110 px across.
                var profileStar = f.Profile is not null;
                var reach = profileStar
                    ? Math.Max(ReachAt(f, ProfileFlagSigma * sigma), f.PlateauRadius + 1.5 * psf.Fwhm)
                    : Math.Max(ReachAt(f, sigma), 1.5 * psf.Fwhm);
                if (f.CompanionAmplitudes is not null)
                {
                    reach += Math.Sqrt((f.CompanionX - f.X) * (f.CompanionX - f.X) + (f.CompanionY - f.Y) * (f.CompanionY - f.Y));
                }
                var around = profileStar ? AnnulusMedian(f.X, f.Y, reach, reach + 4.0, _frame) : double.NaN;
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
                        var ownSky = profileStar ? around : f.Sky + f.SkyX * (x - f.WindowX) + f.SkyY * (y - f.WindowY);
                        var shift = _rms[i] > 0 ? (_sky[i] - ownSky) / _rms[i] : 0.0;
                        var ownSmoothed = _rms[i] > 0 ? smoothed[i] + shift : 0.0;
                        // And pixel by pixel: a bright star's core misfit alternates sign from pixel to pixel, which the
                        // three-pixel mean averages away (eta Car's 132-sigma star left +-3,000 ADU, nine sigma, unflagged).
                        var ownPixel = _rms[i] > 0 ? above[i] + shift : 0.0;
                        if (Math.Abs(ownSmoothed) > threshold || Math.Abs(ownPixel) > PixelFlagSigma)
                        {
                            mask[y, x] = true;
                        }
                    }
                }
                if (f.Saturated)
                {
                    var core = CoreRadius(f, sigma);
                    StampDisc(mask, cx, cy, core + 2.0);
                }
                else if (CoreBelowSky(f, psf, Math.Max(_holeNoise[centre], 1e-12)) is { } holeRadius)
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
            => HoleFill.Fill(_work, _width, _height, holes, _absent, lumPsf.Fwhm, options.Seed, Ceiling(), ct);

        private ImmutableArray<FittedStar> Describe(List<PointSource> sources, List<Fit> fits, int secondFrom, BitMatrix inpainted)
        {
            var stars = ImmutableArray.CreateBuilder<FittedStar>(sources.Count);
            var skyFloor = SkyFloor();
            for (var k = 0; k < sources.Count; k++)
            {
                var s = sources[k];
                var f = fits[k];
                var cx = (int)Math.Round(f.X);
                var cy = (int)Math.Round(f.Y);
                var inside = cx >= 0 && cx < _width && cy >= 0 && cy < _height;
                var sigma = inside ? _rms[cy * _width + cx] : float.NaN;
                var noise = inside ? _holeNoise[cy * _width + cx] : float.NaN;
                var skyAbove = noise > 0 ? (_sky[cy * _width + cx] - skyFloor) / noise : float.NaN;
                var texture = inside ? _texture.At(cx, cy) : float.NaN;
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
                    var holeRadius = f.Saturated ? Math.Max(radius, CoreRadius(f, sigma)) : radius;
                    var annulus = SkyAnnulus(ReachAt(f, sigma) + 2.0, Math.Max(holeRadius + 2.0, 3.0 * psf.Fwhm));
                    var plateSky = AnnulusMedian(f.X, f.Y, annulus, annulus + 6.0, _frame);
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
                var model = f.Outcome != StarFitOutcome.Subtracted ? StarFitModel.None
                    : f.Profile is not null ? StarFitModel.Profile
                    : f.CompanionAmplitudes is not null ? StarFitModel.Pair
                    : StarFitModel.Moffat;
                var amplitudes = model == StarFitModel.None
                    ? ImmutableArray<float>.Empty
                    : f.ChannelAmplitudes.Select(static a => (float)a).ToImmutableArray();
                stars.Add(new FittedStar(
                    (float)f.X, (float)f.Y, s.Significance, (float)f.Amplitude, (float)FwhmRatio(f.Width, f.Beta), (float)f.Sky, sigma,
                    f.Outcome, f.Saturated, touched || (f.Outcome == StarFitOutcome.Subtracted && inside && inpainted[cy, cx]),
                    residual, bias, k >= secondFrom, holeDepth, model, amplitudes, skyAbove, texture));
            }
            return stars.MoveToImmutable();
        }

        // The frame's darkest sky, what FittedStar.SkyAbove is read from: the luminance sky map's 5th percentile over the
        // covered pixels, every fourth pixel each way (the map is smooth over a block far wider than that).
        private float SkyFloor()
        {
            var values = new List<float>((_width / 4 + 1) * (_height / 4 + 1));
            for (var y = 0; y < _height; y += 4)
            {
                for (var x = 0; x < _width; x += 4)
                {
                    var v = _sky[y * _width + x];
                    if (!IsAbsent(x, y) && float.IsFinite(v))
                    {
                        values.Add(v);
                    }
                }
            }
            return values.Count > 0 ? StatisticsHelper.NthSmallest(CollectionsMarshal.AsSpan(values), values.Count / 20) : float.NaN;
        }

        // The core radius when a subtracted star's core, on the working plate, sits more than HoleSigma (in sigma over
        // root n) below the plate's median in an annulus beyond the star; null when it does not.
        private double? CoreBelowSky(in Fit f, MoffatPsf psf, double sigma)
        {
            var radius = Math.Max(1.5, psf.Fwhm);
            var annulus = SkyAnnulus(psf.RadiusAtLevel(f.Amplitude, sigma) + 2.0, radius + 2.0);
            var sky = AnnulusMedian(f.X, f.Y, annulus, annulus + 6.0, _frame);
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

        // The plate's median over an annulus on the ground, absent pixels left out; NaN where nothing is present.
        private double AnnulusMedian(double cx, double cy, double inner, double outer, in PixelRect ground)
            => AnnulusMedianPlane(_lum, cx, cy, inner, outer, ground);

        private double AnnulusMedianPlane(float[] plane, double cx, double cy, double inner, double outer, in PixelRect ground)
        {
            var values = new List<float>();
            var box = Square(ground, (int)Math.Round(cx), (int)Math.Round(cy), (int)Math.Ceiling(outer), write: false);
            for (var y = box.Top; y < box.Bottom; y++)
            {
                for (var x = box.Left; x < box.Right; x++)
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
            return StatisticsHelper.NthSmallest(CollectionsMarshal.AsSpan(values), values.Count / 2);
        }

        // The hole test's false-alarm rate on this plate: the same core aperture and annulus at random places no star
        // touched, the fraction reading below -HoleSigma. A master's noise is correlated, so sigma over root n
        // understates a mean's scatter; holes count only as an excess over this.
        private float HoleNullRate(BitMatrix subtracted, BitMatrix inpainted)
        {
            var rng = new Random(options.Seed);
            var radius = Math.Max(1.5, LumPsf(1.0).Fwhm);
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
                for (var c = 0; c < _channels && !double.IsNaN(worst); c++)
                {
                    var sky = AnnulusMedianPlane(_work[c], cx, cy, annulus, annulus + 6.0, _frame);
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

        private StarlessPlateStatistics Measure(ImmutableArray<FittedStar> stars, BitMatrix inpainted, BitMatrix subtracted, ImmutableArray<float> correlation, int dugCores, Stopwatch sw)
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
            var speckles = StarlessSpeckles.Measure(_lum, _width, _height, _absent,
                [.. stars.Where(static s => s.Outcome == StarFitOutcome.Subtracted).Select(static s => (s.X, s.Y, s.Significance))],
                [.. stars.Select(static s => (s.X, s.Y))], options.Seed, _texture);
            return new StarlessPlateStatistics(
                bands.MoveToImmutable(),
                present > 0 ? (float)inpainted.PopCount() / present : 0f,
                leftovers.Count(l => l.Significance < BandEdges[0]),
                Faint(static r => r < 1.0 / 3.0),
                Faint(static r => r > 2.0 / 3.0),
                fwhmOut, betaOut, (float)_fieldScale, (float)_beta, HoleNullRate(subtracted, inpainted), correlation, sw.Elapsed.TotalSeconds,
                speckles, dugCores);
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
