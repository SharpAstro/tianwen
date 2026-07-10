using System;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;

namespace TianWen.AI.MCP.Tools;

/// <summary>
/// Catalog lookup tools. A single <see cref="Lookup"/> resolves any designation -- fixed catalog objects
/// (NGC/IC/Messier/HIP/HD/Tycho-2/common name) via the object DB, and JPL comets via the cached
/// ephemeris + a real observability computation (position, magnitude, and the best night to observe from
/// a given site), rather than a bare not-found / static row.
/// </summary>
[McpServerToolType]
public class CatalogTools
{
    [McpServerTool, Description(
        "Resolve a sky object and describe it. Fixed objects (NGC/IC/Messier/Caldwell e.g. 'NGC 7331', "
        + "'M 31'; HIP/HD numbers; Tycho-2 'TYC 1799-1441-1'; common names 'Vega') return RA/Dec/Mag/type "
        + "+ cross-refs. JPL comets (numbered '10P', '12P/Pons-Brooks'; provisional 'C/2023 A3') return a "
        + "LIVE two-body ephemeris (J2000 RA/Dec, predicted vmag, heliocentric/geocentric distance, "
        + "perihelion date + brightening/fading trend). Supply latitude+longitude (degrees, east +) to also "
        + "get observability from that site: for a comet, tonight's dark window + peak altitude + a weekly "
        + "outlook and the BEST night to observe over the coming ~6 months (answers 'when is the best time "
        + "to observe 10P/Tempel in my location'); for a fixed object, rise/transit/set. Times are shown in "
        + "the site's local timezone (derived from the coordinates). Optional 'whenIso' anchors the instant "
        + "(default now); 'minAltitudeDeg' sets the usable-horizon floor (default 20).")]
    public static async Task<string> Lookup(
        ICelestialObjectDB db,
        ICometRepository comets,
        ITimeProvider timeProvider,
        [Description("Catalog designation, comet designation, or common name.")] string designation,
        [Description("Observing-site latitude in degrees (north +). Omit to skip observability.")] double? latitude = null,
        [Description("Observing-site longitude in degrees (east +). Omit to skip observability.")] double? longitude = null,
        [Description("ISO-8601 instant to evaluate at (e.g. '2026-09-01T12:00:00Z'). Default: now.")] string? whenIso = null,
        [Description("Minimum usable altitude in degrees for the 'observable' flag + best-night pick. Default 20.")] double minAltitudeDeg = 20.0,
        CancellationToken ct = default)
    {
        var when = ParseWhen(whenIso, timeProvider);
        var hasSite = latitude is { } siteLat && longitude is { } siteLon
            && siteLat is >= -90 and <= 90 && siteLon is >= -180 and <= 180;
        var lat = latitude ?? double.NaN;
        var lon = longitude ?? double.NaN;

        // Comet path first. The parse is offline (no repo touch) so a plain star/DSO lookup never pays the
        // comet fetch; the repo is only loaded once the designation is comet-shaped AND a known comet.
        if (CometDesignation.TryParse(designation, out var cometDes) && cometDes.TryToCatalogIndex(out var cometIdx))
        {
            await comets.EnsureLoadedAsync(ct);
            if (comets.TryGet(cometIdx, out var elements))
            {
                return FormatComet(elements, when, hasSite, lat, lon, minAltitudeDeg, timeProvider);
            }
        }

        // Init is idempotent + fast-path-protected; first call pays the Tycho-2 bulk-decode cost.
        await db.InitDBAsync(waitForTycho2BulkLoad: true, ct);

        if (!db.TryLookupByIndex(designation, out var obj))
        {
            return $"NOT FOUND: {designation}";
        }

        return hasSite && !double.IsNaN(obj.RA) && !double.IsNaN(obj.Dec)
            ? $"{obj}\n\n{FormatFixedObservability(obj.RA, obj.Dec, when, lat, lon, timeProvider)}"
            : obj.ToString();
    }

    private static string FormatComet(
        in CometElements el, DateTimeOffset when,
        bool hasSite, double lat, double lon, double minAlt, ITimeProvider tp)
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine($"{el.DisplayName}  [Comet]");

        if (!CometEphemeris.TryGetEquatorialJ2000(el, when, out var ra, out var dec, out var r, out var delta))
        {
            sb.AppendLine($"Ephemeris solve failed at {when.UtcDateTime:yyyy-MM-dd HH:mm} UTC.");
            return sb.ToString();
        }
        var mag = CometEphemeris.PredictTotalMagnitude(el, r, delta);

        sb.AppendLine($"Ephemeris @ {when.UtcDateTime:yyyy-MM-dd HH:mm} UTC (two-body, arcminute-class):");
        sb.AppendLine($"  RA  {CoordinateUtils.HoursToHMS(ra)}   Dec {CoordinateUtils.DegreesToDMS(dec)}   (J2000)");
        sb.AppendLine($"  vmag {FormatMag(mag)}   r {r:F3} AU   delta {delta:F3} AU");

        var perihelion = JdTtToUtc(el.PerihelionJdTt);
        var dToPeri = (perihelion - when).TotalDays;
        var periPhrase = Math.Abs(dToPeri) < 1.0 ? "at perihelion"
            : dToPeri > 0 ? $"{dToPeri:F0} d before perihelion"
            : $"{-dToPeri:F0} d after perihelion";
        var trend = "";
        if (!double.IsNaN(mag)
            && CometEphemeris.TryGetEquatorialJ2000WithMagnitude(el, when.AddDays(14), out _, out _, out var magLater)
            && !double.IsNaN(magLater))
        {
            trend = magLater < mag - 0.05 ? ", brightening"
                : magLater > mag + 0.05 ? ", fading"
                : ", ~steady";
        }
        sb.AppendLine($"  Perihelion {perihelion.UtcDateTime:yyyy-MM-dd} ({periPhrase}{trend})");

        if (!hasSite)
        {
            sb.AppendLine("Provide latitude + longitude for rise/set + the best time to observe.");
            return sb.ToString();
        }

        var transform = BuildTransform(lat, lon, when, tp);
        var tz = transform.TryGetSiteTimeZone(out var off, out _) ? off : TimeSpan.Zero;
        sb.AppendLine($"Observability @ lat {lat:F3}, lon {lon:F3} (UTC{FormatOffset(tz)}), horizon >= {minAlt:F0} deg:");

        if (!CometObservability.TryFindBest(el, transform, when, minAlt, out var best, out var samples) || samples.Length == 0)
        {
            sb.AppendLine("  Could not compute an observability window (ephemeris unsolvable in range).");
            return sb.ToString();
        }

        var tonight = samples[0];
        sb.AppendLine($"  Tonight: dark {tonight.DarkStartUtc.ToOffset(tz):HH:mm}-{tonight.DarkEndUtc.ToOffset(tz):HH:mm} local; "
            + $"peak {tonight.MaxAltitudeDeg:F0} deg at {tonight.BestTimeUtc.ToOffset(tz):HH:mm} local; vmag {FormatMag(tonight.VMag)} "
            + (tonight.Observable ? "[observable]" : "[below horizon limit]"));
        sb.AppendLine($"  BEST: {best.BestTimeUtc.ToOffset(tz):yyyy-MM-dd HH:mm} local -- vmag {FormatMag(best.VMag)}, "
            + $"peaks {best.MaxAltitudeDeg:F0} deg "
            + (best.Observable ? "[well placed]" : "[stays low -- best available]"));

        sb.AppendLine("  Outlook (weekly):  date        vmag   alt   best(local)");
        foreach (var n in samples)
        {
            sb.AppendLine($"    {n.NightLocal:yyyy-MM-dd}  {FormatMag(n.VMag),5}   {n.MaxAltitudeDeg,3:F0}   "
                + $"{n.BestTimeUtc.ToOffset(tz):HH:mm}  {(n.Observable ? "ok" : "--")}");
        }
        return sb.ToString();
    }

    private static string FormatFixedObservability(
        double ra, double dec, DateTimeOffset when, double lat, double lon, ITimeProvider tp)
    {
        var transform = BuildTransform(lat, lon, when, tp);
        var tz = transform.TryGetSiteTimeZone(out var off, out _) ? off : TimeSpan.Zero;
        var sb = new StringBuilder(256);
        sb.AppendLine($"Observability @ lat {lat:F3}, lon {lon:F3} (UTC{FormatOffset(tz)}):");

        if (!RiseTransitSetHelper.TryComputeRiseTransitSet(ra, dec, lat, lon, when,
                out var rise, out var transit, out var set, out var circumpolar, out var neverRises))
        {
            sb.AppendLine("  (rise/set unavailable)");
        }
        else if (neverRises)
        {
            sb.AppendLine("  Never rises from this site.");
        }
        else if (circumpolar)
        {
            sb.AppendLine($"  Circumpolar; transit {transit.ToOffset(tz):yyyy-MM-dd HH:mm} local.");
        }
        else
        {
            sb.AppendLine($"  Rise {rise.ToOffset(tz):HH:mm}  Transit {transit.ToOffset(tz):HH:mm}  Set {set.ToOffset(tz):HH:mm} local.");
        }
        return sb.ToString();
    }

    private static Transform BuildTransform(double lat, double lon, DateTimeOffset when, ITimeProvider tp)
        => new(tp)
        {
            SiteLatitude = lat,
            SiteLongitude = lon,
            SiteElevation = 0.0,
            SiteTemperature = 15.0,
            DateTimeOffset = when.ToUniversalTime(),
        };

    private static DateTimeOffset ParseWhen(string? iso, ITimeProvider tp)
        => !string.IsNullOrWhiteSpace(iso)
           && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var w)
            ? w
            : tp.GetUtcNow();

    // JD(TT) -> UTC as a display date. TT-UTC (~69 s) and the OADate epoch offset are both far below the
    // day-level precision we print, so this is exact enough for "perihelion 2026-09-12".
    private static DateTimeOffset JdTtToUtc(double jdTt)
        => new(DateTime.SpecifyKind(DateTime.FromOADate(jdTt - 2415018.5), DateTimeKind.Utc));

    private static string FormatMag(double mag) => double.IsNaN(mag) ? "  -- " : mag.ToString("F1", CultureInfo.InvariantCulture);

    private static string FormatOffset(TimeSpan tz)
        => (tz < TimeSpan.Zero ? "-" : "+") + tz.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
}
