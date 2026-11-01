using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// Every channel of every injected star drawn at <see cref="Ratio"/> times the reference channel's width
/// (docs/plans/star-remover-training.md, "The SMC's green stars"). The SMC night through the L-eNhance has stars 1.23 times
/// wider in green and blue than in red (OIII and H-beta against H-alpha), where the training masters' injected stars sit at
/// 0.64 to 1.12 (10th to 90th percentile, the L-Ultimate nights being most of them), and every model leaves its stars' green.
/// A causal check: export the same draws with the ratio forced and read whether the leftover follows it. It draws no random
/// number, so an export with it places, brightens and colours every star as one without it, and only the non-reference
/// channels' widths differ; each star's widths are in its injections.jsonl row (<c>FwhmPx</c>). A mono master is untouched.
/// </summary>
public sealed record ChromaticWidth
{
    /// <summary>The channel the others' widths are set from: red in an RGB master.</summary>
    public const int ReferenceChannel = 0;

    /// <param name="ratio">Every other channel's width over the reference channel's, in [0.25, 4].</param>
    public ChromaticWidth(double ratio)
    {
        if (!IsRatio(ratio))
        {
            throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "a chromatic width ratio is in [0.25, 4]");
        }
        Ratio = ratio;
    }

    /// <summary>Every other channel's width over the reference channel's.</summary>
    public double Ratio { get; }

    /// <summary>Whether <paramref name="ratio"/> is one a chromatic width takes, [0.25, 4].</summary>
    public static bool IsRatio(double ratio) => ratio >= 0.25 && ratio <= 4.0;

    /// <summary>The profiles with every channel but the reference drawn at <see cref="Ratio"/> times its width.</summary>
    public ImmutableArray<StarProfile> Apply(ImmutableArray<StarProfile> profiles)
    {
        if (profiles.Length < 2)
        {
            return profiles;
        }
        var reference = profiles[ReferenceChannel].FwhmPx;
        var set = ImmutableArray.CreateBuilder<StarProfile>(profiles.Length);
        for (var c = 0; c < profiles.Length; c++)
        {
            set.Add(c == ReferenceChannel ? profiles[c] : profiles[c] with { FwhmPx = reference * Ratio });
        }
        return set.MoveToImmutable();
    }
}
