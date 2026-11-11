using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// Every channel of every injected star drawn at one Moffat <see cref="Beta"/>, its FWHM kept (docs/plans/star-remover-training.md,
/// "The SMC's noise check: not the sky, and the heaviest wings in the archive", #1947). The SMC's L-eNhance night fits its stars at
/// beta 1.74 / 1.42 / 1.40, the lowest of the archive, where the training stars' median is 3.8. A causal check: export the same
/// draws with the beta held and read whether the leftover follows it. It draws no random number, so an export with it places,
/// brightens and colours every star as one without it, and only the profiles' beta differs; each star's beta is in its
/// injections.jsonl row (<c>Beta</c>). A field profile's residual table is kept as it was.
/// </summary>
public sealed record StarBeta
{
    /// <param name="beta">The Moffat exponent every channel is drawn at, in [1.1, 20].</param>
    /// <param name="redFactor">An RGB star's red beta over <paramref name="beta"/>, its green's, in [1/4, 4]; 1 holds it.</param>
    /// <param name="blueFactor">An RGB star's blue beta over its green's, in [1/4, 4]; 1 holds it.</param>
    public StarBeta(double beta, double redFactor = 1.0, double blueFactor = 1.0)
    {
        if (!IsBeta(beta))
        {
            throw new ArgumentOutOfRangeException(nameof(beta), beta, "a held star beta is in [1.1, 20]");
        }
        if (!IsFactor(redFactor) || !IsFactor(blueFactor))
        {
            throw new ArgumentOutOfRangeException(nameof(redFactor), (redFactor, blueFactor), "a channel's beta factor is in [1/4, 4]");
        }
        (Beta, RedFactor, BlueFactor) = (beta, redFactor, blueFactor);
    }

    /// <summary>The Moffat exponent every channel is drawn at: green's in an RGB star.</summary>
    public double Beta { get; }

    /// <summary>An RGB star's red beta over its green's.</summary>
    public double RedFactor { get; }

    /// <summary>An RGB star's blue beta over its green's.</summary>
    public double BlueFactor { get; }

    /// <summary>Whether <paramref name="factor"/> is one a channel's beta factor takes, [1/4, 4].</summary>
    public static bool IsFactor(double factor) => factor >= 0.25 && factor <= 4.0;

    /// <summary>Whether <paramref name="beta"/> is one a held star beta takes, [1.1, 20].</summary>
    public static bool IsBeta(double beta) => beta >= 1.1 && beta <= 20.0;

    /// <summary>
    /// The profiles with every channel's Moffat exponent at <see cref="Beta"/>, an RGB star's red and blue at their factors of it,
    /// kept inside [1.1, 20].
    /// </summary>
    public ImmutableArray<StarProfile> Apply(ImmutableArray<StarProfile> profiles)
    {
        var set = ImmutableArray.CreateBuilder<StarProfile>(profiles.Length);
        for (var c = 0; c < profiles.Length; c++)
        {
            var factor = profiles.Length == 3 ? c switch { 0 => RedFactor, 2 => BlueFactor, _ => 1.0 } : 1.0;
            set.Add(profiles[c] with { Beta = Math.Clamp(Beta * factor, 1.1, 20.0) });
        }
        return set.MoveToImmutable();
    }
}

/// <summary>
/// A <see cref="Share"/> of the cells drawn with every star at one Moffat beta, uniform in [<see cref="Low"/>, <see cref="High"/>],
/// on whatever plate the cell is cut from (docs/plans/star-remover-training.md, "R3h: oversampling the low-beta sessions learns the
/// field, not the wings", #1947). R3h drew the archive's heavy-winged sessions three times as often and its model cleared the SMC by
/// knowing the FIELD: it left HIP 80609's and the Lobster's stars drawn at the SMC's beta as R2w did, and dug every typical star
/// harder. Drawn per cell on any plate, a star's wings are no longer told by its session, only by itself. The cell's stars share
/// one beta, as a real field's do. Each star's beta is in its injections.jsonl row (<c>Beta</c>).
/// </summary>
public sealed record StarBetaDraw
{
    /// <param name="share">The share of cells drawn at a held beta, in (0, 1].</param>
    /// <param name="low">The lowest beta drawn, in [1.1, 20].</param>
    /// <param name="high">The highest beta drawn, in [<paramref name="low"/>, 20].</param>
    /// <param name="channelSpread">How far a held cell's red and blue betas are drawn from its green's, in [1, 4]: each a factor
    /// log-uniform in [1/spread, spread], drawn per cell. 1, every channel at one beta, as R3i drew them.</param>
    public StarBetaDraw(double share, double low, double high, double channelSpread = 1.0)
    {
        if (!IsDraw(share, low, high, channelSpread))
        {
            throw new ArgumentOutOfRangeException(nameof(share), (share, low, high, channelSpread),
                "a star beta draw is a share in (0, 1], betas low <= high in [1.1, 20] and a channel spread in [1, 4]");
        }
        (Share, Low, High, ChannelSpread) = (share, low, high, channelSpread);
    }

    /// <summary>
    /// How far a held cell's red and blue betas are drawn from its green's (docs/plans/star-remover-training.md, "M42's bright
    /// stars: the channels' wings, outside the training range"): M42's field fits blue at 2.64 times green's beta, where R3i's
    /// draw held every channel at one, so a third of the training cells taught that blue's wing follows green's.
    /// </summary>
    public double ChannelSpread { get; }

    /// <summary>The share of cells drawn at a held beta.</summary>
    public double Share { get; }

    /// <summary>The lowest beta drawn.</summary>
    public double Low { get; }

    /// <summary>The highest beta drawn.</summary>
    public double High { get; }

    /// <summary>Whether the three are a draw: a share in (0, 1] and betas low &lt;= high in [1.1, 20].</summary>
    public static bool IsDraw(double share, double low, double high, double channelSpread = 1.0)
        => share > 0.0 && share <= 1.0 && StarBeta.IsBeta(low) && StarBeta.IsBeta(high) && low <= high
           && channelSpread >= 1.0 && channelSpread <= 4.0;

    /// <summary>
    /// One cell's held beta, or null for a cell drawn at its master's own, from a stream of its own so the cell's stars are the
    /// ones it makes without the draw. Two numbers are taken whatever the outcome, so the stream's use does not depend on it,
    /// and two more after them with a <see cref="ChannelSpread"/> over 1, so a draw without one is the draw R3i made.
    /// </summary>
    public StarBeta? DrawCell(Random rng)
    {
        var held = rng.NextDouble() < Share;
        var beta = Low + ((High - Low) * rng.NextDouble());
        var (red, blue) = (1.0, 1.0);
        if (ChannelSpread > 1.0)
        {
            var span = Math.Log(ChannelSpread);
            red = Math.Exp(span * ((2.0 * rng.NextDouble()) - 1.0));
            blue = Math.Exp(span * ((2.0 * rng.NextDouble()) - 1.0));
        }
        return held ? new StarBeta(beta, red, blue) : null;
    }
}
