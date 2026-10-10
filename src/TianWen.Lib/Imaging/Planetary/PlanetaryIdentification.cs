using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpAstro.Ser;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging.Optics;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// What a planetary capture is (A4 of AUTO, #1391): the planet it shows, the filter a mono capture was taken through, and the telescope
/// that took it, each with where it was read, in words. <see cref="PlanetaryIdentification"/> reads them; <see cref="PlanetaryAuto"/>
/// stacks with them at the measured defaults and writes them into its master (OBJECT, FILTER, TELESCOP, APTDIA).
/// </summary>
/// <param name="Planet">The planet, or null where nothing names one.</param>
/// <param name="PlanetFrom">Where <paramref name="Planet"/> was read, or why there is none.</param>
/// <param name="Filter">The filter's name as it was read (<c>Red</c>, <c>L</c>), or null where none was.</param>
/// <param name="FilterNm">The filter's effective wavelength, nm (<see cref="PlanetaryCaptureName.FilterNm"/>), or null for broadband.</param>
/// <param name="FilterFrom">Where the filter was read, or why there is none.</param>
/// <param name="ApertureMm">The telescope's aperture, mm, or null where nothing gives one.</param>
/// <param name="Design">The telescope's design, whose usual obstruction the pupil takes (<see cref="PlanetaryBestStack.PupilFor"/>).</param>
/// <param name="TelescopeFrom">Where the telescope was read, or why there is none.</param>
/// <param name="Camera">The camera's name as the capture gives it, by which its telescope is remembered (<see cref="PlanetaryTelescopeMemory"/>).</param>
/// <param name="Layout">The capture's frames: a mono capture takes its filter's wavelength, a colour one each channel's own.</param>
public sealed record PlanetaryIdentity(CatalogIndex? Planet, string PlanetFrom, string? Filter, double? FilterNm, string FilterFrom,
    int? ApertureMm, OpticalDesign Design, string TelescopeFrom, string? Camera, PlanetaryFrameLayout Layout)
{
    /// <summary>The telescope's pupil, or null without an aperture.</summary>
    public Pupil? Telescope => PlanetaryBestStack.PupilFor(ApertureMm, Design);

    /// <summary>The derived sharpening's wavelengths: a mono capture's filter, else the defaults (empty: 550 mono, 610, 530, 460 in colour).</summary>
    public ImmutableArray<double> WavelengthsNm => FilterNm is { } nm && Layout == PlanetaryFrameLayout.Mono ? [nm] : [];

    /// <summary>The telescope as a master's TELESCOP card names it (<c>254 mm Newtonian</c>), which <see cref="PlanetaryCaptureName.Telescope"/> reads back.</summary>
    public string? TelescopeName => ApertureMm is not { } mm
        ? null
        : Design is OpticalDesign.Unknown ? string.Create(CultureInfo.InvariantCulture, $"{mm} mm") : string.Create(CultureInfo.InvariantCulture, $"{mm} mm {Design}");

    /// <summary>What was identified and where it was read, in one line.</summary>
    public string Describe()
    {
        var inv = CultureInfo.InvariantCulture;
        var planet = Planet is { } body ? $"{body} ({PlanetFrom})" : $"no planet ({PlanetFrom})";
        var filter = Layout != PlanetaryFrameLayout.Mono
            ? "colour, each channel at its own wavelength"
            : FilterNm is { } nm ? string.Create(inv, $"{Filter ?? "filter"} at {nm:0} nm ({FilterFrom})") : $"broadband, 550 nm ({FilterFrom})";
        var telescope = TelescopeName is { } name ? $"a {name} ({TelescopeFrom})" : $"no telescope ({TelescopeFrom})";
        return $"{planet}; {filter}; {telescope}";
    }
}

/// <summary>What a person said of a capture, which AUTO takes before anything it reads: each null (or unknown) where nothing was said.</summary>
public sealed record PlanetaryIdentityGiven(CatalogIndex? Planet = null, double? FilterNm = null, int? ApertureMm = null,
    OpticalDesign Design = OpticalDesign.Unknown)
{
    /// <summary>Nothing said: AUTO reads everything from the capture.</summary>
    public static PlanetaryIdentityGiven Nothing { get; } = new PlanetaryIdentityGiven();
}

/// <summary>
/// Reads what a planetary capture is (A4 of AUTO, #1391; the order and the rules were set on #1391 before anything was measured). Each
/// fact is taken from the first source that gives it:
/// <list type="bullet">
/// <item>The planet: what was given, then the capture program's settings (FireCapture's <c>Profile</c>), then the mount's pointing those
/// settings hold against the ephemeris (SharpCap writes the mount's RA and Dec), then the file's name and its folders. The frames then
/// tell a Jupiter from a Saturn the path named (<see cref="RingedElongation"/>), and never name a planet themselves.</item>
/// <item>The filter, which only a mono capture takes: what was given, then the settings (FireCapture's <c>Filter</c>, SharpCap's filter
/// wheel), then the file's name, else broadband.</item>
/// <item>The telescope: what was given, then the SER header (a TianWen recording's), then the settings (FireCapture's <c>Scope</c>),
/// then the file's name and its folders, then the telescope last said for the same camera (<see cref="PlanetaryTelescopeMemory"/>), else
/// none, which sharpens by the preset.</item>
/// </list>
/// </summary>
public static class PlanetaryIdentification
{
    /// <summary>How many frames, spread through the capture, its planet's elongation is read over (<see cref="ElongationAsync"/>).</summary>
    public const int ElongationFrames = 32;

    /// <summary>
    /// The median elongation (<see cref="PlanetaryDisk.Elongation"/>) at or past which a capture's frames show Saturn's rings: the
    /// geometric mean of the largest Jupiter's and the smallest Saturn's on hand, by the rule set on #1391 before they were read. Over
    /// the 200 captures on D: and E: (2026-10-10) every Jupiter read 1.064 to 1.112 and every Saturn 2.401 to 3.139, so 1.63.
    /// </summary>
    public const double RingedElongation = 1.63;

    /// <summary>
    /// The ring opening, degrees (Saturn's sub-observer latitude), below which the frames say nothing of the rings: the smallest
    /// opening among the Saturn captures the threshold was read on (#1391), 7.339 for the owner's of 2026-10-07. Past it, a capture with no
    /// rings is not Saturn.
    /// </summary>
    public const double MinRingOpeningDeg = 7.3;

    /// <summary>
    /// What the SER capture at <paramref name="capturePath"/> is: <paramref name="given"/> first, then what the capture says of itself,
    /// then, for the telescope, what <paramref name="external"/>'s memory holds for its camera (none consulted when null).
    /// </summary>
    public static async Task<PlanetaryIdentity> IdentifyAsync(string capturePath, PlanetaryIdentityGiven? given = null, IExternal? external = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capturePath);
        given ??= PlanetaryIdentityGiven.Nothing;
        var path = Path.GetFullPath(capturePath);
        var settings = CaptureSettings.Of(path);
        string serTelescope;
        string serInstrument;
        DateTimeOffset? headerTime;
        using (var reader = SerReader.Open(path))
        {
            (serTelescope, serInstrument) = (reader.Header.Telescope, reader.Header.Instrument);
            headerTime = reader.Header.UtcDateTime is { } utc ? new DateTimeOffset(utc, TimeSpan.Zero) : null;
        }
        using var stream = SerFrameStream.Open(path);
        var middle = stream.CaptureSpan is { } span ? span.Earliest + ((span.Latest - span.Earliest) / 2) : settings.Middle ?? headerTime;
        var camera = settings.Camera ?? CameraName(serInstrument);

        var (planet, planetFrom, fromPath) = PlanetFromWords(path, given, settings, middle);
        if (fromPath)
        {
            (planet, planetFrom) = await CheckedByTheFramesAsync(stream, planet, planetFrom, middle, cancellationToken).ConfigureAwait(false);
        }

        var (filter, filterNm, filterFrom) = given.FilterNm is { } givenNm
            ? (NameOf(givenNm), givenNm, "given")
            : settings.Filter is { } named && PlanetaryCaptureName.FilterNm(named) is { } settingsNm
                ? (named, settingsNm, $"{settings.Program}'s filter{(settings.FilterWheel ? " wheel" : "")}")
                : PlanetaryCaptureName.WavelengthNm(path) is { } nameNm
                    ? (NameOf(nameNm), (double?)nameNm, "the file's name")
                    : (null, null, "none named");

        var (apertureMm, design, telescopeFrom) = await TelescopeAsync(given, path,
            [(serTelescope, "the capture's header"), (settings.Scope, $"{settings.Program}'s settings")], camera, external, cancellationToken).ConfigureAwait(false);
        return new PlanetaryIdentity(planet, planetFrom, filter, filterNm, filterFrom, apertureMm, design, telescopeFrom, camera, stream.Layout);
    }

    /// <summary>
    /// What a planetary master at <paramref name="path"/> is, read off its own header (OBJECT, FILTER, TELESCOP and APTDIA, which
    /// <see cref="PlanetaryAuto"/> writes) and its path by the same rules as <see cref="IdentifyAsync"/>, its one image checked for rings.
    /// </summary>
    public static async Task<PlanetaryIdentity> IdentifyMasterAsync(Image master, string path, PlanetaryIdentityGiven? given = null, IExternal? external = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(path);
        given ??= PlanetaryIdentityGiven.Nothing;
        var meta = master.ImageMeta;
        var full = Path.GetFullPath(path);
        var middle = PlanetaryBestStack.InstantOf(master, epoch: null);

        var (planet, planetFrom, fromPath) = given.Planet is { } givenPlanet
            ? (givenPlanet, "given", false)
            : PlanetaryCaptureName.Named(meta.ObjectName) is { } fromObject
                ? (fromObject, "the master's OBJECT", false)
                : PlanetOfPath(full);
        if (fromPath)
        {
            (planet, planetFrom) = Checked(PlanetaryDisk.Elongation(master), planet, planetFrom, middle);
        }

        var cardFilter = meta.Filter == Filter.None || meta.Filter == Filter.Unknown ? null : meta.Filter.FilterNameForFits;
        var (filter, filterNm, filterFrom) = given.FilterNm is { } givenNm
            ? (NameOf(givenNm), givenNm, "given")
            : PlanetaryCaptureName.FilterNm(cardFilter) is { } cardNm
                ? (cardFilter, (double?)cardNm, "the master's FILTER")
                : PlanetaryCaptureName.WavelengthNm(full) is { } nameNm
                    ? (NameOf(nameNm), (double?)nameNm, "the file's name")
                    : (null, null, "none named");

        // APTDIA is the aperture's own card; TELESCOP names it again and the design beside it.
        var telescopeCard = meta.Aperture > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{meta.Aperture} mm {meta.Telescope}")
            : meta.Telescope;
        var (apertureMm, design, telescopeFrom) = await TelescopeAsync(given, full, [(telescopeCard, "the master's header")],
            CameraName(meta.Instrument), external, cancellationToken).ConfigureAwait(false);
        var layout = master.ChannelCount == 3 ? PlanetaryFrameLayout.Rgb : PlanetaryFrameLayout.Mono;
        return new PlanetaryIdentity(planet, planetFrom, filter, filterNm, filterFrom, apertureMm, design, telescopeFrom, CameraName(meta.Instrument), layout);
    }

    /// <summary>
    /// The median elongation of a capture's planet (<see cref="PlanetaryDisk.Elongation"/>) over <see cref="ElongationFrames"/> frames
    /// spread through it: about 1.1 for Jupiter's disk and past 2 for Saturn's open rings (measured on five captures, #1300). A frame that
    /// finds no planet is left out; NaN when none does.
    /// </summary>
    public static async Task<double> ElongationAsync(IPlanetaryFrameStream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var count = Math.Min(ElongationFrames, stream.FrameCount);
        var values = new List<float>(count);
        for (var k = 0; k < count; k++)
        {
            var frame = await stream.LoadAsync((int)((k + 0.5) * stream.FrameCount / count), cancellationToken).ConfigureAwait(false);
            try
            {
                if (PlanetaryDisk.Elongation(frame) is var elongation && float.IsFinite(elongation) && elongation > 0)
                {
                    values.Add(elongation);
                }
            }
            finally
            {
                frame.Release();
            }
        }
        if (values.Count == 0)
        {
            return double.NaN;
        }
        values.Sort();
        return values.Count % 2 == 1 ? values[values.Count / 2] : (values[(values.Count / 2) - 1] + values[values.Count / 2]) / 2.0;
    }

    /// <summary>
    /// Saturn's ring opening at <paramref name="utc"/>, degrees: the observer's latitude above the ring plane, which is Saturn's equator.
    /// </summary>
    public static double RingOpeningDeg(DateTimeOffset utc)
        => Math.Abs(PhysicalEphemeris.Compute(CatalogIndex.Saturn, utc).SubObserverLatitudeCentric);

    // The planet the words give, and whether it came from the path, the one source the frames may overrule (#1391 rule 4).
    private static (CatalogIndex? Planet, string From, bool FromPath) PlanetFromWords(string path, PlanetaryIdentityGiven given, CaptureSettings settings,
        DateTimeOffset? middle)
    {
        if (given.Planet is { } givenPlanet)
        {
            return (givenPlanet, "given", false);
        }
        if (PlanetaryCaptureName.Named(settings.Profile) is { } profiled)
        {
            return (profiled, $"{settings.Program}'s profile", false);
        }
        // A planet's place differs from the Earth's centre by its parallax, seconds of arc, against the 1.5 degrees PointedAt allows, so no
        // site is needed (the Moon's degree of parallax is inside it too). A pointing near two bodies (a conjunction) says neither.
        var byPath = PlanetOfPath(path);
        if (settings.Pointing is { } pointing && middle is { } at
            && PlanetaryCaptureName.BodiesPointedAt(pointing.RaHours, pointing.DecDeg, at, latitude: 0, longitude: 0) is [var pointed])
        {
            var pointingFrom = $"the mount's pointing in {settings.Program}'s settings";
            // A mount reports where it believes it points: on 2024-09-17 one said Saturn, 0.7 degrees off it, while the camera showed the
            // full Moon 9 degrees away, and the folder said Moon (#1391). Where the two disagree, neither names the planet.
            return byPath.Planet is { } named && named != pointed
                ? (null, $"none: {pointingFrom} gives {pointed}, but {byPath.From} gives {named}", false)
                : (pointed, pointingFrom, false);
        }
        return byPath;
    }

    // The planet a path names, the file's name before its folders'.
    private static (CatalogIndex? Planet, string From, bool FromPath) PlanetOfPath(string path)
    {
        if (PlanetaryCaptureName.Named(Path.GetFileNameWithoutExtension(path)) is { } inName)
        {
            return (inName, "the file's name", true);
        }
        return PlanetaryCaptureName.Planet(path) is { } inFolder ? (inFolder, "the folder's name", true) : (null, "nothing names one", false);
    }

    // A planet from the path, or none, checked against what the frames show (#1391 rule 4).
    private static async Task<(CatalogIndex? Planet, string From)> CheckedByTheFramesAsync(IPlanetaryFrameStream stream, CatalogIndex? planet, string from,
        DateTimeOffset? middle, CancellationToken cancellationToken)
        => middle is null ? (planet, from) : Checked(await ElongationAsync(stream, cancellationToken).ConfigureAwait(false), planet, from, middle);

    // The frames decide between the two planets AUTO derives a sharpening for, and only between them: rings make Saturn of a capture the path
    // calls Jupiter, and no rings where Saturn's would show leave one the path calls Saturn unnamed. Never a planet named from nothing, nor
    // any other overruled: a crescent Venus reads 1.54 to 3.55 and a partial Moon 1.82 to 2.34, as elongated as the rings (#1391). Only where
    // the rings are open enough for the frames to say (MinRingOpeningDeg).
    private static (CatalogIndex? Planet, string From) Checked(double elongation, CatalogIndex? planet, string from, DateTimeOffset? middle)
    {
        if (planet is not (CatalogIndex.Jupiter or CatalogIndex.Saturn) || middle is not { } at || !double.IsFinite(elongation)
            || RingOpeningDeg(at) is var opening && opening < MinRingOpeningDeg)
        {
            return (planet, from);
        }
        var inv = CultureInfo.InvariantCulture;
        if (elongation >= RingedElongation && planet == CatalogIndex.Jupiter)
        {
            return (CatalogIndex.Saturn, string.Create(inv, $"Saturn's rings in the frames, elongation {elongation:0.00}, where {from} gives Jupiter"));
        }
        if (elongation < RingedElongation && planet == CatalogIndex.Saturn)
        {
            return (null, string.Create(inv,
                $"none: {from} gives Saturn, but the frames show no rings (elongation {elongation:0.00}) where they would be {opening:0} degrees open"));
        }
        return (planet, from);
    }

    // The telescope: given, then each text in turn, then the path, then the camera's remembered one.
    private static async Task<(int? ApertureMm, OpticalDesign Design, string From)> TelescopeAsync(PlanetaryIdentityGiven given, string path,
        (string? Text, string From)[] texts, string? camera, IExternal? external, CancellationToken cancellationToken)
    {
        if (given.ApertureMm is { } givenMm)
        {
            return (givenMm, given.Design, "given");
        }
        foreach (var (text, from) in texts)
        {
            if (PlanetaryCaptureName.Telescope(text) is { ApertureMm: { } mm } named)
            {
                return (mm, named.Design, from);
            }
        }
        if (PlanetaryCaptureName.Telescope(Path.GetFileNameWithoutExtension(path)) is { ApertureMm: { } inName } byName)
        {
            return (inName, byName.Design, "the file's name");
        }
        if (PlanetaryCaptureName.TelescopeOfPath(path) is { ApertureMm: { } inFolder } byFolder)
        {
            return (inFolder, byFolder.Design, "the folder's name");
        }
        if (external is not null
            && await PlanetaryTelescopeMemory.RecallAsync(external, camera, cancellationToken).ConfigureAwait(false) is { } remembered)
        {
            return (remembered.ApertureMm, remembered.Design, $"remembered for {camera}");
        }
        return (null, OpticalDesign.Unknown, camera is null ? "none named" : $"none named or remembered for {camera}");
    }

    // A filter's name for a wavelength the reader gave, as FILTER carries it.
    private static string NameOf(double nm) => nm switch
    {
        650 => "Red",
        530 => "Green",
        460 => "Blue",
        750 => "IR",
        550 => "L",
        _ => string.Create(CultureInfo.InvariantCulture, $"{nm:0} nm"),
    };

    /// <summary>
    /// The camera a SER capture names, as <see cref="IdentifyAsync"/> reads it: its capture program's settings' (SharpCap's section,
    /// FireCapture's <c>Camera</c>), else its header's Instrument (<see cref="CameraName"/>); null when neither names one. What a telescope
    /// said for the capture is remembered by (<see cref="PlanetaryTelescopeMemory"/>).
    /// </summary>
    public static string? CameraOf(string capturePath)
    {
        ArgumentNullException.ThrowIfNull(capturePath);
        var path = Path.GetFullPath(capturePath);
        if (CaptureSettings.Of(path).Camera is { } camera)
        {
            return camera;
        }
        using var reader = SerReader.Open(path);
        return CameraName(reader.Header.Instrument);
    }

    /// <summary>
    /// A camera's name from a SER header's Instrument field or a FITS INSTRUME card: as written, but FireCapture's packed field
    /// (<c>ASI=ZWO ASI224MCtemp=21.0</c>) to the camera alone. Null when empty.
    /// </summary>
    public static string? CameraName(string? instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument))
        {
            return null;
        }
        var name = instrument;
        if (name.IndexOf('=') is var equals and >= 0)
        {
            name = name[(equals + 1)..];
        }
        if (name.IndexOf("temp=", StringComparison.OrdinalIgnoreCase) is var temperature and >= 0)
        {
            name = name[..temperature];
        }
        name = name.Trim();
        return name.Length == 0 ? null : name;
    }

    // What a capture program's settings file beside the capture says (PlanetaryCorpus.FindSettings): its program, the planet's profile, the
    // filter, the telescope, the mount's pointing, the capture's middle and the camera.
    private sealed record CaptureSettings(string Program, string? Profile, string? Filter, bool FilterWheel, string? Scope,
        (double RaHours, double DecDeg)? Pointing, DateTimeOffset? Middle, string? Camera)
    {
        public static CaptureSettings Of(string capturePath)
        {
            if (PlanetaryCorpus.FindSettings(capturePath) is not { } file)
            {
                return new CaptureSettings("the capture program", null, null, false, null, null, null, null);
            }
            var text = File.ReadAllText(file);
            var settings = PlanetaryCorpus.ParseSettings(text);
            var program = text.StartsWith("FireCapture", StringComparison.OrdinalIgnoreCase) ? "FireCapture"
                : settings.ContainsKey("SharpCapVersion") ? "SharpCap"
                : "the capture program";
            string? filter = null;
            var wheel = false;
            (double, double)? pointing = null;
            string? scope = null;
            foreach (var (key, value) in settings)
            {
                if (key.Contains("FilterWheel", StringComparison.OrdinalIgnoreCase) && filter is null)
                {
                    (filter, wheel) = (value, true);
                }
                else if (key.Equals("Filter", StringComparison.OrdinalIgnoreCase))
                {
                    (filter, wheel) = (value, false);
                }
                if (PointingOf(value) is { } at)
                {
                    pointing ??= at;
                }
                else if (key.Equals("Scope", StringComparison.OrdinalIgnoreCase) || key.Equals("Telescope", StringComparison.OrdinalIgnoreCase))
                {
                    scope ??= value;
                }
            }
            DateTimeOffset? middle = settings.TryGetValue("MidCapture", out var mid)
                && DateTimeOffset.TryParse(mid, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;
            var profile = settings.TryGetValue("Profile", out var p) ? p : settings.TryGetValue("Object", out var o) ? o : settings.TryGetValue("Target", out var t) ? t : null;
            return new CaptureSettings(program, profile, filter, wheel, scope, pointing, middle, settings.TryGetValue("Camera", out var camera) ? camera : null);
        }

        // SharpCap's mount line, "RA=00:44:58.9,Dec=+01:52:51 (JNOW)": hours and degrees, or null.
        private static (double RaHours, double DecDeg)? PointingOf(string value)
        {
            var ra = value.IndexOf("RA=", StringComparison.OrdinalIgnoreCase);
            var dec = value.IndexOf("Dec=", StringComparison.OrdinalIgnoreCase);
            if (ra < 0 || dec < ra)
            {
                return null;
            }
            var raText = value[(ra + 3)..dec].TrimEnd(',', ' ');
            var decText = value[(dec + 4)..];
            if (decText.IndexOf(' ') is var space and > 0)
            {
                decText = decText[..space];
            }
            return Sexagesimal(raText) is { } hours && Sexagesimal(decText) is { } degrees ? (hours, degrees) : null;
        }

        // "hh:mm:ss.s" or "+dd:mm:ss" as one number.
        private static double? Sexagesimal(string text)
        {
            var parts = text.Trim().Split(':');
            if (parts.Length is < 1 or > 3)
            {
                return null;
            }
            var negative = parts[0].StartsWith('-');
            var value = 0.0;
            for (var i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i].TrimStart('+', '-'), NumberStyles.Float, CultureInfo.InvariantCulture, out var part))
                {
                    return null;
                }
                value += part / Math.Pow(60, i);
            }
            return negative ? -value : value;
        }
    }
}
