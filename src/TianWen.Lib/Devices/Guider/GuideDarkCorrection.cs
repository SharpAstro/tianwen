using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Devices.Guider;

/// <summary>One master dark of a guide camera: the state it was shot in and its pixels.</summary>
/// <param name="Exposure">The exposure of every frame it was combined from.</param>
/// <param name="Gain">The gain register it was shot at, or -1 when unknown.</param>
/// <param name="TemperatureC">The sensor temperature, NaN when the camera does not report one.</param>
/// <param name="Master">The combined dark. Owned by the correction, never released by it.</param>
public sealed record GuideDark(TimeSpan Exposure, short Gain, double TemperatureC, Image Master);

/// <summary>
/// Subtracts a guide camera's dark and masks its bad pixels, in a NEW frame, before a star is measured or the frame is shown
/// (#1915). The one routine for the guide-camera loop and the built-in guider's own frames.
/// </summary>
/// <remarks>
/// <para><b>The dark is chosen by the exposure first</b> (a dark's level is dark current times time), then the gain, which must
/// agree wherever both are known, then the sensor temperature as the tie-break. A dark of another exposure is scaled
/// about its own level (its pedestal is not dark current and does not scale), within a factor of
/// <see cref="MaxExposureRatio"/>; beyond that no dark serves the frame and it is only masked.</para>
/// <para><b>The bad pixels are the darks' own</b> (<see cref="BadPixelDetection.BuildMaskFromDark"/>: a photosite is judged
/// against its same-colour neighbours, never a frame-wide threshold), and are replaced from those neighbours
/// (<see cref="BadPixelDetection.WithMaskedFromNeighbours"/>) so a hot pixel is never a star.</para>
/// <para><b>Ownership:</b> <see cref="Correct"/> CONSUMES the frame it is given and returns the one to use, so the camera's
/// buffer goes back as soon as the corrected copy exists. With nothing to apply it returns the frame it was given.</para>
/// </remarks>
public sealed class GuideDarkCorrection
{
    /// <summary>How far a dark's exposure may be from the frame's, as a ratio either way, before it no longer serves it.</summary>
    public const double MaxExposureRatio = 4.0;

    /// <summary>Gaussian sigmas above the neighbourhood that a dark's photosite is a defect (the stacker's default).</summary>
    public const float MaskSigma = 8f;

    private const double TemperatureWeightPerC = 0.02;
    private readonly ImmutableArray<GuideDark> _darks;
    private readonly BitMatrix[]? _badPixels;
    private readonly Dictionary<GuideDark, float[]> _levels = [];

    /// <param name="darks">The master darks; empty for a correction that masks only.</param>
    /// <param name="badPixels">The union of the darks' masks, one per channel, or null for none.</param>
    public GuideDarkCorrection(ImmutableArray<GuideDark> darks, BitMatrix[]? badPixels)
    {
        _darks = darks.IsDefault ? [] : darks;
        _badPixels = badPixels;
        foreach (var dark in _darks)
        {
            _levels[dark] = ChannelLevels(dark.Master);
        }
    }

    /// <summary>Builds one from master darks, masking with the rules the stacker's own mask uses.</summary>
    public static GuideDarkCorrection FromDarks(ImmutableArray<GuideDark> darks)
    {
        BitMatrix[]? union = null;
        foreach (var dark in darks)
        {
            if (BadPixelDetection.BuildMaskFromDark(dark.Master, MaskSigma) is not { } mask)
            {
                continue;
            }
            if (union is null)
            {
                union = mask;
                continue;
            }
            if (union.Length != mask.Length)
            {
                continue;
            }
            for (var c = 0; c < mask.Length; c++)
            {
                BadPixelAccumulator.UnionInto(union[c], mask[c], dark.Master.Width, dark.Master.Height);
            }
        }
        return new GuideDarkCorrection(darks, union);
    }

    /// <summary>How many master darks the correction holds.</summary>
    public int DarkCount => _darks.Length;

    /// <summary>The photosites masked, summed over channels (0 with no map).</summary>
    public int MaskedPixels => BadPixelDetection.CountMaskedPixels(_badPixels);

    /// <summary>The dark that serves a frame of this state, or null. Public so a host can say which one it applies.</summary>
    public GuideDark? Find(TimeSpan exposure, short gain, double temperatureC, int width, int height)
    {
        GuideDark? best = null;
        var bestScore = double.MaxValue;
        foreach (var dark in _darks)
        {
            if (dark.Master.Width != width || dark.Master.Height != height || dark.Exposure <= TimeSpan.Zero || exposure <= TimeSpan.Zero)
            {
                continue;
            }
            if (gain >= 0 && dark.Gain >= 0 && gain != dark.Gain)
            {
                continue;
            }
            var ratio = exposure / dark.Exposure;
            if (ratio > MaxExposureRatio || ratio < 1 / MaxExposureRatio)
            {
                continue;
            }
            var score = Math.Abs(Math.Log(ratio));
            if (double.IsFinite(temperatureC) && double.IsFinite(dark.TemperatureC))
            {
                score += TemperatureWeightPerC * Math.Abs(temperatureC - dark.TemperatureC);
            }
            if (score < bestScore)
            {
                (best, bestScore) = (dark, score);
            }
        }
        return best;
    }

    /// <summary>
    /// The frame with its matching dark subtracted and its bad pixels masked, in new planes; CONSUMES <paramref name="frame"/>.
    /// The frame as given when there is nothing to apply (no dark serves it and no map fits it).
    /// </summary>
    /// <param name="frame">The camera's frame, released here once a corrected one replaces it.</param>
    /// <param name="exposure">What it was exposed for.</param>
    /// <param name="gain">The gain it was taken at, or -1 when unknown.</param>
    public Image Correct(Image frame, TimeSpan exposure, short gain = -1)
    {
        var dark = Find(exposure, gain, frame.ImageMeta.CCDTemperature, frame.Width, frame.Height);
        var mask = _badPixels is { } m && m.Length == frame.ChannelCount ? m : null;
        if (dark is null && mask is null)
        {
            return frame;
        }

        var current = frame;
        if (dark is not null)
        {
            var ratio = exposure / dark.Exposure;
            var levels = PedestalsOf(dark);
            var data = new float[frame.ChannelCount][,];
            for (var c = 0; c < data.Length; c++)
            {
                data[c] = Subtract(frame.GetChannelArray(c), dark.Master.GetChannelArray(c), levels[c], (float)ratio);
            }
            current = new Image(data, frame.BitDepth, frame.MaxValue, 0f, frame.Pedestal, frame.ImageMeta, frame.SamplesAreUnitReferred);
        }
        if (mask is not null)
        {
            var masked = BadPixelDetection.WithMaskedFromNeighbours(current, mask);
            if (!ReferenceEquals(masked, current))
            {
                if (!ReferenceEquals(current, frame))
                {
                    current.Release();
                }
                current = masked;
            }
        }

        if (!ReferenceEquals(current, frame))
        {
            frame.Release();
        }
        return current;
    }

    // The pedestal of each plane of a dark: its level at zero exposure, extrapolated through the library's dark of the same gain
    // and the most different exposure (a dark's median is pedestal PLUS dark current, so it cannot be the pedestal). With no such
    // partner the pedestal is unknowable and taken as zero, so the whole dark scales.
    private float[] PedestalsOf(GuideDark dark)
    {
        var own = _levels[dark];
        GuideDark? partner = null;
        foreach (var other in _darks)
        {
            if (other.Master.ChannelCount != dark.Master.ChannelCount || other.Master.Width != dark.Master.Width || other.Master.Height != dark.Master.Height
                || (other.Gain >= 0 && dark.Gain >= 0 && other.Gain != dark.Gain) || other.Exposure == dark.Exposure)
            {
                continue;
            }
            if (partner is null || Math.Abs(other.Exposure.TotalSeconds - dark.Exposure.TotalSeconds) > Math.Abs(partner.Exposure.TotalSeconds - dark.Exposure.TotalSeconds))
            {
                partner = other;
            }
        }
        if (partner is null)
        {
            return new float[own.Length];
        }

        var theirs = _levels[partner];
        var (t1, t2) = (dark.Exposure.TotalSeconds, partner.Exposure.TotalSeconds);
        var pedestals = new float[own.Length];
        for (var c = 0; c < pedestals.Length; c++)
        {
            var p = (own[c] * t2 - theirs[c] * t1) / (t2 - t1);
            pedestals[c] = float.IsFinite(p) ? Math.Clamp(p, 0f, own[c]) : 0f;
        }
        return pedestals;
    }

    // dark scaled about its pedestal: pedestal + (dark - pedestal) * ratio, so the pedestal stays and only the dark current moves.
    private static float[,] Subtract(float[,] light, float[,] dark, float level, float ratio)
    {
        var h = light.GetLength(0);
        var w = light.GetLength(1);
        var result = new float[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var d = ratio == 1f ? dark[y, x] : level + (dark[y, x] - level) * ratio;
                var v = light[y, x] - d;
                result[y, x] = v > 0f ? v : 0f;
            }
        }
        return result;
    }

    // The median of a strided sample of each plane: its pedestal, which a hot pixel or a warm corner does not move.
    private static float[] ChannelLevels(Image master)
    {
        var levels = new float[master.ChannelCount];
        for (var c = 0; c < levels.Length; c++)
        {
            var plane = master.GetChannelArray(c);
            var h = plane.GetLength(0);
            var w = plane.GetLength(1);
            var sample = new List<float>();
            for (var y = 1; y < h; y += 7)
            {
                for (var x = 1; x < w; x += 7)
                {
                    sample.Add(plane[y, x]);
                }
            }
            if (sample.Count == 0)
            {
                continue;
            }
            sample.Sort();
            levels[c] = sample[sample.Count / 2];
        }
        return levels;
    }
}
