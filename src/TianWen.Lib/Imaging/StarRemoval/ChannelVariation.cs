using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// R2s (docs/plans/star-remover-training.md, "R2s: each channel's halo and width drawn on its own"): every channel of an
/// injected star gets its own halo strength (<see cref="StarProfile.HaloLogGain"/>) and width, around the master's measured
/// profile. A remover trained on stars whose channels all keep their master's relation learned to take blue's shape from
/// red and green: on the owner's Andromeda, whose blue halo is weaker than its others, R2p dug blue at 46 % of its bright
/// stars, and each channel put through alone came out clean (1.5 %); on the held-out Tarantula, whose blue halo is a
/// little stronger, it left the most light in blue. The factors are drawn once per draw of a cell, one optical train per
/// tile as a master's stars share theirs, each channel on its own and log-uniform in its range, and each star then
/// scatters about them by up to <see cref="StarScatter"/> (log-uniform, both ways), its lobes taking its own.
/// </summary>
/// <param name="HaloMin">The smallest wing factor a channel draws.</param>
/// <param name="HaloMax">The largest; past 1 it also covers the bright stars' near wings the field's table under-reads
/// by up to 30 percent (#1241).</param>
/// <param name="WidthMin">The smallest factor a channel's FWHM is drawn at.</param>
/// <param name="WidthMax">The largest.</param>
/// <param name="StarScatter">How far, as a log, each star's factors stray from the draw's.</param>
public sealed record ChannelVariation(double HaloMin, double HaloMax, double WidthMin, double WidthMax, double StarScatter)
{
    /// <summary>
    /// R2s's ranges. The halo spans half to twice the measured wing, which holds the channel-to-channel wing ratios measured
    /// on the owner's masters (Andromeda's blue at 4-8 px about 0.4 of its red) and #1241's 30 percent; the width spans
    /// 0.85 to 1.18 (Andromeda's green against its red is 0.82, the store's channels mostly within 10 percent of each other).
    /// </summary>
    public static ChannelVariation R2s { get; } = new ChannelVariation(0.5, 2.0, 0.85, 1.18, 0.1);

    /// <summary>One draw of a cell's per-channel factors, as logs: (halo, width) for each channel.</summary>
    public ImmutableArray<(double LogHalo, double LogWidth)> DrawCell(int channels, Random rng)
    {
        var factors = ImmutableArray.CreateBuilder<(double, double)>(channels);
        for (var c = 0; c < channels; c++)
        {
            factors.Add((LogUniform(HaloMin, HaloMax, rng), LogUniform(WidthMin, WidthMax, rng)));
        }
        return factors.MoveToImmutable();
    }

    /// <summary>One star's factors: the cell's, each scattered on its own by up to <see cref="StarScatter"/>.</summary>
    public ImmutableArray<(double LogHalo, double LogWidth)> DrawStar(ImmutableArray<(double LogHalo, double LogWidth)> cell, Random rng)
    {
        var factors = ImmutableArray.CreateBuilder<(double, double)>(cell.Length);
        foreach (var (logHalo, logWidth) in cell)
        {
            factors.Add((logHalo + Scatter(rng), logWidth + Scatter(rng)));
        }
        return factors.MoveToImmutable();
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

    private double Scatter(Random rng) => StarScatter * (2.0 * rng.NextDouble() - 1.0);

    private static double LogUniform(double min, double max, Random rng)
        => Math.Log(min) + rng.NextDouble() * (Math.Log(max) - Math.Log(min));
}
