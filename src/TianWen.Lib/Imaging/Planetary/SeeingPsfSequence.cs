using System;
using System.Numerics;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// The seeing's PSF frame after frame, as <see cref="PlanetaryDegrade"/> makes a synthetic capture's (docs/plans/planetary-restoration.md,
/// R2 and R7): the free air on a screen the wind carries across the pupil, integrated over each exposure in steps of at most a
/// centimetre, a still layer at the telescope on a screen of its own, and the telescope's defocus, on the fine grid of
/// <see cref="PlanetaryDegrade.PsfGrid"/> samples. The twin and whatever reasons about the seeing the twin was made with (the spectral
/// ratio's theory, R7) share this one code, so the two cannot differ.
/// <para>
/// With <see cref="DegradeOptions.HighR0M"/> a third screen, the free air at an altitude (R4 per-point, #1071), is seen by each point of
/// the disk through its own footprint (<see cref="ExposureAt"/>), so the PSF varies over the disk; the two screens at the pupil are
/// common to every point.
/// </para>
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
    // The layer at an altitude, when there is one: its screen at twice the pupil's spacing (its air is coarser than the telescope's),
    // read bilinearly at each point's footprint, and the common phase of every exposure step, set once a frame by Freeze.
    private readonly EvolvingPhaseScreen? _high;
    private readonly double[] _highPhase;
    private readonly int _highSamples;
    private readonly double _highSpacing;
    private readonly double _pupilSpacing;
    private readonly (double X, double Y) _highWind;
    private readonly double _highRenewSeconds;
    private readonly double _highSweepM;
    private readonly int _layeredSteps;
    private readonly double[][] _common;
    // The pupil's transmitting samples and the rows they span: a point's PSF is built and transformed there alone.
    private readonly int[] _support;
    private readonly int _firstRow;
    private readonly int _lastRow;

    /// <param name="options">The air, the telescope and the draws' seed, as <see cref="PlanetaryDegrade.MakeAsync"/> takes them.</param>
    /// <param name="arcsecPerPixel">The detector's scale, which sets the fine grid's.</param>
    /// <param name="freeAirScale">The free air's phase multiplied by this, which is its r0 over (r0 / this^(6/5)): one set of draws
    /// read at several strengths of seeing, for a theory fitted in r0. One leaves the twin's seeing as it is.</param>
    /// <param name="highReachM">The farthest any footprint on the layer at an altitude lies from the disk's centre's, in metres: how
    /// much of its air the screen must hold (<see cref="ExposureAt"/>).</param>
    public SeeingPsfSequence(DegradeOptions options, double arcsecPerPixel, double freeAirScale = 1, double highReachM = 0)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        // The pupil sampled for a PSF of fine samples, and the screen that crosses it.
        var spacing = PlanetaryDegrade.PupilSpacingM(arcsecPerPixel, options);
        _pupil = options.Pupil.Rasterise(PsfGrid, spacing);
        var support = new System.Collections.Generic.List<int>();
        (_firstRow, _lastRow) = (PsfGrid, -1);
        for (var i = 0; i < _pupil.Length; i++)
        {
            if (_pupil[i] != 0)
            {
                support.Add(i);
                (_firstRow, _lastRow) = (Math.Min(_firstRow, i / PsfGrid), Math.Max(_lastRow, i / PsfGrid));
            }
        }
        _support = [.. support];
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
        _localRenewSeconds = _local is null ? double.PositiveInfinity : options.LocalRenewSeconds ?? (_local.SizeM / Math.Max(options.LocalWindMps, 1e-3) / 3);
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

        // The layer at an altitude: a screen large enough for every footprint, the pupil and the exposure's sweep, renewed by the same
        // rule as the others; every footprint shares the exposure's steps, as many as either moving layer needs.
        _pupilSpacing = spacing;
        _highSpacing = 2 * spacing;
        _highSweepM = options.HighWindMps * options.ExposureSeconds;
        _layeredSteps = Math.Max(_subSteps, Math.Max(1, (int)Math.Ceiling(_highSweepM / 0.01)));
        if (options.HasHighLayer)
        {
            var extentM = (2 * highReachM) + (PsfGrid * spacing) + _highSweepM;
            _highSamples = Math.Max(256, PlanetaryDegrade.NextPowerOfTwo((int)Math.Ceiling(extentM / _highSpacing) + 4));
            _high = new EvolvingPhaseScreen(_highSamples, _highSpacing, options.HighR0M, new Random(options.Seed + 3), options.HighOuterScaleM);
            _highPhase = new double[_highSamples * _highSamples];
            _highRenewSeconds = _high.SizeM / Math.Max(options.HighWindMps, 1e-3) / 3;
            _highWind = (options.HighWindMps * Math.Cos(options.HighWindAngleDeg * Math.PI / 180), options.HighWindMps * Math.Sin(options.HighWindAngleDeg * Math.PI / 180));
            _common = new double[_layeredSteps][];
            for (var step = 0; step < _layeredSteps; step++)
            {
                _common[step] = new double[PsfGrid * PsfGrid];
            }
        }
        else
        {
            _highPhase = [];
            _common = [];
        }
    }

    /// <summary>The perfect telescope's PSF peak on this grid, which a frame's Strehl ratio is taken against.</summary>
    public double DiffractionPeak { get; }

    /// <summary>Both screens moved on <paramref name="seconds"/>, as the air is between one frame and the next.</summary>
    public void Step(double seconds)
    {
        _screen.Step(_wind.X, _wind.Y, seconds, Math.Exp(-seconds / _renewSeconds));
        _local?.Step(_localWind.X, _localWind.Y, seconds, Math.Exp(-seconds / _localRenewSeconds));
        _high?.Step(_highWind.X, _highWind.Y, seconds, Math.Exp(-seconds / _highRenewSeconds));
    }

    /// <summary>
    /// The screens as they are now, held for <see cref="ExposureAt"/> until the next <see cref="Step"/>: the layer at an altitude, and
    /// the phase every point shares at each step of the exposure (the free air at the pupil, the still layer and the defocus).
    /// </summary>
    public void Freeze()
    {
        if (_high is null)
        {
            throw new InvalidOperationException("There is no layer at an altitude to look through.");
        }
        _screen.Fill(_screenPhase);
        _local?.Fill(_localPhase);
        _high.Fill(_highPhase);
        var angle = _options.WindAngleDeg * Math.PI / 180;
        for (var step = 0; step < _layeredSteps; step++)
        {
            var along = _layeredSteps == 1 ? 0 : (_sweepM * (((step + 0.5) / _layeredSteps) - 0.5)) / _screenSpacing;
            var offsetX = _screenMargin - (int)Math.Round(along * Math.Cos(angle));
            var offsetY = _screenMargin - (int)Math.Round(along * Math.Sin(angle));
            var common = _common[step];
            foreach (var i in _support)
            {
                var (y, x) = (i / PsfGrid, i % PsfGrid);
                var (air, still) = _stride == 1
                    ? (_screenPhase[((y + offsetY) * _screenSamples) + x + offsetX], _local is null ? 0 : _localPhase[((y + _screenMargin) * _screenSamples) + x + _screenMargin])
                    : (PlanetaryDegrade.ScreenAt(_screenPhase, _screenSamples, offsetX + (x * _stride), offsetY + (y * _stride)),
                        _local is null ? 0 : PlanetaryDegrade.ScreenAt(_localPhase, _screenSamples, _screenMargin + (x * _stride), _screenMargin + (y * _stride)));
                common[i] = (air * _airScale) + _defocus[i] + (still * _phaseScale);
            }
        }
    }

    /// <summary>
    /// The PSF over one exposure seen by the point of the disk whose footprint on the layer at an altitude lies
    /// (<paramref name="footprintXM"/>, <paramref name="footprintYM"/>) metres from the disk's centre's, into <paramref name="psf"/>: the
    /// common phase of each step of the exposure (<see cref="Freeze"/>) with that footprint's air, the layer's own wind sweeping it.
    /// Safe from any thread with a <paramref name="scratch"/> of its own, between one <see cref="Freeze"/> and the next <see cref="Step"/>.
    /// </summary>
    public void ExposureAt(double footprintXM, double footprintYM, double[] psf, ExposureScratch scratch)
    {
        ArgumentNullException.ThrowIfNull(psf);
        ArgumentNullException.ThrowIfNull(scratch);
        if (_high is null)
        {
            throw new InvalidOperationException("There is no layer at an altitude to look through.");
        }
        Array.Clear(psf);
        var angle = _options.HighWindAngleDeg * Math.PI / 180;
        var ratio = _pupilSpacing / _highSpacing;
        var centre = _highSamples / 2.0;
        var field = scratch.Field;
        for (var step = 0; step < _layeredSteps; step++)
        {
            var along = _layeredSteps == 1 ? 0 : _highSweepM * (((step + 0.5) / _layeredSteps) - 0.5);
            var originX = centre + ((footprintXM - (along * Math.Cos(angle))) / _highSpacing) - (PsfGrid / 2 * ratio);
            var originY = centre + ((footprintYM - (along * Math.Sin(angle))) / _highSpacing) - (PsfGrid / 2 * ratio);
            // The pupil's samples on the periodic screen, bilinear and separable: each column's and row's two samples and weight once.
            Neighbours(originX, ratio, _highSamples, scratch.Columns, scratch.ColumnWeights);
            Neighbours(originY, ratio, _highSamples, scratch.Rows, scratch.RowWeights);
            var common = _common[step];
            // The pupil's field, |FT|^2 of it as ShortExposurePsf takes it, built where the pupil transmits and transformed on its rows.
            Array.Clear(field);
            foreach (var i in _support)
            {
                var (y, x) = (i / PsfGrid, i % PsfGrid);
                var (top, bottom, wy) = (scratch.Rows[2 * y] * _highSamples, scratch.Rows[(2 * y) + 1] * _highSamples, scratch.RowWeights[y]);
                var (left, right, wx) = (scratch.Columns[2 * x], scratch.Columns[(2 * x) + 1], scratch.ColumnWeights[x]);
                var upper = (_highPhase[top + left] * (1 - wx)) + (_highPhase[top + right] * wx);
                var lower = (_highPhase[bottom + left] * (1 - wx)) + (_highPhase[bottom + right] * wx);
                field[i] = Complex.FromPolarCoordinates(_pupil[i], common[i] + ((((1 - wy) * upper) + (wy * lower)) * _phaseScale));
            }
            for (var y = _firstRow; y <= _lastRow; y++)
            {
                ComplexFft.Forward(field.AsSpan(y * PsfGrid, PsfGrid));
            }
            var column = scratch.Column;
            for (var x = 0; x < PsfGrid; x++)
            {
                for (var y = 0; y < PsfGrid; y++)
                {
                    column[y] = field[(y * PsfGrid) + x];
                }
                ComplexFft.Forward(column);
                for (var y = 0; y < PsfGrid; y++)
                {
                    field[(y * PsfGrid) + x] = column[y];
                }
            }
            // The zero frequency moved to the grid's centre and each step's PSF a unit sum, as ShortExposurePsf leaves one.
            var sub = scratch.SubPsf;
            var (half, sum) = (PsfGrid / 2, 0.0);
            for (var y = 0; y < PsfGrid; y++)
            {
                var sy = (y + half) % PsfGrid;
                for (var x = 0; x < PsfGrid; x++)
                {
                    var value = field[(y * PsfGrid) + x];
                    var power = (value.Real * value.Real) + (value.Imaginary * value.Imaginary);
                    sub[(sy * PsfGrid) + ((x + half) % PsfGrid)] = power;
                    sum += power;
                }
            }
            var scale = sum > 0 ? 1 / (sum * _layeredSteps) : 0;
            for (var i = 0; i < psf.Length; i++)
            {
                psf[i] += sub[i] * scale;
            }
        }
    }

    /// <summary>What one thread needs to make <see cref="ExposureAt"/>'s PSFs.</summary>
    public sealed class ExposureScratch
    {
        internal double[] SubPsf { get; } = new double[PsfGrid * PsfGrid];

        internal Complex[] Column { get; } = new Complex[PsfGrid];

        internal Complex[] Field { get; } = new Complex[PsfGrid * PsfGrid];

        internal int[] Columns { get; } = new int[2 * PsfGrid];

        internal double[] ColumnWeights { get; } = new double[PsfGrid];

        internal int[] Rows { get; } = new int[2 * PsfGrid];

        internal double[] RowWeights { get; } = new double[PsfGrid];
    }

    // Where each of the pupil's samples along one axis falls on a periodic screen of `n` samples, from `origin` in steps of `ratio`:
    // the two screen samples either side (wrapped) and the weight of the second.
    private static void Neighbours(double origin, double ratio, int n, int[] indices, double[] weights)
    {
        for (var i = 0; i < weights.Length; i++)
        {
            var at = origin + (i * ratio);
            var below = (int)Math.Floor(at);
            (indices[2 * i], indices[(2 * i) + 1], weights[i]) = (Wrap(below, n), Wrap(below + 1, n), at - below);
        }
    }

    private static int Wrap(int i, int n) => ((i % n) + n) % n;

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
