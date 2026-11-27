using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>
/// The routine of <c>tianwen comet track</c>: the track of the comet a folder of lights was taken of, asked of JPL
/// Horizons once and written as a <see cref="CometTrack"/> file a stack then registers on, offline and the same way
/// every time (#2489).
/// </summary>
/// <remarks>
/// The window is the whole night's: the exposure of every light a stack of the folder takes, so one file serves each
/// group a stack makes of them (a group's own window is inside it; <see cref="CometTrack.RefusalFor"/> holds that), and
/// each group's rate is fitted on the samples that bracket its own frames (<see cref="CometTrack.Bracketing"/>). The
/// lights are the stack's own scan's (<see cref="StackingPipeline.ScanFramesAsync"/>), so another tool's composite beside
/// them (Astro Pixel Processor keeps <c>IMAGETYP=LIGHT</c> and states hours of <c>EXPTIME</c>) neither names a second
/// comet nor stretches the window. The request is built as the stack builds its own
/// (<see cref="CometTrackRequest.TryBuild"/>): the same designation rule, the same refusal of an unknown site, the same
/// padding and cadence.
/// </remarks>
public static class CometTrackFetch
{
    /// <summary>
    /// The Horizons request for the lights a stack of <paramref name="framesFolder"/> takes, with how many there are;
    /// the refusal in words in place of a request where there is none to make: no such folder, no light, lights of more than one comet
    /// with none named, lights from more than one site, or a site or a designation the frames do not give.
    /// </summary>
    /// <param name="framesFolder">The night's folder; read recursively, headers only, as the stack scans it.</param>
    /// <param name="designation">The comet, e.g. <c>10P</c> or <c>C/2025 R2</c>; null or empty reads it off the lights'
    /// <c>OBJECT</c>.</param>
    /// <param name="cancellationToken">Ends the scan.</param>
    public static async Task<(CometTrackRequest Request, int LightCount, string? Refusal)> RequestForAsync(string framesFolder, string? designation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(framesFolder);
        if (!Directory.Exists(framesFolder))
        {
            return (default, 0, $"there is no folder {framesFolder}");
        }

        // A frame named as a suspected cloud is a light of the night a stack may be asked to keep
        // (--include-maybe-cloud), so the window covers it; an integration a stack would only keep when asked
        // (--include-integrations) is no exposure of this night's and is left out.
        var scan = await StackingPipeline.ScanFramesAsync(framesFolder, outputDir: null, includeIntegrations: false,
            includeSuspectedCloud: true, reassigned: default, cancellationToken);
        return RequestFor(framesFolder, designation, scan.Frames.Where(static frame => frame.FrameType is FrameType.Light));
    }

    private static (CometTrackRequest Request, int LightCount, string? Refusal) RequestFor(string framesFolder, string? designation,
        IEnumerable<FrameInfo> lights)
    {
        var lightCount = 0;
        var first = DateTimeOffset.MaxValue;
        var last = DateTimeOffset.MinValue;
        var objects = new SortedSet<string>(StringComparer.Ordinal);
        (double Lat, double Lon, double Elev)? site = null;
        foreach (var frame in lights)
        {
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
            else if (!(double.IsNaN(seen.Lat) && double.IsNaN(here.Lat)) && !CometTrack.SameSite(seen.Lat, seen.Lon, here.Lat, here.Lon))
            {
                return (default, lightCount, $"the lights under {framesFolder} were taken at more than one site, and a comet's track is the one seen from one: "
                    + "ask for each site's frames apart");
            }
        }

        if (lightCount == 0 || site is not { } at)
        {
            return (default, lightCount, $"there is no light under {framesFolder}");
        }

        if (string.IsNullOrWhiteSpace(designation) && objects.Count > 1)
        {
            return (default, lightCount, $"the lights under {framesFolder} are of {string.Join(", ", objects)}: name the comet (--designation)");
        }

        if (CometTrackRequest.TryBuild(designation, objects.FirstOrDefault(), at.Lat, at.Lon, at.Elev, first, last) is not { } built)
        {
            return (default, lightCount, double.IsNaN(at.Lat) || double.IsNaN(at.Lon)
                ? $"the lights under {framesFolder} state no site (SITELAT, SITELONG), and a comet's track is the one seen from the frames' own site"
                : $"'{(string.IsNullOrWhiteSpace(designation) ? objects.FirstOrDefault() ?? "" : designation)}' is no comet designation Horizons can be asked for (10P, C/2025 R2): name the comet (--designation)");
        }

        return (built, lightCount, null);
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
