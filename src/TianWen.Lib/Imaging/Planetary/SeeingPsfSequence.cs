using System;
using System.Numerics;
using TianWen.Lib.Imaging.Optics;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// The seeing's PSF frame after frame, as <see cref="PlanetaryDegrade"/> makes a synthetic capture's (docs/plans/planetary-restoration.md,
/// R2 and R7): the free air on a screen the wind carries across the pupil, integrated over each exposure in steps of at most a
/// centimetre, a still layer at the telescope on a screen of its own, and the telescope's defocus, on the fine grid of
/// <see cref="PlanetaryDegrade.PsfGrid"/> samples. The twin and whatever reasons about the seeing the twin was made with (the spectral
/// ratio's theory, R7) share this one code, so the two cannot differ.
/// </summary>
internal sealed class SeeingPsfSequence
{
    private const int PsfGrid = PlanetaryDegrade.PsfGrid;
    private readonly DegradeOptions _options;
    private readonly float[] _pupil;
    private readonly double[] _defocus;
    private readonly EvolvingPhaseScreen _screen;
    private readonly EvolvingPhaseScreen? _local;
    private readonly double[] _screenPhase;
    private readonly double[] _localPhase;
    private readonly double[] _phase = new double[PsfGrid * PsfGrid];
    private readonly Complex[] _scratch = new Complex[PsfGrid * PsfGrid];
    private readonly double[] _subPsf = new double[PsfGrid * PsfGrid];
    private readonly int _screenSamples;
    private readonly double _stride;
    private readonly int _screenMargin;
    private readonly int _subSteps;
    private readonly double _sweepM;
    private readonly double _screenSpacing;
    private readonly double _airScale;
    private readonly double _phaseScale;
    private readonly (double X, double Y) _wind;
    private readonly (double X, double Y) _localWind;
    private readonly double _renewSeconds;
    private readonly double _localRenewSeconds;

    /// <param name="options">The air, the telescope and the draws' seed, as <see cref="PlanetaryDegrade.MakeAsync"/> takes them.</param>
    /// <param name="arcsecPerPixel">The detector's scale, which sets the fine grid's.</param>
    /// <param name="freeAirScale">The free air's phase multiplied by this, which is its r0 over (r0 / this^(6/5)): one set of draws
    /// read at several strengths of seeing, for a theory fitted in r0. One leaves the twin's seeing as it is.</param>
    public SeeingPsfSequence(DegradeOptions options, double arcsecPerPixel, double freeAirScale = 1)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        // The pupil sampled for a PSF of fine samples, and the screen that crosses it.
        var spacing = PlanetaryDegrade.PupilSpacingM(arcsecPerPixel, options);
        _pupil = options.Pupil.Rasterise(PsfGrid, spacing);
        _defocus = PlanetaryDegrade.DefocusPhase(PsfGrid, spacing, options.Pupil.DiameterM, options.DefocusNm * 1e-9, options.WavelengthM);
        var diffraction = new double[PsfGrid * PsfGrid];
        // The diffraction limit the Strehl ratio is taken against is the perfect telescope's, so a defocused one scores below 1.
        ShortExposurePsf.Compute(_pupil, ReadOnlySpan<double>.Empty, PsfGrid, diffraction);
        var peak = 0.0;
        foreach (var v in diffraction)
        {
            peak = Math.Max(peak, v);
        }
        DiffractionPeak = peak;
        // The screens' own spacing: the pupil's, or the one the colours of one atmosphere share, which a pupil then samples at its
        // own, `stride` screen samples to one of its own.
        var screenSpacing = options.ScreenSpacingM ?? spacing;
        _stride = spacing / screenSpacing;
        // On the pupil's own spacing the screen is what it always was (a mono capture made before is made again, sample for
        // sample); on a shared one it must hold the pupil's extent at the stride and a neighbour for the interpolation.
        _screenSamples = _stride == 1 ? Math.Max(options.ScreenSamples, PsfGrid) : Math.Max(options.ScreenSamples, PlanetaryDegrade.NextPowerOfTwo((int)Math.Ceiling(PsfGrid * _stride) + 2));
        _screen = new EvolvingPhaseScreen(_screenSamples, screenSpacing, options.R0M, new Random(options.Seed), options.OuterScaleM);
        // Phase in radians at 500 nm, where r0 is stated, scaled to the imaging wavelength (the path difference is achromatic).
        _phaseScale = 500e-9 / options.WavelengthM;
        _airScale = _phaseScale * freeAirScale;
        _wind = (options.WindMps * Math.Cos(options.WindAngleDeg * Math.PI / 180), options.WindMps * Math.Sin(options.WindAngleDeg * Math.PI / 180));
        // The periodic screen comes round again after its side over the wind; it is renewed three e-folds in that time.
        _renewSeconds = _screen.SizeM / Math.Max(options.WindMps, 1e-3) / 3;
        // The layer at the telescope, on a screen of its own, the same size (its outer scale is well inside it); the same rule
        // for its renewal, so a still one is the same air throughout.
        _local = double.IsFinite(options.LocalR0M)
            ? new EvolvingPhaseScreen(_screenSamples, screenSpacing, options.LocalR0M, new Random(options.Seed + 2), options.LocalOuterScaleM)
            : null;
        _localPhase = _local is null ? [] : new double[_screenSamples * _screenSamples];
        _localRenewSeconds = _local is null ? double.PositiveInfinity : _local.SizeM / Math.Max(options.LocalWindMps, 1e-3) / 3;
        _localWind = (options.LocalWindMps * Math.Cos((options.WindAngleDeg + 90) * Math.PI / 180), options.LocalWindMps * Math.Sin((options.WindAngleDeg + 90) * Math.PI / 180));
        _screenPhase = new double[_screenSamples * _screenSamples];

        // The exposure in frozen-flow steps of at most a centimetre: the pupil's window slides across the same screen, the air
        // being the same air within a frame.
        var sweepM = options.WindMps * options.ExposureSeconds;
        _subSteps = Math.Max(1, (int)Math.Ceiling(sweepM / 0.01));
        (_sweepM, _screenSpacing) = (sweepM, screenSpacing);
        // A pupil on its own spacing reads whole samples; one on the shared spacing needs a neighbour for its interpolation.
        _screenMargin = _stride == 1 ? (_screenSamples - PsfGrid) / 2 : (_screenSamples - (int)Math.Ceiling(PsfGrid * _stride) - 1) / 2;
        if (sweepM / screenSpacing > _screenMargin)
        {
            throw new ArgumentException($"The exposure sweeps {sweepM:0.000} m of air, more than the screen's margin of {_screenMargin * screenSpacing:0.000} m: use a larger screen.", nameof(options));
        }
    }

    /// <summary>The perfect telescope's PSF peak on this grid, which a frame's Strehl ratio is taken against.</summary>
    public double DiffractionPeak { get; }

    /// <summary>Both screens moved on <paramref name="seconds"/>, as the air is between one frame and the next.</summary>
    public void Step(double seconds)
    {
        _screen.Step(_wind.X, _wind.Y, seconds, Math.Exp(-seconds / _renewSeconds));
        _local?.Step(_localWind.X, _localWind.Y, seconds, Math.Exp(-seconds / _localRenewSeconds));
    }

    /// <summary>
    /// The PSF over one exposure of the air as it is now, into <paramref name="psf"/> (<see cref="PlanetaryDegrade.PsfGrid"/> squared
    /// samples, centred on sample PsfGrid / 2 in each axis): the pupil's window stepped upwind across the screen as the air moves past
    /// it, each step's PSF averaged.
    /// </summary>
    public void Exposure(double[] psf)
    {
        ArgumentNullException.ThrowIfNull(psf);
        _screen.Fill(_screenPhase);
        _local?.Fill(_localPhase);
        Array.Clear(psf);
        var angle = _options.WindAngleDeg * Math.PI / 180;
        for (var step = 0; step < _subSteps; step++)
        {
            var along = _subSteps == 1 ? 0 : (_sweepM * (((step + 0.5) / _subSteps) - 0.5)) / _screenSpacing;
            var offsetX = _screenMargin - (int)Math.Round(along * Math.Cos(angle));
            var offsetY = _screenMargin - (int)Math.Round(along * Math.Sin(angle));
            for (var y = 0; y < PsfGrid; y++)
            {
                for (var x = 0; x < PsfGrid; x++)
                {
                    // The pupil's own spacing on the screen's: the one screen read at each colour's sampling of the pupil,
                    // exactly the screen's samples where the two are one.
                    var (air, still) = _stride == 1
                        ? (_screenPhase[((y + offsetY) * _screenSamples) + x + offsetX], _local is null ? 0 : _localPhase[((y + _screenMargin) * _screenSamples) + x + _screenMargin])
                        : (PlanetaryDegrade.ScreenAt(_screenPhase, _screenSamples, offsetX + (x * _stride), offsetY + (y * _stride)),
                            _local is null ? 0 : PlanetaryDegrade.ScreenAt(_localPhase, _screenSamples, _screenMargin + (x * _stride), _screenMargin + (y * _stride)));
                    _phase[(y * PsfGrid) + x] = (air * _airScale) + _defocus[(y * PsfGrid) + x] + (still * _phaseScale);
                }
            }
            ShortExposurePsf.Compute(_pupil, _phase, PsfGrid, _subPsf, _scratch);
            for (var i = 0; i < _subPsf.Length; i++)
            {
                psf[i] += _subPsf[i] / _subSteps;
            }
        }
    }

}
