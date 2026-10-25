using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// The channel wing relation the real masters span (#1241, docs/plans/star-remover-training.md, Synthetics S1): each channel's
/// light from <see cref="StarShape.LightFromPx"/> to <see cref="StarShape.LightToPx"/> over its core (per catalogued amplitude,
/// as <see cref="StarShapeChannel.RealLight"/> reads it), as a ratio to green's, in natural logs, over every readable
/// brightness band of every master of a star-shapes file. R2s's lesson: a channel drawn outside every real master's relation
/// teaches a model to infer the wrong one (Andromeda's blue), and a brightness trend per channel
/// (<see cref="StarShape.LogGains"/>) moves each channel's wing on its own, so nothing else keeps a drawn star's channels to what a
/// real star's can be. Read per channel count (a mono master has no relation; the data carries no filter, so a colour sensor's
/// masters of every filter share one envelope, the widest honest one), as a box: each channel's own least and greatest.
/// </summary>
public sealed class ChannelWingEnvelope
{
    /// <summary>
    /// A band counts toward the envelope only where its light is read to better than the stars' own scatter: its
    /// <see cref="StarShapeChannel.Weight"/> at least this fraction of what the stars alone would carry
    /// (<see cref="StarShape.IntrinsicScatter"/>). A faint band's light is a fraction of the sky's error, and its ratio would
    /// only widen the envelope by noise. A row measured before the weight was kept counts for nothing.
    /// </summary>
    public const double MinReliability = 0.5;

    private readonly Dictionary<int, (double LogMin, double LogMax)[]> _bounds;

    private ChannelWingEnvelope(Dictionary<int, (double LogMin, double LogMax)[]> bounds, Dictionary<int, int> bands)
    {
        _bounds = bounds;
        BandsByChannels = bands;
    }

    /// <summary>The readable bands each channel count's envelope was read over.</summary>
    public IReadOnlyDictionary<int, int> BandsByChannels { get; }

    /// <summary>The channel the others are read against: green, which is the middle of three; the last of two.</summary>
    public static int ReferenceChannel(int channels) => Math.Min(1, channels - 1);

    /// <summary>
    /// The envelope over <paramref name="rows"/>. A band counts where every channel's light is read and positive and reliable
    /// (<see cref="MinReliability"/>); a master of one channel has no relation and counts for nothing.
    /// </summary>
    public static ChannelWingEnvelope Read(IEnumerable<StarShapeRow> rows)
    {
        var min = new Dictionary<int, double[]>();
        var max = new Dictionary<int, double[]>();
        var bands = new Dictionary<int, int>();
        foreach (var row in rows)
        {
            foreach (var band in row.Bands)
            {
                var channels = band.Channels.Length;
                if (channels < 2 || !band.Channels.All(Readable))
                {
                    continue;
                }
                var reference = Math.Log(band.Channels[ReferenceChannel(channels)].RealLight);
                if (!min.TryGetValue(channels, out var lo))
                {
                    min[channels] = lo = [.. Enumerable.Repeat(double.PositiveInfinity, channels)];
                    max[channels] = [.. Enumerable.Repeat(double.NegativeInfinity, channels)];
                    bands[channels] = 0;
                }
                var hi = max[channels];
                for (var c = 0; c < channels; c++)
                {
                    var relation = Math.Log(band.Channels[c].RealLight) - reference;
                    lo[c] = Math.Min(lo[c], relation);
                    hi[c] = Math.Max(hi[c], relation);
                }
                bands[channels]++;
            }
        }
        return new ChannelWingEnvelope(
            min.ToDictionary(static p => p.Key, p => Enumerable.Range(0, p.Key).Select(c => (p.Value[c], max[p.Key][c])).ToArray()), bands);

        static bool Readable(StarShapeChannel c)
            => double.IsFinite(c.RealLight) && c.RealLight > 0 && c.Stars > 0
                && c.Weight >= MinReliability * c.Stars / (StarShape.IntrinsicScatter * StarShape.IntrinsicScatter);
    }

    /// <summary>Each channel's least and greatest log light ratio to green over the real masters of <paramref name="channels"/>
    /// channels (the reference channel's is (0, 0)), or null where none was readable.</summary>
    public (double LogMin, double LogMax)[]? BoundsFor(int channels) => _bounds.TryGetValue(channels, out var bounds) ? bounds : null;

    /// <summary>The envelope per channel count, for a log: its bands and each channel's bounds.</summary>
    public string InWords()
        => _bounds.Count == 0
            ? "no readable band, nothing is clamped"
            : string.Join("; ", _bounds.OrderBy(static p => p.Key).Select(p => string.Create(CultureInfo.InvariantCulture,
                $"{p.Key} channels, {BandsByChannels[p.Key]} bands: {string.Join(" ", p.Value.Select(static b => $"{b.LogMin:F2}/{b.LogMax:F2}"))}")));
}

/// <summary>
/// A master's stars held to a <see cref="ChannelWingEnvelope"/> (#1241): a drawn star's channels take their trend gains
/// (<see cref="StarShape.LogGains"/>), and a channel whose wing light over its core, against green's, falls outside what the real
/// masters span has its wing gain moved to the envelope's edge. Green keeps the gain the trend gave it, so the clamp only
/// ever moves the others toward each other, never green's level. The light a gain draws is read as
/// <see cref="StarShape.DrawnLight"/> reads it (the same measure as the envelope's), which is a line in the gain (the wing factor
/// is linear in it, and so are the sums and the sky's median over a wing past 1.5 FWHM), so a gain that meets an edge is solved
/// exactly from two readings per channel.
/// </summary>
public sealed class WingRelationClamp
{
    private readonly (double A, double B)[] _light;
    private readonly (double LogMin, double LogMax)[] _bounds;

    /// <param name="channelProfiles">Each channel's round profile as the master draws it at wing gain 1 (the field profile's).</param>
    /// <param name="bounds">The envelope's bounds for this channel count (<see cref="ChannelWingEnvelope.BoundsFor"/>).</param>
    public WingRelationClamp(ImmutableArray<StarProfile> channelProfiles, (double LogMin, double LogMax)[] bounds)
    {
        if (channelProfiles.Length < 2 || bounds.Length != channelProfiles.Length)
        {
            throw new ArgumentException($"{bounds.Length} channel bounds for {channelProfiles.Length} channel profiles; a relation needs two channels", nameof(bounds));
        }
        _bounds = bounds;
        _light = [.. channelProfiles.Select(static p =>
        {
            var round = p with { AxisRatio = 1.0, PositionAngleRad = 0.0, HaloLogGain = 0.0 };
            var one = StarShape.DrawnLight(round, 0.0, 0.0);
            var slope = StarShape.DrawnLight(round with { HaloLogGain = Math.Log(2.0) }, 0.0, 0.0) - one;
            return (one - slope, slope);
        })];
    }

    /// <summary>The light per amplitude channel <paramref name="channel"/> draws at wing gain <paramref name="gain"/>.</summary>
    public double LightAt(int channel, double gain) => _light[channel].A + _light[channel].B * gain;

    /// <summary>Each channel's log light ratio to green for profiles drawn at their own wing gains; the reference's is 0, and NaN
    /// where a channel's light is not positive.</summary>
    public double[] LogRelations(ImmutableArray<StarProfile> profiles)
    {
        var reference = ChannelWingEnvelope.ReferenceChannel(profiles.Length);
        var referenceLight = LightAt(reference, profiles[reference].HaloGain);
        return [.. profiles.Select((p, c) => c == reference ? 0.0 : Math.Log(LightAt(c, p.HaloGain) / referenceLight))];
    }

    /// <summary>
    /// <paramref name="profiles"/> with every channel outside the envelope given the gain that puts it on its edge, held to
    /// <see cref="StarShape.MinGain"/> and <see cref="StarShape.MaxGain"/>; <paramref name="clamped"/> is whether any was moved.
    /// A gain that cannot reach the edge within those bounds (a channel whose core alone already carries more of the 3 to 8 px
    /// light than the edge allows, whatever its wing) stays at the bound, the nearest the draw can be, and is said by
    /// <paramref name="unreached"/>: the guarantee that a star's relation is inside the envelope holds only where it is false.
    /// </summary>
    public ImmutableArray<StarProfile> Apply(ImmutableArray<StarProfile> profiles, out bool clamped, out bool unreached)
    {
        clamped = false;
        unreached = false;
        var channels = profiles.Length;
        if (channels != _light.Length)
        {
            throw new ArgumentException($"{channels} channel profiles for a clamp of {_light.Length} channels", nameof(profiles));
        }
        var reference = ChannelWingEnvelope.ReferenceChannel(channels);
        var referenceLight = LightAt(reference, profiles[reference].HaloGain);
        if (!(referenceLight > 0))
        {
            return profiles;
        }
        var result = profiles.ToBuilder();
        for (var c = 0; c < channels; c++)
        {
            var (a, b) = _light[c];
            if (c == reference || !(b > 0))
            {
                continue;
            }
            var light = LightAt(c, profiles[c].HaloGain);
            var relation = light > 0 ? Math.Log(light / referenceLight) : double.NegativeInfinity;
            var edge = relation < _bounds[c].LogMin ? _bounds[c].LogMin : relation > _bounds[c].LogMax ? _bounds[c].LogMax : double.NaN;
            if (double.IsNaN(edge))
            {
                continue;
            }
            var wanted = (referenceLight * Math.Exp(edge) - a) / b;
            var gain = Math.Clamp(wanted, StarShape.MinGain, StarShape.MaxGain);
            unreached |= gain != wanted;
            result[c] = profiles[c] with { HaloLogGain = Math.Log(gain) };
            clamped = true;
        }
        return clamped ? result.ToImmutable() : profiles;
    }
}
