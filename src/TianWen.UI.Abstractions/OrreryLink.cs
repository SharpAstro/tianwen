using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;

namespace TianWen.UI.Abstractions;

/// <summary>
/// What an open orrery is in a link (#2072): the query vocabulary the web atlas writes into its address bar while the orrery is open
/// and reads back from a shared link, a refresh or back/forward, so a link reopens the same scene. Defined once here, beside
/// <see cref="SkyAtlasLink"/>, so the writer and the reader cannot drift; the host only moves the string in and out of its address bar.
/// </summary>
/// <remarks>
/// <para>A parameter at its default is left out, so a link says only what differs from a freshly opened orrery. An unknown or
/// unparseable value reads as absent, never as an error: a hand-edited link opens the orrery, not a broken page.</para>
/// <para><b>The instant is <c>t</c></b>, the pointing link's own key (an ISO-8601 UTC time to the second), and like it, it is read
/// into the planning date, so the whole atlas, not only the orrery, is at that instant. It is written only while the atlas is off
/// live time and not playing: live time would rewrite the link every second, and a played clock every frame.</para>
/// </remarks>
public static class OrreryLink
{
    /// <summary>The key that says the orrery is open: <c>orrery=1</c>.</summary>
    public const string OpenKey = "orrery";

    /// <summary>The instant, shared with <see cref="SkyAtlasLink"/>'s pointing link.</summary>
    public const string TimeKey = "t";

    /// <summary>Playing: <c>play=1</c>.</summary>
    public const string PlayKey = "play";

    /// <summary>The play speed, by its <see cref="OrreryPlaySpeed.ShortName"/> (<c>1d</c>, <c>1mo</c>, <c>10y</c>).</summary>
    public const string SpeedKey = "speed";

    /// <summary>The focused body, by <see cref="BodyToken"/>.</summary>
    public const string FocusKey = "focus";

    /// <summary>The camera centred on the focus: <c>centre=1</c>.</summary>
    public const string CentreKey = "centre";

    /// <summary>The body whose panel is open, when it is not the focus; <c>none</c> for no panel.</summary>
    public const string PanelKey = "panel";

    /// <summary>The camera's azimuth, elevation and distance (<see cref="OrreryState"/>'s own units).</summary>
    public const string AzimuthKey = "az", ElevationKey = "el", DistanceKey = "dist";

    /// <summary>The distance scale: <c>true</c> (the default is auto).</summary>
    public const string ScaleKey = "scale";

    /// <summary>The comet filter, by its magnitude limit (<c>6</c>, <c>9</c>, <c>12</c>) or <c>all</c>.</summary>
    public const string CometsKey = "comets";

    /// <summary>The star backdrop off: <c>backdrop=0</c>.</summary>
    public const string BackdropKey = "backdrop";

    /// <summary>
    /// The comet the view was opened on (<see cref="OrreryState.Comet"/>), when the focus has moved off it: the comet keeps its
    /// accented orbit and its place in the scene whatever the brightest set holds, so a link without it would not reopen the same
    /// scene (the review of #2073).
    /// </summary>
    public const string CometKey = "comet";

    /// <summary>The value <see cref="PanelKey"/> takes for a closed panel.</summary>
    public const string NoPanel = "none";

    /// <summary>Every key the orrery's part of a link uses, the shared <see cref="TimeKey"/> included.</summary>
    public static readonly ImmutableArray<string> Keys =
        [OpenKey, TimeKey, PlayKey, SpeedKey, FocusKey, CentreKey, PanelKey, CometKey, AzimuthKey, ElevationKey, DistanceKey, ScaleKey, CometsKey, BackdropKey];

    /// <summary>The bodies the orrery names, the Sun first; a comet is named by its designation instead.</summary>
    private static readonly ImmutableArray<CatalogIndex> NamedBodies =
    [
        CatalogIndex.Sol, CatalogIndex.Mercury, CatalogIndex.Venus, CatalogIndex.Earth, CatalogIndex.Moon, CatalogIndex.Mars,
        CatalogIndex.Jupiter, CatalogIndex.Saturn, CatalogIndex.Uranus, CatalogIndex.Neptune,
    ];

    /// <summary>
    /// What one body is called in a link: a planet, the Moon or the Sun by the orrery's own label (<c>Mars</c>), a comet by its
    /// canonical designation (<c>10P</c>), which <see cref="CometSearchKeys.TryResolve"/> reads back.
    /// </summary>
    public static string BodyToken(CatalogIndex body)
        => body.ToCatalog() == Catalog.Comet ? body.ToCanonical() : OrreryScene.NameOf(body);

    /// <summary>The body a <see cref="BodyToken"/> names; a comet only once <paramref name="comets"/> holds it.</summary>
    public static bool TryResolveBody(string? token, ICometRepository? comets, out CatalogIndex body)
    {
        body = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }
        var name = token.Trim();
        foreach (var named in NamedBodies)
        {
            if (string.Equals(OrreryScene.NameOf(named), name, StringComparison.OrdinalIgnoreCase))
            {
                body = named;
                return true;
            }
        }
        return CometSearchKeys.TryResolve(comets, name, out body, out _);
    }

    /// <summary>
    /// The orrery's part of a link's query, without a leading separator: <c>orrery=1</c> and every setting that differs from a new
    /// view's. <paramref name="instantUtc"/> is written as <see cref="TimeKey"/> when given; the host passes it only off live time and
    /// not playing (see the remarks on the class).
    /// </summary>
    public static string QueryFor(OrreryState orrery, DateTimeOffset? instantUtc)
    {
        var query = new StringBuilder(OpenKey).Append("=1");
        if (instantUtc is { } instant)
        {
            Append(query, TimeKey, instant.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        }
        if (orrery.Playing)
        {
            Append(query, PlayKey, "1");
        }
        if (orrery.DaysPerSecond != OrreryState.DefaultPlaySpeed.DaysPerSecond)
        {
            Append(query, SpeedKey, SpeedOf(orrery.DaysPerSecond).ShortName);
        }
        if (orrery.Focus is { } focus)
        {
            Append(query, FocusKey, BodyToken(focus));
            if (orrery.CentredOnFocus)
            {
                Append(query, CentreKey, "1");
            }
        }
        if (orrery.Selected != orrery.Focus)
        {
            Append(query, PanelKey, orrery.Selected is { } selected ? BodyToken(selected) : NoPanel);
        }
        if (orrery.Comet is { } comet && comet != orrery.Focus)
        {
            Append(query, CometKey, BodyToken(comet));
        }
        var fresh = new OrreryState();
        // The camera: written whole once any part of it moved, so the reader never mixes a link's angle with a framing of its own.
        if (orrery.AzimuthDeg != fresh.AzimuthDeg || orrery.ElevationDeg != fresh.ElevationDeg || orrery.Distance != fresh.Distance)
        {
            Append(query, AzimuthKey, orrery.AzimuthDeg.ToString("0.#", CultureInfo.InvariantCulture));
            Append(query, ElevationKey, orrery.ElevationDeg.ToString("0.#", CultureInfo.InvariantCulture));
            Append(query, DistanceKey, orrery.Distance.ToString("0.###", CultureInfo.InvariantCulture));
        }
        if (orrery.Scale == OrreryScale.True)
        {
            Append(query, ScaleKey, "true");
        }
        if (orrery.CometFilter != OrreryCometFilter.Default)
        {
            Append(query, CometsKey, orrery.CometFilter.MaxMagnitude is { } limit ? limit.ToString(CultureInfo.InvariantCulture) : "all");
        }
        if (!orrery.ShowBackdrop)
        {
            Append(query, BackdropKey, "0");
        }
        return query.ToString();

        static void Append(StringBuilder query, string key, string value) => query.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
    }

    /// <summary>The speed of <see cref="OrreryState.PlaySpeeds"/> nearest <paramref name="daysPerSecond"/> (they are all the strip offers).</summary>
    private static OrreryPlaySpeed SpeedOf(double daysPerSecond)
    {
        var best = OrreryState.DefaultPlaySpeed;
        foreach (var speed in OrreryState.PlaySpeeds)
        {
            if (Math.Abs(Math.Log(speed.DaysPerSecond / daysPerSecond)) < Math.Abs(Math.Log(best.DaysPerSecond / daysPerSecond)))
            {
                best = speed;
            }
        }
        return best;
    }

    /// <summary>
    /// The orrery a query asks for, or null when it does not open one (no <c>orrery=1</c>). Every other field is null where the link
    /// does not say it, or says it in a form that does not parse.
    /// </summary>
    public static OrreryLinkData? Parse(string query)
    {
        string? open = null, time = null, play = null, speed = null, focus = null, centre = null, panel = null, opened = null;
        string? az = null, el = null, dist = null, scale = null, comets = null, backdrop = null;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var value = Uri.UnescapeDataString(part[(eq + 1)..]);
            switch (part[..eq].ToLowerInvariant())
            {
                case OpenKey: open = value; break;
                case TimeKey: time = value; break;
                case PlayKey: play = value; break;
                case SpeedKey: speed = value; break;
                case FocusKey: focus = value; break;
                case CentreKey: centre = value; break;
                case PanelKey: panel = value; break;
                case CometKey: opened = value; break;
                case AzimuthKey: az = value; break;
                case ElevationKey: el = value; break;
                case DistanceKey: dist = value; break;
                case ScaleKey: scale = value; break;
                case CometsKey: comets = value; break;
                case BackdropKey: backdrop = value; break;
            }
        }
        if (!IsOn(open))
        {
            return null;
        }

        DateTimeOffset? instant = DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
        double? daysPerSecond = null;
        foreach (var step in OrreryState.PlaySpeeds)
        {
            if (string.Equals(step.ShortName, speed, StringComparison.OrdinalIgnoreCase))
            {
                daysPerSecond = step.DaysPerSecond;
            }
        }
        OrreryCometFilter? filter = null;
        foreach (var step in OrreryCometFilter.Steps)
        {
            var token = step.MaxMagnitude is { } limit ? limit.ToString(CultureInfo.InvariantCulture) : "all";
            if (string.Equals(token, comets, StringComparison.OrdinalIgnoreCase))
            {
                filter = step;
            }
        }
        var camera = Number(az) is { } azimuth && Number(el) is { } elevation && Number(dist) is { } distance && distance > 0
            ? (azimuth, Math.Clamp(elevation, -OrreryCamera.MaxElevationDeg, OrreryCamera.MaxElevationDeg), distance)
            : ((double, double, double)?)null;
        return new OrreryLinkData(
            instant,
            IsOn(play),
            daysPerSecond,
            string.IsNullOrWhiteSpace(focus) ? null : focus.Trim(),
            IsOn(centre),
            string.IsNullOrWhiteSpace(panel) ? null : panel.Trim(),
            camera,
            string.Equals(scale, "true", StringComparison.OrdinalIgnoreCase) ? OrreryScale.True : null,
            filter,
            backdrop is "0" or "false" ? false : null)
        {
            Comet = string.IsNullOrWhiteSpace(opened) ? null : opened.Trim(),
        };

        static bool IsOn(string? value) => value is "1" or "true";

        static double? Number(string? value)
            => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    }
}

/// <summary>
/// The orrery a link asks for (<see cref="OrreryLink.Parse"/>): each field null, or false, where the link leaves it at a new view's.
/// </summary>
/// <param name="Instant">The atlas's instant, UTC.</param>
/// <param name="Playing">Whether time runs.</param>
/// <param name="DaysPerSecond">The play speed, one of <see cref="OrreryState.PlaySpeeds"/>.</param>
/// <param name="Focus">The focused body's <see cref="OrreryLink.BodyToken"/>.</param>
/// <param name="CentredOnFocus">Whether the camera is centred on the focus.</param>
/// <param name="Panel">The open panel's body, <see cref="OrreryLink.NoPanel"/> for none, or null for the focus's.</param>
/// <param name="Camera">Azimuth and elevation (degrees) and distance, all three or none.</param>
/// <param name="Scale">The distance scale.</param>
/// <param name="CometFilter">The comet filter.</param>
/// <param name="ShowBackdrop">The star backdrop.</param>
public sealed record OrreryLinkData(
    DateTimeOffset? Instant,
    bool Playing,
    double? DaysPerSecond,
    string? Focus,
    bool CentredOnFocus,
    string? Panel,
    (double AzimuthDeg, double ElevationDeg, double Distance)? Camera,
    OrreryScale? Scale,
    OrreryCometFilter? CometFilter,
    bool? ShowBackdrop)
{
    /// <summary>The comet the view was opened on, by its <see cref="OrreryLink.BodyToken"/>, when it is not the focus.</summary>
    public string? Comet { get; init; }
}
