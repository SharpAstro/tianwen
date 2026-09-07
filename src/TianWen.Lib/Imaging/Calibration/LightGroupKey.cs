using System;
using System.Collections.Generic;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Equality-able key partitioning a folder of light frames into independently
/// stackable sets. A folder may mix multiple targets (NINA writes all lights
/// to a single LIGHT directory regardless of target), and frames of different
/// targets never register against each other: they look at different sky.
///
/// <para>The key wraps a <see cref="MasterGroupKey"/> (which encodes the
/// sensor configuration, exposure, temperature, filter, dimensions, gain, and
/// offset) plus the FITS <c>OBJECT</c> header value. When <see cref="ObjectName"/>
/// is empty, two frames group together iff their calibration keys match; 
/// identical to the legacy behavior. When set, frames of different targets
/// are forced into separate groups even when otherwise identical.</para>
///
/// <para>Calibration master grouping continues to use <see cref="MasterGroupKey"/>
/// directly: bias / dark / flat masters are sky-independent and should be
/// shared across all targets imaged with the same sensor configuration.</para>
/// </summary>
/// <param name="CalibrationKey">The sensor / exposure / filter signature used
/// to find the matching calibration masters for this light group.</param>
/// <param name="ObjectName">FITS <c>OBJECT</c> header value, empty when unset.</param>
public sealed record LightGroupKey(MasterGroupKey CalibrationKey, string ObjectName)
{
    /// <summary>Derives the light-group key from a single frame's parsed header.</summary>
    public static LightGroupKey FromFrame(FrameInfo frame)
        => new(MasterGroupKey.FromFrame(frame), frame.Meta.ObjectName);

    /// <summary>
    /// A light-group key for every frame, with frames of one target whose sensor temperatures differ
    /// by no more than <paramref name="temperatureToleranceC"/> between neighbours placed in ONE group.
    /// </summary>
    /// <remarks>
    /// <see cref="FromFrame"/> carries <see cref="MasterGroupKey.TemperatureC"/> rounded to the degree,
    /// so a cooler that drifts across a degree boundary during one night splits that night into as many
    /// masters as degrees it crossed, each with its own reference and canvas (a 2025-10-15 SV605CC
    /// session read 13.7 to 12.1 C and stacked as 49, 18 and 4 frames). With a positive tolerance the
    /// frames that agree on everything BUT temperature are sorted by it and cut only where two
    /// consecutive readings are further apart than the tolerance: a drift is one cluster, a different
    /// night's 8 C is another. Each cluster's key carries the rounded MEDIAN of its temperatures, which
    /// is what the dark match and the slug then see. A tolerance of zero (or less) returns exactly
    /// <see cref="FromFrame"/> for every frame, so the default path is unchanged; a frame without a
    /// temperature keeps its null and is never merged with one that has one.
    /// </remarks>
    public static IReadOnlyDictionary<FrameInfo, LightGroupKey> Assign(IReadOnlyList<FrameInfo> lights, double temperatureToleranceC)
    {
        var keys = new Dictionary<FrameInfo, LightGroupKey>(lights.Count);
        if (!(temperatureToleranceC > 0))
        {
            foreach (var light in lights)
            {
                keys[light] = FromFrame(light);
            }

            return keys;
        }

        // Everything but temperature first, then temperature within each of those.
        var byBase = new Dictionary<LightGroupKey, List<FrameInfo>>();
        foreach (var light in lights)
        {
            var exact = FromFrame(light);
            var baseKey = exact with { CalibrationKey = exact.CalibrationKey with { TemperatureC = null } };
            if (!byBase.TryGetValue(baseKey, out var list))
            {
                byBase[baseKey] = list = [];
            }

            list.Add(light);
        }

        foreach (var (baseKey, members) in byBase)
        {
            var withTemperature = new List<(float Temperature, FrameInfo Frame)>(members.Count);
            foreach (var member in members)
            {
                var t = member.Meta.CCDTemperature;
                if (float.IsNaN(t))
                {
                    // No temperature is not "any temperature": it stays its own group, as FromFrame has it.
                    keys[member] = baseKey;
                }
                else
                {
                    withTemperature.Add((t, member));
                }
            }

            withTemperature.Sort(static (a, b) => a.Temperature.CompareTo(b.Temperature));
            var start = 0;
            for (var i = 1; i <= withTemperature.Count; i++)
            {
                var cutHere = i == withTemperature.Count
                    || withTemperature[i].Temperature - withTemperature[i - 1].Temperature > temperatureToleranceC;
                if (!cutHere)
                {
                    continue;
                }

                var count = i - start;
                var mid = start + (count / 2);
                var median = count % 2 == 1
                    ? withTemperature[mid].Temperature
                    : 0.5f * (withTemperature[mid - 1].Temperature + withTemperature[mid].Temperature);
                var clusterKey = baseKey with { CalibrationKey = baseKey.CalibrationKey with { TemperatureC = (int)Math.Round(median) } };
                for (var j = start; j < i; j++)
                {
                    keys[withTemperature[j].Frame] = clusterKey;
                }

                start = i;
            }
        }

        return keys;
    }

    /// <summary>
    /// Filename-safe slug for the group, of the form
    /// <c>&lt;object&gt;_&lt;calibration-slug&gt;</c>. Falls back to just the
    /// calibration slug when <see cref="ObjectName"/> is empty.
    /// </summary>
    public string Slug()
    {
        var calSlug = CalibrationKey.Slug();
        if (string.IsNullOrEmpty(ObjectName))
        {
            return calSlug;
        }
        return SanitizeForFilename(ObjectName) + "_" + calSlug;
    }

    private static string SanitizeForFilename(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is '+' or '-' or '_') sb.Append(c);
            // skip everything else (spaces, slashes, unicode glyphs)
        }
        return sb.Length > 0 ? sb.ToString() : "object";
    }
}
