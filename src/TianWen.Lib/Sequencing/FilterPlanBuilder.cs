using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Builds filter exposure plans for observation scheduling.
/// The plan is ordered as an "altitude ladder": narrowband first (best at low altitude),
/// broadband last (best at high altitude). The imaging loop traverses forward while the
/// target is ascending (rising → transit) and reverses after meridian flip (transit → setting).
/// </summary>
public static class FilterPlanBuilder
{
    /// <summary>
    /// Builds a single-entry filter plan for backward compatibility when no filter wheel is present.
    /// </summary>
    public static ImmutableArray<FilterExposure> BuildSingleFilterPlan(TimeSpan subExposure)
    {
        return [new FilterExposure(FilterPosition: -1, SubExposure: subExposure)];
    }

    /// <summary>
    /// Builds an altitude-ladder filter plan: narrowband first (low altitude tolerant),
    /// broadband last (needs high altitude for best seeing). The imaging loop traverses
    /// this plan forward while the target is ascending and reverses after meridian flip.
    /// </summary>
    /// <param name="filters">Installed filters from the filter wheel.</param>
    /// <param name="broadbandExposure">Sub-exposure duration for broadband filters.</param>
    /// <param name="narrowbandExposure">Sub-exposure duration for narrowband filters.</param>
    /// <param name="framesPerFilter">Number of frames per filter before switching.</param>
    public static ImmutableArray<FilterExposure> BuildAutoFilterPlan(
        IReadOnlyList<InstalledFilter> filters,
        TimeSpan broadbandExposure,
        TimeSpan narrowbandExposure,
        int framesPerFilter = 10)
    {
        if (filters.Count == 0)
        {
            return BuildSingleFilterPlan(broadbandExposure);
        }

        var narrowband = new List<(int Position, InstalledFilter Filter)>();
        var rgb = new List<(int Position, InstalledFilter Filter)>();
        var luminance = -1;

        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            if (IsNarrowband(filter.Filter))
            {
                narrowband.Add((i, filter));
            }
            else if (filter.Filter.Bandpass == Bandpass.Luminance)
            {
                luminance = i;
            }
            else if (filter.Filter != Filter.None && filter.Filter != Filter.Unknown)
            {
                rgb.Add((i, filter));
            }
        }

        var builder = ImmutableArray.CreateBuilder<FilterExposure>();

        // Altitude ladder: narrowband → RGB → Luminance (top)
        // Narrowband tolerates low altitude (atmospheric dispersion doesn't affect narrow bands)
        // RGB needs decent altitude
        // Luminance at the top: benefits most from peak seeing, and is the stacking foundation
        foreach (var (pos, _) in narrowband)
        {
            builder.Add(new FilterExposure(pos, narrowbandExposure, framesPerFilter));
        }

        foreach (var (pos, _) in rgb)
        {
            builder.Add(new FilterExposure(pos, broadbandExposure, framesPerFilter));
        }

        if (luminance >= 0)
        {
            builder.Add(new FilterExposure(luminance, broadbandExposure, framesPerFilter));
        }

        if (builder.Count == 0)
        {
            return BuildSingleFilterPlan(broadbandExposure);
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Overload that accepts (and ignores) targetAltitudeDeg for backward compatibility
    /// with existing scheduler call sites. The altitude-based ordering is now handled
    /// by the imaging loop's forward/reverse traversal around meridian transit.
    /// </summary>
    public static ImmutableArray<FilterExposure> BuildAutoFilterPlan(
        IReadOnlyList<InstalledFilter> filters,
        TimeSpan broadbandExposure,
        TimeSpan narrowbandExposure,
        double targetAltitudeDeg,
        int framesPerFilter = 10)
    {
        return BuildAutoFilterPlan(filters, broadbandExposure, narrowbandExposure, framesPerFilter);
    }

    /// <summary>
    /// Returns the index of the best filter to use as focus reference.
    /// For mirror-based / CA-free designs: luminance (widest bandpass, most stars).
    /// For refractive designs with non-zero offsets: luminance (offsets handle the rest).
    /// For refractive designs with no offsets: -1 (caller should focus on scheduled filter).
    /// </summary>
    /// <param name="filters">Installed filters from the filter wheel.</param>
    /// <param name="opticalDesign">The telescope's optical design.</param>
    /// <returns>Filter position index, or -1 if no suitable reference filter (focus on scheduled filter instead).</returns>
    public static int GetReferenceFilter(IReadOnlyList<InstalledFilter> filters, OpticalDesign opticalDesign)
    {
        if (filters.Count == 0)
        {
            return -1;
        }

        // Find the luminance filter
        var lumIndex = -1;
        for (var i = 0; i < filters.Count; i++)
        {
            if (filters[i].Filter.Bandpass == Bandpass.Luminance)
            {
                lumIndex = i;
                break;
            }
        }

        if (!opticalDesign.NeedsFocusAdjustmentPerFilter)
        {
            // Pure mirror / astrograph: luminance is safe, no offset needed
            return lumIndex >= 0 ? lumIndex : 0;
        }

        // Refractive design: check if non-zero offsets are defined
        var hasNonZeroOffsets = filters.Any(f => f.Position != 0);

        if (hasNonZeroOffsets)
        {
            // Offsets defined: focus on luminance, apply deltas for other filters
            return lumIndex >= 0 ? lumIndex : 0;
        }

        // Refractive design with no offsets: must focus on each filter directly
        return -1;
    }

    /// <summary>
    /// Returns true if the filter is narrowband (Ha, Hb, OIII, SII or combinations thereof).
    /// </summary>
    public static bool IsNarrowband(Filter filter)
    {
        var bandpass = filter.Bandpass;

        if (bandpass == Bandpass.None || bandpass == Bandpass.Luminance)
        {
            return false;
        }

        // Narrowband if it has any narrowband bits and no broadband RGB bits
        var hasNarrowbandBits = (bandpass & (Bandpass.Ha | Bandpass.Hb | Bandpass.OIII | Bandpass.SII)) != 0;
        var hasBroadbandBits = (bandpass & (Bandpass.Red | Bandpass.Green | Bandpass.Blue)) != 0;

        return hasNarrowbandBits && !hasBroadbandBits;
    }
}
