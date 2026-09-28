using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// One WinJUPOS image measurement, as its <c>.ims.xml</c> writes it beside the binary <c>.ims</c> (WinJUPOS 12.1.2): the
/// image, its time and site, the disk outline set on it, and the central meridians WinJUPOS computed for that time
/// (docs/plans/planetary-restoration.md, R1). The outline's centre is in the XML's own convention, half a pixel below the
/// binary file's: a pixel's centre at integer coordinates, as TianWen's.
/// </summary>
/// <param name="ImageFileName">The measured image's path as WinJUPOS recorded it (on the machine that measured it).</param>
/// <param name="RotationAngleDeg">The outline's rotation, WinJUPOS's own convention.</param>
/// <param name="AxisRatio">WinJUPOS's <c>q</c>: the planet's polar over equatorial radius.</param>
/// <param name="EarthDeclinationDeg">The Earth's planetocentric declination on the planet (Meeus's DE).</param>
public sealed record WinJuposMeasurement(
    string Body,
    string ImageFileName,
    double JulianDateUt,
    double LongitudeDeg,
    double LatitudeDeg,
    double CenterX,
    double CenterY,
    double EquatorialRadius,
    double RotationAngleDeg,
    bool Mirrored,
    double CentralMeridianI,
    double CentralMeridianII,
    double CentralMeridianIII,
    double AxisRatio,
    double EarthDeclinationDeg)
{
    private const double UnixEpochJulianDate = 2440587.5;

    /// <summary>The measurement's instant (WinJUPOS's <c>JulianDateUT</c>).</summary>
    public DateTimeOffset Utc => DateTimeOffset.UnixEpoch.AddDays(JulianDateUt - UnixEpochJulianDate);

    /// <summary>
    /// The image beside <paramref name="xmlPath"/> with the file name WinJUPOS recorded: the measurement folder travels, the
    /// recorded path is the measuring machine's. Null when none is there.
    /// </summary>
    public string? LocalImage(string xmlPath)
    {
        var name = ImageFileName.Replace('\\', '/');
        var candidate = Path.Combine(Path.GetDirectoryName(xmlPath) ?? ".", name[(name.LastIndexOf('/') + 1)..]);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Reads an <c>.ims.xml</c>; null when it is not one WinJUPOS wrote.</summary>
    public static WinJuposMeasurement? TryRead(string xmlPath)
    {
        XElement? settings;
        try
        {
            settings = XDocument.Load(xmlPath).Root?.Element("ImageMeasurementSettings");
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
        if (settings is null)
        {
            return null;
        }
        string Text(string name) => settings.Element(name)?.Value ?? "";
        double Number(string name) => double.TryParse(Text(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
        return new WinJuposMeasurement(Text("Body"), Text("FileName"), Number("JulianDateUT"), Number("GeoLongDeg"), Number("GeoLatDeg"),
            Number("CenterXPixel"), Number("CenterYPixel"), Number("EquatorialRadiusPixel"), Number("RotationAngleDeg"),
            string.Equals(Text("MirroredInvertedImage"), "True", StringComparison.OrdinalIgnoreCase),
            Number("CM1Deg"), Number("CM2Deg"), Number("CM3Deg"), Number("q"), Number("DeclinationOfEarthOnPlanetDeg"));
    }
}
