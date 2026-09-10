using System;

namespace TianWen.Lib.Imaging
{
    /// <summary>
    /// Where a frame's site came from, weakest last. Travels WITH the answer because the answers are
    /// not equally good: a header is right to metres, and everything below it is an inference that a
    /// reader is entitled to be told about.
    /// </summary>
    public enum FrameSiteSource
    {
        /// <summary>No site could be established. Equatorial only: no horizon, no Alt/Az.</summary>
        None = 0,

        /// <summary>
        /// The frame's own <c>SITELAT</c> / <c>SITELONG</c>. Accurate to whatever the capture software
        /// was told, which in practice is metres, and present on every N.I.N.A. light measured
        /// (40 of 40 on the 10P/Tempel set) as well as on TianWen's own captures.
        /// </summary>
        Header = 1,

        /// <summary>
        /// Solved from the frame's geometry -- its centre's altitude and azimuth against its
        /// declination and instant. Right to about 0.15 degrees, which is a sixth of a degree of
        /// horizon tilt and about 16 km on the ground.
        /// </summary>
        /// <remarks>
        /// A DISPLAY site and never an astrometric one: it must not reach an ephemeris, a topocentric
        /// correction or a written header. Not implemented yet -- the value exists so the vocabulary
        /// does not change when it is.
        /// </remarks>
        Solved = 2,

        /// <summary>
        /// Carried over from a frame that did know. What keeps the horizon on while stepping through a
        /// folder where only some frames carry the cards, and the reason it is worth naming: the sky
        /// on screen then belongs to a DIFFERENT frame from the picture.
        /// </summary>
        Remembered = 3,
    }

    /// <summary>
    /// Where a photograph was taken, with the provenance of that answer. Degrees, east-positive
    /// longitude.
    /// </summary>
    public readonly record struct FrameSite(double LatitudeDeg, double LongitudeDeg, FrameSiteSource Source)
    {
        /// <summary>Nothing known: the equatorial sky with no horizon under it.</summary>
        public static readonly FrameSite Unknown = new FrameSite(double.NaN, double.NaN, FrameSiteSource.None);

        /// <summary>Whether there is a site at all.</summary>
        public bool IsKnown => Source is not FrameSiteSource.None;

        /// <summary>
        /// The same site, restated as having been carried over from an earlier frame. Anything already
        /// weaker than that keeps its own provenance, so a remembered guess does not get promoted by
        /// being remembered again.
        /// </summary>
        public FrameSite AsRemembered()
            => IsKnown && Source is not FrameSiteSource.Remembered
                ? this with { Source = FrameSiteSource.Remembered }
                : this;

        /// <summary>A short phrase for a status line: where this came from, in the reader's terms.</summary>
        public string Describe() => Source switch
        {
            FrameSiteSource.Header => "site from the frame",
            FrameSiteSource.Solved => "site solved from the frame (approximate)",
            FrameSiteSource.Remembered => "site from a previous frame",
            _ => "no site",
        };
    }

    /// <summary>
    /// Reads where a frame was taken out of what the frame itself says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure and header-only by design. It answers "where was this photograph taken", which is a
    /// different question from "where is this machine" -- a frame shot from a dark site and opened at
    /// home would otherwise draw the wrong horizon with complete confidence.
    /// </para>
    /// <para>
    /// The site is what turns the horizon and the Alt/Az grid on when the sky is drawn behind a frame.
    /// Everything else about that sky -- the star field, the constellations, the milky way, where the
    /// planets were -- is equatorial and needs no site at all, so an unknown site costs two layers
    /// rather than the feature.
    /// </para>
    /// </remarks>
    public static class FrameSiteResolver
    {
        /// <summary>
        /// The site a frame states in its own header, or <see cref="FrameSite.Unknown"/>.
        /// </summary>
        /// <remarks>
        /// <b>Exactly zero in both is read as UNSET, not as the Gulf of Guinea.</b> Capture software
        /// writes the profile's site whether or not anyone filled it in, so (0, 0) is overwhelmingly
        /// an empty profile rather than a boat 500 km off Ghana -- and a horizon drawn there is worse
        /// than no horizon, because nothing on screen says it was invented.
        /// </remarks>
        public static FrameSite FromHeader(in ImageMeta meta)
        {
            var lat = (double)meta.Latitude;
            var lon = (double)meta.Longitude;

            if (!double.IsFinite(lat) || !double.IsFinite(lon)
                || Math.Abs(lat) > 90.0 || Math.Abs(lon) > 360.0
                || (lat == 0.0 && lon == 0.0))
            {
                return FrameSite.Unknown;
            }

            // East-positive either way, but written 0..360 by some software and -180..180 by others.
            var normalisedLon = lon > 180.0 ? lon - 360.0 : lon;
            return new FrameSite(lat, normalisedLon, FrameSiteSource.Header);
        }

        /// <summary>
        /// The best site available for a frame: what it says itself, else what an earlier frame said.
        /// </summary>
        /// <param name="meta">The frame's metadata.</param>
        /// <param name="remembered">
        /// The last site resolved in this session, or <see cref="FrameSite.Unknown"/>. Stepping through
        /// a folder is the case this exists for: one frame in it carries the cards and the rest do not,
        /// and losing the horizon on every arrow key is worse than saying where it came from.
        /// </param>
        public static FrameSite Resolve(in ImageMeta meta, FrameSite remembered)
        {
            var own = FromHeader(in meta);
            return own.IsKnown ? own : remembered.AsRemembered();
        }

        /// <summary>
        /// The instant the shutter opened, or null when the frame does not say.
        /// </summary>
        /// <remarks>
        /// A missing <c>DATE-OBS</c> parses to year 1 rather than to a null, so the test is for a
        /// plausible instant rather than for a default. Nothing digital and astronomical predates
        /// 1990 by enough for the bound to matter, and a frame that claims to is not carrying a real
        /// capture time anyway.
        /// </remarks>
        public static DateTimeOffset? CapturedAt(in ImageMeta meta)
            => meta.ExposureStartTime.Year >= 1990 ? meta.ExposureStartTime : null;
    }
}
