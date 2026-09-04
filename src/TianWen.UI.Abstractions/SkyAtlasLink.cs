using System;
using System.Globalization;
using System.Text;
using TianWen.Lib.Astrometry;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Builds a deep link into the web Sky Atlas (<c>TianWen.UI.Web</c>), and defines the query
    /// vocabulary both ends speak. The viewer's right-click menu produces one; <c>Planner.razor</c>
    /// consumes it.
    /// </summary>
    /// <remarks>
    /// <para><b>RA travels in DEGREES, not hours.</b> Every RA inside this codebase is in hours, and a
    /// URL is the one place that stops being obvious -- SIMBAD, Aladin and WorldWide Telescope all take
    /// degrees, so an hours value would be read as a 15x-wrong position by a human pasting it anywhere
    /// else, and silently: 5.5 is a legal RA in both units. The conversion therefore lives here, once,
    /// with a test, rather than at each call site.</para>
    /// <para>A link deliberately carries NO SITE. The equatorial view at a given instant is the same
    /// sky wherever it is opened; only the horizon and altitude overlays are site-dependent, and those
    /// should be the recipient's own. Time is what pins the sky, and time is in the link.</para>
    /// </remarks>
    public static class SkyAtlasLink
    {
        /// <summary>
        /// Where the web build is published. A project site, so the <c>/tianwen/</c> segment is part of
        /// it -- github.io serves an org root only from the org's own <c>&lt;org&gt;.github.io</c> repo.
        /// </summary>
        public const string BaseUrl = "https://sharpastro.github.io/tianwen/";

        /// <summary>
        /// A capture time at or before the Unix epoch is a MISSING one, not a real observation. Both
        /// import paths use a sentinel rather than a null: a FITS with no <c>DATE-OBS</c> parses to
        /// <see cref="DateTime.MinValue"/> and a CR2 with no EXIF capture time to the epoch itself.
        /// Nothing in this domain was photographed in 1970, so one comparison covers both.
        /// </summary>
        public static bool IsKnownCaptureTime(DateTimeOffset time) => time > DateTimeOffset.UnixEpoch;

        /// <summary>
        /// How much sky a frame covers, in degrees across its long axis -- the <c>fov</c> a link
        /// carries, so the atlas opens showing roughly what the image showed.
        /// </summary>
        /// <remarks>
        /// Null when the frame has no usable plate scale. An APPROXIMATE WCS still answers: a
        /// FOCALLEN-derived scale with no CD matrix is a poor astrometric solution but a perfectly good
        /// statement about the geometry, which is all a "show me about this much sky" number is. That
        /// is the opposite of the grid labels' rule, which need the astrometry and so exclude it.
        /// </remarks>
        public static double? FieldOfViewDeg(WCS? wcs, int widthPx, int heightPx)
        {
            if (wcs is not { } w || !double.IsFinite(w.PixelScaleArcsec) || w.PixelScaleArcsec <= 0)
            {
                return null;
            }

            // The LONG axis, so the atlas frames the whole image rather than cropping its longer side
            // -- SkyMapState.FieldOfViewDeg is a single number and the sky map is rarely the same
            // aspect as the sensor.
            var longAxis = Math.Max(widthPx, heightPx);
            return longAxis <= 0 ? null : w.PixelScaleArcsec * longAxis / 3600.0;
        }

        /// <summary>
        /// A link to the atlas centred on <paramref name="raHours"/> / <paramref name="decDeg"/>.
        /// </summary>
        /// <param name="raHours">Right ascension in HOURS, as carried everywhere else; emitted as degrees.</param>
        /// <param name="decDeg">Declination in degrees.</param>
        /// <param name="fovDeg">
        /// Field width in degrees -- the frame's own, so the atlas opens showing roughly what the image
        /// covers rather than a whole hemisphere around it. Omitted when unknown, or when it is not a
        /// finite positive number.
        /// </param>
        /// <param name="capturedUtc">
        /// When the frame was taken. The atlas draws the sky at that instant, which is the only way a
        /// shared link of a planet, the Moon or a comet points at anything at all. Omitted unless
        /// <see cref="IsKnownCaptureTime"/>.
        /// </param>
        public static string For(double raHours, double decDeg, double? fovDeg = null, DateTimeOffset? capturedUtc = null)
        {
            // Wrapped and clamped here rather than trusted: a solve near the RA seam legitimately
            // answers just outside [0, 24), and the atlas would take 24.03h as "past the end" instead
            // of as 0.03h.
            var raDeg = (((raHours % 24.0) + 24.0) % 24.0) * 15.0;
            var dec = Math.Clamp(decDeg, -90.0, 90.0);

            var url = new StringBuilder(BaseUrl)
                .Append("?view=sky&ra=")
                .Append(raDeg.ToString("F6", CultureInfo.InvariantCulture))
                .Append("&dec=")
                .Append(dec.ToString("F6", CultureInfo.InvariantCulture));

            if (fovDeg is { } fov && double.IsFinite(fov) && fov > 0)
            {
                url.Append("&fov=").Append(fov.ToString("F4", CultureInfo.InvariantCulture));
            }

            if (capturedUtc is { } captured && IsKnownCaptureTime(captured))
            {
                // Round-trip "o" would carry sub-second precision and an offset the atlas does not use;
                // seconds in UTC is what the sky is drawn to, and it stays readable in an address bar.
                url.Append("&t=").Append(captured.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            }

            return url.ToString();
        }
    }
}
