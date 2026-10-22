using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// R2s2 (docs/plans/star-remover-training.md): every star of a draw of a cell takes one real master's channel relation
/// (<see cref="ChannelRelation"/>), drawn from <see cref="Pool"/>, the training masters' own field profiles, in place of its
/// own master's, so a master's stars are seen with every way real channels differ and with no way they do not. Each
/// channel's width and wing are moved by the drawn relation less the master's own, so the master's mean width and wing (its
/// seeing and its halo) stay its own.
/// <para>
/// R2s drew each channel's wing (0.5 to 2) and width (0.85 to 1.18) on its own, and its stars came out unlike any real
/// master's. Over the 123 colour training masters a wider red core comes with a weaker red wing (correlation -0.59 between
/// the two, red to green, against R2s's -0.23), 1 of 123 has red's wing 1.5 times green's or more (R2s: 12.8 percent of
/// its stars) and 2 of 123 have red at least 1.3 times green's width with a wing at least green's (R2s: 9.4 percent); 45
/// percent of R2s's stars lay outside every real master's relation. R2s then took 77 percent of the red above the ring at
/// M31's nucleus, uniformly coloured light (red over green 1.17 to 1.21 from 0 to 30 px), against 10 percent of its green:
/// a red-only star about 8 px wide, which its own data had taught it exists.
/// </para>
/// </summary>
public sealed class ChannelVariation
{
    private readonly Dictionary<int, ImmutableArray<ChannelRelation>> _byChannels;

    /// <param name="pool">The measured relations to draw from; a master draws only those of its own channel count.</param>
    public ChannelVariation(ImmutableArray<ChannelRelation> pool)
    {
        Pool = pool;
        _byChannels = pool.GroupBy(static r => r.Channels).ToDictionary(static g => g.Key, static g => g.ToImmutableArray());
    }

    /// <summary>The measured relations a draw is taken from.</summary>
    public ImmutableArray<ChannelRelation> Pool { get; }

    /// <summary>
    /// One draw of a cell: a relation from the pool with the master's channel count, and each channel's factors (log wing
    /// gain, log width) that move <paramref name="own"/> onto it. A master whose channel count the pool lacks keeps its own.
    /// </summary>
    public (ChannelRelation Drawn, ImmutableArray<(double LogHalo, double LogWidth)> Factors) DrawCell(ChannelRelation own, Random rng)
    {
        var drawn = _byChannels.TryGetValue(own.Channels, out var candidates) && candidates.Length > 0
            ? candidates[rng.Next(candidates.Length)]
            : own;
        var factors = ImmutableArray.CreateBuilder<(double, double)>(own.Channels);
        for (var c = 0; c < own.Channels; c++)
        {
            factors.Add((drawn.LogWing[c] - own.LogWing[c], drawn.LogWidth[c] - own.LogWidth[c]));
        }
        return (drawn, factors.MoveToImmutable());
    }

    /// <summary>Each channel's profile drawn at its factors.</summary>
    public static ImmutableArray<StarProfile> Apply(ImmutableArray<StarProfile> profiles, ImmutableArray<(double LogHalo, double LogWidth)> factors)
    {
        if (profiles.Length != factors.Length)
        {
            throw new ArgumentException($"{factors.Length} channel factors for {profiles.Length} channel profiles", nameof(factors));
        }
        var varied = ImmutableArray.CreateBuilder<StarProfile>(profiles.Length);
        for (var c = 0; c < profiles.Length; c++)
        {
            var p = profiles[c];
            varied.Add(p with { FwhmPx = p.FwhmPx * Math.Exp(factors[c].LogWidth), HaloLogGain = p.HaloLogGain + factors[c].LogHalo });
        }
        return varied.MoveToImmutable();
    }
}
