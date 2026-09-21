using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace TianWen.Lib.Imaging
{
    /// <summary>
    /// How large one exposure is, in the pixels of the master in hand, for a master that cannot say.
    /// A single-pointing integration holds no fully-covered data wider than one frame, so whatever its
    /// canvas carries beyond that footprint is dither excursion and is partial coverage BY
    /// CONSTRUCTION. That is a geometric fact about a thing <see cref="CoverageEdgeWalk"/> otherwise
    /// has to infer from noise statistics alone.
    /// </summary>
    /// <remarks>
    /// <para><b>This is for FOREIGN masters, and only for them.</b> A master TianWen wrote carries a
    /// per-pixel coverage plane beside it and the exact crop tier uses that: it is better than
    /// anything here in every way, being measured rather than assumed, per-pixel rather than per-axis,
    /// and correct for a mosaic and across a meridian flip where this bound is simply wrong. Nothing
    /// in this file should ever be reached for one of ours. It exists because a master from another
    /// stacker has no such plane and never will, and the walk is the only tier it will ever get --
    /// which is the case backlog #71 is about.</para>
    ///
    /// <para><b>Advisory, and may only WIDEN what the walk looks at.</b> An unlisted camera, a header
    /// without the cards to scale it, or any non-finite arithmetic all answer null and leave the walk
    /// on its configured constants. An entry that is too LARGE is therefore inert, since the margin
    /// shrinks to nothing; one that is too SMALL makes the walk look further than it should, so an
    /// entry goes in only where the model is unambiguous.</para>
    ///
    /// <para>Each entry is corroborated against the 139-master store of 2026-09-19: the SMALLEST
    /// canvas a camera produced across its sessions is that camera's sensor plus its least dither, and
    /// so sits just above the datasheet figure. The dither excursion over that store ran from 0.6% of
    /// the canvas to 15.4% depending on camera and session, which is the measurement saying no single
    /// <see cref="CoverageEdgeWalkOptions.SettleSearchFraction"/> can serve them all.</para>
    /// </remarks>
    public static class SensorGeometry
    {
        /// <summary>The 206.265 that turns photosite pitch in microns over focal length in mm into
        /// arcsec per pixel.</summary>
        private const double ArcsecPerPixelConstant = 206.265;

        /// <summary>Unbinned photosite counts, keyed on <c>INSTRUME</c> as the camera writes it. The
        /// corpus minimum canvas is quoted per entry as the corroboration.</summary>
        private static readonly FrozenDictionary<string, (int Width, int Height)> Sensors =
            new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase)
            {
                // IMX533, square 1 inch. Corpus minimum canvas 3011x3027 over 87 masters.
                ["ZWO ASI533MC Pro"] = (3008, 3008),
                // IMX533 rebadged. Corpus minimum canvas 3024x3025 over 24 masters.
                ["SVBONY SV605CC"] = (3008, 3008),
                // IMX585. Corpus minimum canvas 3844x2174 over 14 masters.
                ["ZWO ASI585MC Pro"] = (3840, 2160),
                // MN34230. Corpus minimum canvas 4714x3558 over 4 masters.
                ["ZWO ASI1600MM Pro"] = (4656, 3520),
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether a camera is one this table knows, so a caller can report the difference
        /// between "no bound" and "a bound of zero".</summary>
        public static bool Knows(string? instrument)
            => !string.IsNullOrWhiteSpace(instrument) && Sensors.ContainsKey(instrument.Trim());

        /// <summary>
        /// One exposure's footprint on this master's canvas, in that canvas's own pixels, or null when
        /// it cannot be established without guessing.
        /// </summary>
        /// <remarks>
        /// A table lookup is in SENSOR pixels and has to cross two scale changes to mean anything on a
        /// canvas: binning, which makes one read pixel cover <c>BinX</c> photosites, and any
        /// resampling the integration did, which shows up as the declared pixel scale differing from
        /// the one the optics and the binned photosite imply. A 2x drizzle halves the declared scale
        /// and so doubles the footprint. Both come off the header rather than being assumed, and if
        /// either is missing the answer is null rather than a guess.
        /// </remarks>
        public static (int Width, int Height)? FrameFootprint(in ImageMeta meta)
        {
            if (!Knows(meta.Instrument) || !Sensors.TryGetValue(meta.Instrument.Trim(), out var sensor))
            {
                return null;
            }

            var binX = Math.Max(meta.BinX, 1);
            var binY = Math.Max(meta.BinY, 1);
            var scale = ResampleFactor(meta, binX);
            if (!double.IsFinite(scale) || scale <= 0)
            {
                return null;
            }

            var w = (int)Math.Round(sensor.Width / (double)binX * scale);
            var h = (int)Math.Round(sensor.Height / (double)binY * scale);
            return w > 0 && h > 0 ? (w, h) : null;
        }

        /// <summary>How many canvas pixels one binned photosite became: 1 for a plain integration, 2
        /// for a 2x drizzle. Derived, never assumed, and NaN where the header cannot say.</summary>
        private static double ResampleFactor(in ImageMeta meta, int binX)
        {
            var declared = meta.DeclaredPixelScale;
            if (!float.IsFinite(declared) || declared <= 0
                || !float.IsFinite(meta.PixelSizeX) || meta.PixelSizeX <= 0
                || meta.FocalLength <= 0)
            {
                return double.NaN;
            }

            var native = ArcsecPerPixelConstant * meta.PixelSizeX * binX / meta.FocalLength;
            return native / declared;
        }

        /// <summary>
        /// The deepest a coverage border can run into <paramref name="span"/> on one axis: whatever
        /// that axis carries beyond one frame's footprint. Zero when the footprint is unknown or the
        /// span already sits inside it, so a caller can always add it to a constant.
        /// </summary>
        /// <remarks>
        /// It is the excursion for the axis as a WHOLE, and therefore a bound for EITHER of its edges
        /// rather than for each: all of the dither may have gone one way. Clamped to
        /// <paramref name="maxFraction"/> of the span, because a wrong entry, a mosaic panel, or a
        /// master spanning both sides of a meridian flip would otherwise hand the walk an excursion
        /// the size of the frame.
        /// </remarks>
        public static int DitherMarginPx(in ImageMeta meta, int span, bool horizontal, double maxFraction = 0.25)
        {
            if (FrameFootprint(meta) is not { } footprint)
            {
                return 0;
            }

            var extent = horizontal ? footprint.Width : footprint.Height;
            var margin = span - extent;
            return margin <= 0 ? 0 : Math.Min(margin, (int)(span * maxFraction));
        }
    }
}
