using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging;
using TianWen.Lib.IO;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>
/// The routine of <c>tianwen comet track</c>: the track of the comet a folder of lights was taken of, asked of JPL
/// Horizons once and written as a <see cref="CometTrack"/> file a stack then registers on, offline and the same way
/// every time (#2489).
/// </summary>
/// <remarks>
/// The window is the whole night's: every light's exposure under the folder, so one file serves each group a stack
/// makes of them (a group's own window is inside it; <see cref="CometTrack.RefusalFor"/> holds that). The request is
/// built as the stack builds its own (<see cref="CometTrackRequest.TryBuild"/>): the same designation rule, the same
/// refusal of an unknown site, the same padding and cadence.
/// </remarks>
public static class CometTrackFetch
{
    /// <summary>
    /// The Horizons request for the lights under <paramref name="framesFolder"/>; false with the reason in words where
    /// there is none to make: no light, lights of more than one comet with none named, lights from more than one site,
    /// or a site or a designation the frames do not give.
    /// </summary>
    /// <param name="framesFolder">The night's folder; read recursively, headers only.</param>
    /// <param name="designation">The comet, e.g. <c>10P</c> or <c>C/2025 R2</c>; null or empty reads it off the lights'
    /// <c>OBJECT</c>.</param>
    /// <param name="request">The request, where there is one.</param>
    /// <param name="refusal">Why there is none.</param>
    /// <param name="lightCount">How many lights the window was taken over.</param>
    public static bool TryRequestFor(string framesFolder, string? designation, out CometTrackRequest request, out int lightCount,
        [NotNullWhen(false)] out string? refusal)
    {
        ArgumentException.ThrowIfNullOrEmpty(framesFolder);
        request = default;
        lightCount = 0;
        var first = DateTimeOffset.MaxValue;
        var last = DateTimeOffset.MinValue;
        var objects = new SortedSet<string>(StringComparer.Ordinal);
        (double Lat, double Lon, double Elev)? site = null;
        foreach (var path in FileEnumeration.EnumerateFiles(framesFolder, [".fits", ".fit", ".fts", ".fz", ".gz"], recursive: true).Order(StringComparer.Ordinal))
        {
            // A light the camera took, never a master a stack wrote beside them.
            if (!Image.IsFitsPath(path) || !Image.TryReadFitsHeader(path, out var frame) || frame.FrameType is not FrameType.Light || frame.IsMaster)
            {
                continue;
            }

            var meta = frame.Meta;
            lightCount++;
            if (meta.ExposureStartTime < first)
            {
                first = meta.ExposureStartTime;
            }

            if (meta.ExposureStartTime + meta.ExposureDuration > last)
            {
                last = meta.ExposureStartTime + meta.ExposureDuration;
            }

            if (!string.IsNullOrWhiteSpace(meta.ObjectName))
            {
                objects.Add(meta.ObjectName.Trim());
            }

            // One site for the night: a track is the comet seen from one place.
            (double Lat, double Lon, double Elev) here = (meta.Latitude, meta.Longitude, meta.SiteElevation);
            if (site is not { } seen)
            {
                site = here;
            }
            else if (!(double.IsNaN(seen.Lat) && double.IsNaN(here.Lat))
                && !(Math.Abs(seen.Lat - here.Lat) <= CometTrack.SiteToleranceDeg && Math.Abs(seen.Lon - here.Lon) <= CometTrack.SiteToleranceDeg))
            {
                refusal = $"the lights under {framesFolder} were taken at more than one site, and a comet's track is the one seen from one: "
                    + "ask for each site's frames apart";
                return false;
            }
        }

        if (lightCount == 0 || site is not { } at)
        {
            refusal = $"there is no light under {framesFolder}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(designation) && objects.Count > 1)
        {
            refusal = $"the lights under {framesFolder} are of {string.Join(", ", objects)}: name the comet (--designation)";
            return false;
        }

        if (CometTrackRequest.TryBuild(designation, objects.FirstOrDefault(), at.Lat, at.Lon, at.Elev, first, last) is not { } built)
        {
            refusal = double.IsNaN(at.Lat) || double.IsNaN(at.Lon)
                ? $"the lights under {framesFolder} state no site (SITELAT, SITELONG), and a comet's track is the one seen from the frames' own site"
                : $"'{(string.IsNullOrWhiteSpace(designation) ? objects.FirstOrDefault() ?? "" : designation)}' is no comet designation Horizons can be asked for (10P, C/2025 R2): name the comet (--designation)";
            return false;
        }

        request = built;
        refusal = null;
        return true;
    }

    /// <summary>The track Horizons answers <paramref name="request"/> with; null with the reason where it answers fewer
    /// than two positions (a rate needs two).</summary>
    public static Task<(CometTrack? Track, string? Refusal)> FetchAsync(CometTrackRequest request, ILogger logger, CancellationToken cancellationToken)
        => FetchAsync(request, new HorizonsObserverSource(logger), cancellationToken);

    /// <summary>As <see cref="FetchAsync(CometTrackRequest, ILogger, CancellationToken)"/>, from <paramref name="source"/>.</summary>
    internal static async Task<(CometTrack? Track, string? Refusal)> FetchAsync(CometTrackRequest request, IHorizonsObserverSource source,
        CancellationToken cancellationToken)
    {
        var samples = await source.TryFetchTrackAsync(
            request.Designation, request.SiteLatDeg, request.SiteLonDeg, request.SiteElevMetres, request.Start, request.Stop, request.Step,
            cancellationToken);
        return samples.Length < 2
            ? (null, $"Horizons answered {samples.Length} position(s) of {request.Designation} from {request.Start:u} to {request.Stop:u}, and a track needs two")
            : (CometTrack.From(request, samples), null);
    }
}
