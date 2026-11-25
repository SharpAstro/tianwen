using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>
/// The ephemeris a comet-aligned stack registers on, kept as a file: what JPL Horizons answered for one body, seen from
/// one site, over one night (<c>tianwen comet track</c> writes it). A stack handed one (<c>tianwen stack --comet-track</c>,
/// the workbench's <c>comet-stack</c> step) asks no network; it still plate-solves its reference frame to project the
/// track onto its pixels.
/// </summary>
/// <remarks>
/// <para><b>Why a file, and not a question asked while stacking</b> (the owner's call of 2026-11-25, #2489). A workbench
/// step's key is fixed before it runs and its result is reused under that key. Horizons' answer is not fixed: JPL
/// revises an orbit, and a run with no network gets none. As a file the ephemeris is data, read by its bytes like any
/// original, so one key always means one track, an offline run keeps its star layer and composite (which
/// <c>--comet-rate</c>, a bare rate with no position, cannot give), and a test needs no network.</para>
/// <para><b>Nothing in it says when it was fetched</b>, so asking twice for an answer that has not changed writes the
/// same bytes and keeps every step's key.</para>
/// <para><b>Topocentric, from the site it records.</b> A track asked from another place is refused for frames taken at
/// this one (<see cref="RefusalFor"/>): diurnal parallax moved 10P by 2.7 px across one night, 25 times the
/// registration residual (<see cref="HorizonsObserverSource"/>).</para>
/// </remarks>
/// <param name="SchemaVersion">The file's shape; <see cref="CurrentSchemaVersion"/> is the one this build reads.</param>
/// <param name="Source">Who answered: <see cref="Horizons"/>.</param>
/// <param name="Designation">The body, in the canonical form Horizons was asked with (<c>10P</c>, <c>C/2025 R2</c>).</param>
/// <param name="SiteLatDeg">The site's latitude, degrees north.</param>
/// <param name="SiteLonDeg">The site's longitude, degrees EAST (Horizons' and FITS <c>SITELONG</c>'s convention).</param>
/// <param name="SiteElevMetres">The site's elevation, metres.</param>
/// <param name="Samples">The topocentric astrometric positions, in time order.</param>
public sealed record CometTrack(
    int SchemaVersion,
    string Source,
    string Designation,
    double SiteLatDeg,
    double SiteLonDeg,
    double SiteElevMetres,
    ImmutableArray<EphemerisSample> Samples)
{
    /// <summary>The file's shape this build writes and reads.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The one source a track comes from today.</summary>
    public const string Horizons = "JPL Horizons";

    /// <summary>
    /// How far, in degrees of latitude or longitude, the frames' site may be from the track's. 0.1 degree is about
    /// 11 km, and parallax over 11 km is 2.7 px times 11 / 6371, about 0.005 px on 10P: nothing. A track asked from
    /// another observatory, or from the geocentre, is out by far more.
    /// </summary>
    public const double SiteToleranceDeg = 0.1;

    /// <summary>The track Horizons answered <paramref name="request"/> with.</summary>
    public static CometTrack From(CometTrackRequest request, ImmutableArray<EphemerisSample> samples)
        => new(CurrentSchemaVersion, Horizons, request.Designation, request.SiteLatDeg, request.SiteLonDeg, request.SiteElevMetres,
            samples.Sort(static (a, b) => a.TimeUtc.CompareTo(b.TimeUtc)));

    /// <summary>
    /// Why this track cannot serve frames taken at the site (<paramref name="siteLatDeg"/>, <paramref name="siteLonDeg"/>)
    /// from <paramref name="firstExposureStart"/> to <paramref name="lastExposureEnd"/>, in words; null when it can.
    /// </summary>
    /// <remarks>
    /// The samples must cover the frames: the rate is a straight line fitted through them, and an extrapolated end is
    /// where a track's curvature is least held (the reason <see cref="CometTrackRequest"/> pads its window).
    /// </remarks>
    public string? RefusalFor(double siteLatDeg, double siteLonDeg, DateTimeOffset firstExposureStart, DateTimeOffset lastExposureEnd)
    {
        if (double.IsNaN(siteLatDeg) || double.IsNaN(siteLonDeg))
        {
            return $"the frames state no site (SITELAT, SITELONG), so whether the track of {Designation} was asked from their place cannot be told";
        }

        // Longitude across the antimeridian: 179.99 E and 179.99 W are 0.02 degrees apart.
        var lonApart = Math.Abs(((siteLonDeg - SiteLonDeg) % 360.0 + 540.0) % 360.0 - 180.0);
        if (Math.Abs(siteLatDeg - SiteLatDeg) > SiteToleranceDeg || lonApart > SiteToleranceDeg)
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"the track of {Designation} was asked from ({SiteLatDeg:F4}, {SiteLonDeg:F4}) and the frames were taken at ({siteLatDeg:F4}, {siteLonDeg:F4}): a comet's track is the one seen from the frames' own site");
        }

        if (Samples[0].TimeUtc > firstExposureStart || Samples[^1].TimeUtc < lastExposureEnd)
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"the track of {Designation} runs from {Samples[0].TimeUtc:u} to {Samples[^1].TimeUtc:u} and the frames from {firstExposureStart:u} to {lastExposureEnd:u}: it has to cover every frame");
        }

        return null;
    }

    /// <summary>Writes the track to <paramref name="path"/> whole or not at all: a half-written track that still parses
    /// would fit a rate to the samples it kept.</summary>
    public async Task WriteAsync(string path, CancellationToken cancellationToken = default)
    {
        var tmp = path + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(fs, this, CometTrackJsonContext.Default.CometTrack, cancellationToken);
        }

        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// The track in <paramref name="path"/>; false with the reason in words when the file is missing, is no track, is of
    /// another shape, or holds fewer than two samples at two instants (a rate needs two).
    /// </summary>
    public static bool TryRead(string path, [NotNullWhen(true)] out CometTrack? track, [NotNullWhen(false)] out string? refusal)
    {
        track = null;
        if (!File.Exists(path))
        {
            refusal = $"there is no comet track at {path}";
            return false;
        }

        CometTrack? read;
        try
        {
            read = JsonSerializer.Deserialize(File.ReadAllBytes(path), CometTrackJsonContext.Default.CometTrack);
        }
        catch (JsonException ex)
        {
            // A file a person may have edited by hand: what is wrong with it is said, not thrown.
            refusal = $"{path} is no comet track: {ex.Message}";
            return false;
        }

        if (read is null)
        {
            refusal = $"{path} is no comet track";
            return false;
        }

        if (read.SchemaVersion != CurrentSchemaVersion)
        {
            refusal = $"{path} is a comet track of shape {read.SchemaVersion}, and this build reads shape {CurrentSchemaVersion}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(read.Designation))
        {
            refusal = $"{path} names no comet";
            return false;
        }

        if (!(double.IsFinite(read.SiteLatDeg) && double.IsFinite(read.SiteLonDeg) && double.IsFinite(read.SiteElevMetres)))
        {
            refusal = $"{path} states no site the track of {read.Designation} was asked from";
            return false;
        }

        var samples = read.Samples.IsDefault ? [] : read.Samples;
        foreach (var sample in samples)
        {
            if (!(double.IsFinite(sample.RaDeg) && double.IsFinite(sample.DecDeg)))
            {
                refusal = $"{path} holds a position of {read.Designation} that is not a number, at {sample.TimeUtc:u}";
                return false;
            }
        }

        // Kept in time order whatever the file's order, so the first and last samples are the window's ends.
        samples = samples.Sort(static (a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
        if (samples.Length < 2 || samples[0].TimeUtc == samples[^1].TimeUtc)
        {
            refusal = $"{path} holds {samples.Length} position(s) of {read.Designation} at one instant, and a rate needs two instants";
            return false;
        }

        track = read with { Samples = samples };
        refusal = null;
        return true;
    }
}

/// <summary>The track file's JSON: indented, since a person reads it, and enums as words.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CometTrack))]
public sealed partial class CometTrackJsonContext : JsonSerializerContext
{
}
