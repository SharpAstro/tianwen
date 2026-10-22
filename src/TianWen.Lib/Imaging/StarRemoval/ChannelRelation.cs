using System;
using System.Collections.Immutable;
using System.Linq;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// How a star's channels stand to each other in shape (R2s2, docs/plans/star-remover-training.md): each channel's FWHM and
/// its wing light over its core light (<see cref="WingToCore"/>), as natural logs less their mean over the channels. A
/// relation holds nothing of the seeing or of the halo's overall strength, only how the channels differ, so a master's
/// stars can be drawn at another master's relation and keep their own size (<see cref="ChannelVariation"/>). Both measures
/// are the same at any width, since the Moffat and the field's table both scale with it, and a wing gain g multiplies the
/// wing light by g exactly: a star drawn at a relation measures as that relation.
/// </summary>
/// <param name="Source">Whose relation it is: the session it was measured on.</param>
/// <param name="LogWidth">Each channel's log FWHM less the channels' mean.</param>
/// <param name="LogWing">Each channel's log wing-to-core light less the channels' mean.</param>
public sealed record ChannelRelation(string Source, ImmutableArray<double> LogWidth, ImmutableArray<double> LogWing)
{
    /// <summary>How far out the wing light is integrated, in FWHM: past the longest field table measured (about 9 FWHM).</summary>
    public const double ReachFwhm = 16.0;

    private const int Steps = 4000;

    /// <summary>The channels the relation spans.</summary>
    public int Channels => LogWidth.Length;

    /// <summary>
    /// A profile's wing light over its core light, split where <see cref="StarProfile.WingFactor"/> ramps (none of the light
    /// inside <see cref="StarProfile.WingStartFwhm"/> FWHM is wing, all of it past <see cref="StarProfile.WingFullFwhm"/>, a
    /// smoothstep between), its halo table included, times the profile's wing gain. Integrated on a grid in units of the FWHM,
    /// so a profile and its widened copy read the same.
    /// </summary>
    public static double WingToCore(StarProfile profile)
    {
        var fwhm = profile.FwhmPx;
        var du = ReachFwhm / Steps;
        double wing = 0, core = 0;
        for (var i = 1; i <= Steps; i++)
        {
            // The midpoint of each step: the ring's area weight is u, the FWHM's square cancels in the ratio.
            var u = (i - 0.5) * du;
            var t = Math.Clamp((u - StarProfile.WingStartFwhm) / (StarProfile.WingFullFwhm - StarProfile.WingStartFwhm), 0.0, 1.0);
            var s = t * t * (3.0 - 2.0 * t);
            var p = profile.UngainedAt(u * fwhm) * u;
            wing += p * s;
            core += p * (1.0 - s);
        }
        return profile.HaloGain * wing / core;
    }

    /// <summary>The relation of one star's channel profiles (their elongation shared, so read round).</summary>
    public static ChannelRelation Of(string source, ImmutableArray<StarProfile> profiles)
    {
        if (profiles.IsDefaultOrEmpty)
        {
            throw new ArgumentException("a relation needs at least one channel", nameof(profiles));
        }
        var logWidth = profiles.Select(static p => Math.Log(p.FwhmPx)).ToArray();
        var logWing = profiles.Select(static p => Math.Log(WingToCore(p with { AxisRatio = 1.0, PositionAngleRad = 0.0 }))).ToArray();
        var meanWidth = logWidth.Average();
        var meanWing = logWing.Average();
        return new ChannelRelation(source, [.. logWidth.Select(w => w - meanWidth)], [.. logWing.Select(w => w - meanWing)]);
    }

    /// <summary>The relation of a master's field profile (<see cref="StarlessFieldProfile"/>): its stars as the injector
    /// draws them with <see cref="StarProfileFamily.Field"/>.</summary>
    public static ChannelRelation Of(string source, StarlessFieldProfile field)
        => Of(source, [.. field.Channels.Select(static c => new StarProfile(StarProfileFamily.Field, c.Fwhm, c.Beta, 1.0, 0.0, c.Table, c.TableAlpha))]);
}
