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
    public StarBeta(double beta)
    {
        if (!IsBeta(beta))
        {
            throw new ArgumentOutOfRangeException(nameof(beta), beta, "a held star beta is in [1.1, 20]");
        }
        Beta = beta;
    }

    /// <summary>The Moffat exponent every channel is drawn at.</summary>
    public double Beta { get; }

    /// <summary>Whether <paramref name="beta"/> is one a held star beta takes, [1.1, 20].</summary>
    public static bool IsBeta(double beta) => beta >= 1.1 && beta <= 20.0;

    /// <summary>The profiles with every channel's Moffat exponent at <see cref="Beta"/>.</summary>
    public ImmutableArray<StarProfile> Apply(ImmutableArray<StarProfile> profiles)
    {
        var set = ImmutableArray.CreateBuilder<StarProfile>(profiles.Length);
        foreach (var profile in profiles)
        {
            set.Add(profile with { Beta = Beta });
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
    public StarBetaDraw(double share, double low, double high)
    {
        if (!IsDraw(share, low, high))
        {
            throw new ArgumentOutOfRangeException(nameof(share), (share, low, high), "a star beta draw is a share in (0, 1] and betas low <= high in [1.1, 20]");
        }
        (Share, Low, High) = (share, low, high);
    }

    /// <summary>The share of cells drawn at a held beta.</summary>
    public double Share { get; }

    /// <summary>The lowest beta drawn.</summary>
    public double Low { get; }

    /// <summary>The highest beta drawn.</summary>
    public double High { get; }

    /// <summary>Whether the three are a draw: a share in (0, 1] and betas low &lt;= high in [1.1, 20].</summary>
    public static bool IsDraw(double share, double low, double high)
        => share > 0.0 && share <= 1.0 && StarBeta.IsBeta(low) && StarBeta.IsBeta(high) && low <= high;

    /// <summary>
    /// One cell's held beta, or null for a cell drawn at its master's own, from a stream of its own so the cell's stars are the
    /// ones it makes without the draw. Two numbers are taken whatever the outcome, so the stream's use does not depend on it.
    /// </summary>
    public StarBeta? DrawCell(Random rng)
    {
        var held = rng.NextDouble() < Share;
        var beta = Low + ((High - Low) * rng.NextDouble());
        return held ? new StarBeta(beta) : null;
    }
}
