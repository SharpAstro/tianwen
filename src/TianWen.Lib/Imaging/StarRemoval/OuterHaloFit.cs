using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>One rig's fitted outer halo for one channel and how well the Moffat holds the measured profile.</summary>
/// <param name="Halo">The fitted term.</param>
/// <param name="RmsLog">The RMS of ln(measured / fitted) over the fitted radii.</param>
/// <param name="TailLog">The largest |ln(measured / fitted)| from 20 to 30 px (0 where the profile has no positive value there):
/// the number to set against the masters' own spread to decide whether a residual table is wanted instead.</param>
public readonly record struct OuterHaloFitResult(OuterHalo Halo, double RmsLog, double TailLog);

/// <summary>
/// The outer halos, per rig, fitted in C# to the clipped-star halo profiles the census measured
/// (<c>training/star-removal/domain/halo-profiles.json</c>, #1870). The profile fitted is the LINEAR one, the master's unit
/// scale, which is what the injector draws in; what an injected star already draws (the file's synth11 set, where a rig has
/// one) is taken off first, so the term is the shortfall under the existing profile and never a second copy of it. A rig the
/// file lacks has none (<see cref="For"/> returns the default array).
/// </summary>
public sealed class OuterHaloFit
{
    /// <summary>The radii the Moffat is fitted over, pixels: under 4 px the core profile owns the light, and the census stops at 40.</summary>
    public const int FitFromPx = 4, FitToPx = 40;

    private const double MinBeta = 0.6, MaxBeta = 4.0, MinAlphaPx = 1.0, MaxAlphaPx = 200.0;

    private readonly Dictionary<string, Dictionary<string, OuterHaloFitResult>> _rigs;

    private OuterHaloFit(Dictionary<string, Dictionary<string, OuterHaloFitResult>> rigs) => _rigs = rigs;

    /// <summary>The rigs the file held a fit for.</summary>
    public IReadOnlyCollection<string> Rigs => _rigs.Keys;

    /// <summary>Whether the file had a fit for <paramref name="rig"/>.</summary>
    public bool Has(string rig) => _rigs.ContainsKey(rig);

    /// <summary>The fit of <paramref name="rig"/>'s channel named <paramref name="channel"/> ("L", "R", "G" or "B"), or null.</summary>
    public OuterHaloFitResult? Of(string rig, string channel)
        => _rigs.TryGetValue(rig, out var channels) && channels.TryGetValue(channel, out var fit) ? fit : null;

    /// <summary>
    /// One halo per channel of <paramref name="channels"/> for <paramref name="rig"/>: a three-channel master takes its R, G and B
    /// where the file has them, else the luminance's for every channel; a one-channel master takes L. The default array for a
    /// rig the file lacks or a channel count it has no reading for.
    /// </summary>
    public ImmutableArray<OuterHalo> For(string rig, int channels)
    {
        if (!_rigs.TryGetValue(rig, out var fits))
        {
            return default;
        }
        string[] names = channels == 3 ? ["R", "G", "B"] : channels == 1 ? ["L"] : [];
        if (names.Length == 0)
        {
            return default;
        }
        var result = ImmutableArray.CreateBuilder<OuterHalo>(channels);
        foreach (var name in names)
        {
            if (fits.TryGetValue(name, out var own))
            {
                result.Add(own.Halo);
            }
            else if (fits.TryGetValue("L", out var luminance))
            {
                result.Add(luminance.Halo);
            }
            else
            {
                return default;
            }
        }
        return result.MoveToImmutable();
    }

    /// <summary>Reads <paramref name="path"/> and fits every rig and channel in it.</summary>
    public static async Task<OuterHaloFit> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return FromDocument(document.RootElement);
    }

    /// <summary>Fits every rig and channel in the profile table's root element.</summary>
    public static OuterHaloFit FromDocument(JsonElement root)
    {
        var rigs = new Dictionary<string, Dictionary<string, OuterHaloFitResult>>(StringComparer.Ordinal);
        if (!root.TryGetProperty("rigs", out var rigsElement))
        {
            return new OuterHaloFit(rigs);
        }
        foreach (var rig in rigsElement.EnumerateObject())
        {
            if (!rig.Value.TryGetProperty("real", out var real))
            {
                continue;
            }
            rig.Value.TryGetProperty("synth11", out var synth);
            var channels = new Dictionary<string, OuterHaloFitResult>(StringComparer.Ordinal);
            foreach (var channel in ChannelNames(real))
            {
                var pooled = Pooled(real, synth, channel);
                if (pooled is not null && Fit(pooled) is { } fit)
                {
                    channels[channel] = fit;
                }
            }
            if (channels.Count > 0)
            {
                rigs[rig.Name] = channels;
            }
        }
        return new OuterHaloFit(rigs);
    }

    private static HashSet<string> ChannelNames(JsonElement real)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in real.EnumerateObject())
        {
            if (group.Value.TryGetProperty("channels", out var channels))
            {
                foreach (var channel in channels.EnumerateObject())
                {
                    names.Add(channel.Name);
                }
            }
        }
        return names;
    }

    // The star-weighted mean over the core groups of the real median less the same group's synthetic one where there is one,
    // at every radius the file lists.
    private static double[]? Pooled(JsonElement real, JsonElement synth, string channel)
    {
        double[]? sum = null;
        var weight = 0.0;
        foreach (var group in real.EnumerateObject())
        {
            if (!group.Value.TryGetProperty("channels", out var channels) || !channels.TryGetProperty(channel, out var own)
                || !group.Value.TryGetProperty("stars", out var starsElement) || !starsElement.TryGetDouble(out var stars) || stars <= 0)
            {
                continue;
            }
            var measured = Median(own);
            if (measured is null)
            {
                continue;
            }
            if (synth.ValueKind == JsonValueKind.Object && synth.TryGetProperty(group.Name, out var synthGroup)
                && synthGroup.TryGetProperty("channels", out var synthChannels) && synthChannels.TryGetProperty(channel, out var synthOwn)
                && Median(synthOwn) is { } drawn && drawn.Length == measured.Length)
            {
                for (var i = 0; i < measured.Length; i++)
                {
                    measured[i] -= drawn[i];
                }
            }
            sum ??= new double[measured.Length];
            if (sum.Length != measured.Length)
            {
                continue;
            }
            for (var i = 0; i < measured.Length; i++)
            {
                sum[i] += stars * measured[i];
            }
            weight += stars;
        }
        if (sum is null || weight <= 0)
        {
            return null;
        }
        for (var i = 0; i < sum.Length; i++)
        {
            sum[i] /= weight;
        }
        return sum;
    }

    private static double[]? Median(JsonElement channel)
    {
        if (!channel.TryGetProperty("linear", out var linear) || !linear.TryGetProperty("median", out var median) || median.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var values = new double[median.GetArrayLength()];
        var i = 0;
        foreach (var v in median.EnumerateArray())
        {
            values[i++] = v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
        }
        return values;
    }

    /// <summary>
    /// Fits a Moffat to <paramref name="byRadius"/>, the excess at radius = index in pixels, over <see cref="FitFromPx"/> to
    /// <see cref="FitToPx"/> by least squares on the logarithm (so the 30 px tail, a thousandth of the 4 px level, counts as
    /// much as the 4 px one). Only positive values enter; the fit is null with fewer than five. Beta is searched over
    /// [<see cref="MinBeta"/>, <see cref="MaxBeta"/>] and alpha over [<see cref="MinAlphaPx"/>, <see cref="MaxAlphaPx"/>] by a
    /// shrinking grid, the amplitude solved exactly for each pair, so the result is deterministic.
    /// </summary>
    public static OuterHaloFitResult? Fit(ReadOnlySpan<double> byRadius)
    {
        var radii = new List<double>();
        var logs = new List<double>();
        for (var r = FitFromPx; r <= FitToPx && r < byRadius.Length; r++)
        {
            if (byRadius[r] > 0.0 && double.IsFinite(byRadius[r]))
            {
                radii.Add(r);
                logs.Add(Math.Log(byRadius[r]));
            }
        }
        if (radii.Count < 5)
        {
            return null;
        }

        double Cost(double logAlpha, double beta, out double logFraction)
        {
            var alpha2 = Math.Exp(2.0 * logAlpha);
            var mean = 0.0;
            for (var i = 0; i < radii.Count; i++)
            {
                mean += logs[i] + (beta * Math.Log(1.0 + (radii[i] * radii[i] / alpha2)));
            }
            logFraction = mean / radii.Count;
            var cost = 0.0;
            for (var i = 0; i < radii.Count; i++)
            {
                var e = logs[i] + (beta * Math.Log(1.0 + (radii[i] * radii[i] / alpha2))) - logFraction;
                cost += e * e;
            }
            return cost;
        }

        double lo1 = Math.Log(MinAlphaPx), hi1 = Math.Log(MaxAlphaPx), lo2 = MinBeta, hi2 = MaxBeta;
        double bestA = 0, bestB = 0, bestCost = double.PositiveInfinity;
        for (var pass = 0; pass < 8; pass++)
        {
            const int steps = 16;
            for (var i = 0; i <= steps; i++)
            {
                var la = lo1 + ((hi1 - lo1) * i / steps);
                for (var j = 0; j <= steps; j++)
                {
                    var b = lo2 + ((hi2 - lo2) * j / steps);
                    var cost = Cost(la, b, out _);
                    if (cost < bestCost)
                    {
                        (bestCost, bestA, bestB) = (cost, la, b);
                    }
                }
            }
            var spanA = (hi1 - lo1) / steps * 2.0;
            var spanB = (hi2 - lo2) / steps * 2.0;
            (lo1, hi1) = (Math.Max(Math.Log(MinAlphaPx), bestA - spanA), Math.Min(Math.Log(MaxAlphaPx), bestA + spanA));
            (lo2, hi2) = (Math.Max(MinBeta, bestB - spanB), Math.Min(MaxBeta, bestB + spanB));
        }
        Cost(bestA, bestB, out var logFraction);
        var halo = new OuterHalo(Math.Exp(logFraction), Math.Exp(bestA), bestB);
        var tail = 0.0;
        for (var r = 20; r <= 30 && r < byRadius.Length; r++)
        {
            if (byRadius[r] > 0.0)
            {
                tail = Math.Max(tail, Math.Abs(Math.Log(byRadius[r] / halo.AtRadius(r))));
            }
        }
        return new OuterHaloFitResult(halo, Math.Sqrt(bestCost / radii.Count), tail);
    }

    /// <summary>The fit in words for a log line.</summary>
    public string InWords(string rig)
        => !_rigs.TryGetValue(rig, out var fits) ? $"{rig}: none"
            : string.Join("; ", fits.Select(static kv => string.Create(CultureInfo.InvariantCulture,
                $"{kv.Key} {kv.Value.Halo.Fraction:G3} (1 + r^2 / {kv.Value.Halo.AlphaPx:F1}^2)^-{kv.Value.Halo.Beta:F2}, rms {kv.Value.RmsLog:F2} ln, tail {kv.Value.TailLog:F2} ln")));
}
