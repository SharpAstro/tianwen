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
