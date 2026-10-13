using System;
using System.Collections.Immutable;
using System.Globalization;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Learned sharpening gains (#1552, docs/plans/planetary-restoration.md, "L-GAINS-D"): per colour, a ridge regression from what the
/// derivation reads off a master's limb (the edge's transfer at 0.1 cycles a pixel, the limb fit's core sigma and halo share) to the a
/// trous gains the truth asks of that master, fitted on twins of one capture at its own sampling. The derived gains are fitted through a
/// kernel read off the edge at every frequency, which past the pupil's cutoff is noise; on an oversampled capture that is twice the
/// truth's error, and these gains are read below the cutoff only.
/// </summary>
/// <remarks>
/// A model applies only inside what it was fitted on: every reading inside its twins' range, and the channel's pupil cutoff within
/// <see cref="CutoffTolerance"/> of theirs (the readings are in pixels and cycles a pixel, so they mean something else at another
/// sampling). Outside, <see cref="Predict"/> answers null and says which reading fell outside, and the derived gains stand.
/// </remarks>
/// <param name="Name">What the model was fitted on, as a sentence names it.</param>
/// <param name="Colours">One fit per colour channel, red, green and blue (a mono master takes the first).</param>
public sealed record PlanetaryGainsModel(string Name, ImmutableArray<PlanetaryGainsModel.Colour> Colours)
{
    /// <summary>One colour's fit.</summary>
    /// <param name="Mean">Each reading's mean over the twins, which it is centred by.</param>
    /// <param name="Spread">Each reading's standard deviation over the twins, which it is scaled by.</param>
    /// <param name="Low">Each reading's least over the twins.</param>
    /// <param name="High">Each reading's most over the twins.</param>
    /// <param name="Cutoff">The channel's pupil cutoff the twins share, cycles a pixel.</param>
    /// <param name="Weights">For each a trous band, finest first, one weight per standardised reading.</param>
    /// <param name="Intercepts">For each band, the gain at the twins' mean readings.</param>
    public sealed record Colour(ImmutableArray<double> Mean, ImmutableArray<double> Spread, ImmutableArray<double> Low, ImmutableArray<double> High,
        double Cutoff, ImmutableArray<ImmutableArray<double>> Weights, ImmutableArray<double> Intercepts);

    /// <summary>The readings a model takes, in order, as a sentence names them.</summary>
    public static readonly ImmutableArray<string> Readings = ["the edge at 0.1 cycles a pixel", "the limb's core sigma", "the limb's halo share"];

    /// <summary>How far a channel's pupil cutoff may lie from the twins' and the model still apply, as a share of theirs.</summary>
    public const double CutoffTolerance = 0.1;

    /// <summary>A master channel's readings, in <see cref="Readings"/>' order.</summary>
    public static ImmutableArray<double> ReadingsOf(in LimbFit fit, double edgeAtTenth) => [edgeAtTenth, fit.PsfSigma, fit.HaloFraction];

    /// <summary>
    /// The gains, finest band first, for <paramref name="colour"/> with these <paramref name="readings"/> at this <paramref name="cutoff"/>;
    /// null, with <paramref name="outside"/> saying why, when the master lies outside what the model was fitted on.
    /// </summary>
    public ImmutableArray<double>? Predict(int colour, ImmutableArray<double> readings, double cutoff, out string? outside)
    {
        var fit = Colours[Math.Min(colour, Colours.Length - 1)];
        var inv = CultureInfo.InvariantCulture;
        if (!(Math.Abs(cutoff - fit.Cutoff) <= CutoffTolerance * fit.Cutoff))
        {
            outside = string.Create(inv, $"its pupil cutoff {cutoff:0.000} cycles a pixel is not the {fit.Cutoff:0.000} the model was fitted at");
            return null;
        }
        for (var i = 0; i < Readings.Length; i++)
        {
            if (!(readings[i] >= fit.Low[i] && readings[i] <= fit.High[i]))
            {
                outside = string.Create(inv, $"{Readings[i]} reads {readings[i]:0.000}, outside the twins' {fit.Low[i]:0.000} to {fit.High[i]:0.000}");
                return null;
            }
        }
        var gains = ImmutableArray.CreateBuilder<double>(fit.Intercepts.Length);
        for (var band = 0; band < fit.Intercepts.Length; band++)
        {
            var gain = fit.Intercepts[band];
            for (var i = 0; i < Readings.Length; i++)
            {
                gain += fit.Weights[band][i] * (readings[i] - fit.Mean[i]) / fit.Spread[i];
            }
            gains.Add(gain);
        }
        outside = null;
        return gains.MoveToImmutable();
    }
}
