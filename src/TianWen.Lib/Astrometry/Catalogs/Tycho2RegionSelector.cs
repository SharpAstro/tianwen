using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// Answers "which bytes of <c>tyc2.bin</c> does this view need" from the GSC-region bounding boxes
/// alone (<c>tyc2_gsc_bounds.bin.lz</c>, 92 KB compressed) plus the offset table that already sits in
/// the catalog's own 37 KB header.
///
/// <para><b>Standalone from the star records on purpose.</b> <see cref="Tycho2RaDecIndex"/> answers a
/// similar-sounding question but takes <c>_tycho2Data</c> in its constructor, because it reads the
/// records to resolve individual stars. A client deciding what to <i>download</i> has, by definition,
/// not downloaded them yet -- so the selection geometry cannot live there. It needs the bounds and
/// nothing else.</para>
///
/// <para><b>No index, and none needed.</b> The query is a linear scan of 9537 bounding boxes. That is
/// not a compromise: a spatial index over ~9.5k boxes would cost more to build than the scan costs to
/// run, and the scan happens on a view change, not per frame.</para>
///
/// <para><b>The convention for <c>viewRadiusDeg</c> is the FULL field of view, matching
/// <see cref="TianWen.UI.Abstractions.StarChunkIndex.IsVisible"/>.</b> <c>FieldOfViewDeg</c> is the
/// viewport's <i>vertical</i> extent, so the half-diagonal of a 16:9 viewport is ~0.92x that number --
/// which is what makes the full FOV the right generous-but-not-absurd radius, rather than an arbitrary
/// doubling. Both pipelines already cull with it; using the same number here means the client fetches
/// the sky it is about to try to draw.</para>
/// </summary>
public sealed class Tycho2RegionSelector
{
    /// <summary>minRA, maxRA (hours) then minDec, maxDec (degrees), four little-endian floats.</summary>
    public const int BytesPerRegion = 16;

    /// <summary>Packed record stride in <c>tyc2.bin</c>: tyc2 u16 | tyc3 u8 | RA f32 | Dec f32 | VT u8
    /// | BT u8 | pmRA i16 | pmDec i16.</summary>
    public const int BytesPerStar = 17;

    private readonly float[] _minRaDeg;
    private readonly float[] _maxRaDeg;
    private readonly float[] _minDecDeg;
    private readonly float[] _maxDecDeg;
    private readonly bool[] _empty;

    public int RegionCount => _minRaDeg.Length;

    /// <summary>
    /// Parses the decompressed bounds table. A region whose RA and Dec bounds are BOTH inverted is the
    /// baker's empty-region sentinel and is skipped; an inverted RA span alone means the region wraps
    /// through RA 0h, which is a real region and must be handled, not skipped. (Same reading as
    /// <see cref="Tycho2RaDecIndex"/>'s constructor -- the two must not disagree about what a region is.)
    /// </summary>
    public Tycho2RegionSelector(ReadOnlySpan<byte> boundsData)
    {
        var count = boundsData.Length / BytesPerRegion;
        _minRaDeg = new float[count];
        _maxRaDeg = new float[count];
        _minDecDeg = new float[count];
        _maxDecDeg = new float[count];
        _empty = new bool[count];

        for (var i = 0; i < count; i++)
        {
            var at = i * BytesPerRegion;
            var minRa = BinaryPrimitives.ReadSingleLittleEndian(boundsData[at..]);
            var maxRa = BinaryPrimitives.ReadSingleLittleEndian(boundsData[(at + 4)..]);
            var minDec = BinaryPrimitives.ReadSingleLittleEndian(boundsData[(at + 8)..]);
            var maxDec = BinaryPrimitives.ReadSingleLittleEndian(boundsData[(at + 12)..]);

            _empty[i] = minRa > maxRa && minDec > maxDec;
            _minRaDeg[i] = minRa * 15f;
            _maxRaDeg[i] = maxRa * 15f;
            _minDecDeg[i] = minDec;
            _maxDecDeg[i] = maxDec;
        }
    }

    /// <summary>
    /// Appends the zero-based GSC region indices (<c>tyc1 - 1</c>) whose bounding box comes within
    /// <paramref name="viewRadiusDeg"/> of the view direction, in ascending order.
    /// </summary>
    /// <param name="raHours">View centre RA in hours, matching <c>SkyMapState.CenterRA</c>.</param>
    /// <param name="decDeg">View centre Dec in degrees.</param>
    /// <param name="viewRadiusDeg">The full field of view; see the class remarks.</param>
    /// <param name="into">Receives the region indices; not cleared, so a caller can accumulate.</param>
    public void SelectVisible(double raHours, double decDeg, double viewRadiusDeg, List<int> into)
    {
        var raDeg = NormalizeDeg(raHours * 15.0);
        for (var i = 0; i < _minRaDeg.Length; i++)
        {
            if (!_empty[i] && MinSeparationDeg(raDeg, decDeg, i) <= viewRadiusDeg)
            {
                into.Add(i);
            }
        }
    }

    /// <summary>
    /// Exact smallest angular separation, in degrees, between a direction and a region's bounding box
    /// (a spherical rectangle). Exact rather than a bounding-circle approximation because the
    /// approximation's slop is a ring of regions wide, and this measurement exists to count regions.
    /// </summary>
    private double MinSeparationDeg(double raDeg, double decDeg, int i)
    {
        double minRa = _minRaDeg[i], maxRa = _maxRaDeg[i];
        double minDec = _minDecDeg[i], maxDec = _maxDecDeg[i];

        // An inverted RA span means the region straddles 0h, so "inside" is the union of two arcs.
        var wrapsRa = minRa > maxRa;
        var insideRa = wrapsRa ? raDeg >= minRa || raDeg <= maxRa : raDeg >= minRa && raDeg <= maxRa;

        if (insideRa)
        {
            // Same meridian: the separation collapses to the Dec gap.
            if (decDeg >= minDec && decDeg <= maxDec) return 0.0;
            return decDeg < minDec ? minDec - decDeg : decDeg - maxDec;
        }

        // Outside the RA span: the closest point lies on whichever bounding meridian is nearer.
        var dRa = Math.Min(AbsDeltaDeg(raDeg, minRa), AbsDeltaDeg(raDeg, maxRa));

        // On that meridian, maximise cos(separation) = A*sin(dec) + B*cos(dec) over dec in
        // [minDec, maxDec]. That is R*cos(dec - atan2(A, B)), unimodal across any 180-degree window,
        // so the interior optimum clamped into range plus the two endpoints covers every case.
        var dec0 = double.DegreesToRadians(decDeg);
        var a = Math.Sin(dec0);
        var b = Math.Cos(dec0) * Math.Cos(double.DegreesToRadians(dRa));

        var peakDeg = double.RadiansToDegrees(Math.Atan2(a, b));
        var best = Math.Max(
            CosSeparation(a, b, Math.Clamp(peakDeg, minDec, maxDec)),
            Math.Max(CosSeparation(a, b, minDec), CosSeparation(a, b, maxDec)));

        return double.RadiansToDegrees(Math.Acos(Math.Clamp(best, -1.0, 1.0)));
    }

    private static double CosSeparation(double a, double b, double decDeg)
    {
        var dec = double.DegreesToRadians(decDeg);
        return a * Math.Sin(dec) + b * Math.Cos(dec);
    }

    private static double AbsDeltaDeg(double from, double to)
    {
        var d = Math.Abs(NormalizeDeg(from - to));
        return d > 180.0 ? 360.0 - d : d;
    }

    private static double NormalizeDeg(double deg)
    {
        var d = deg % 360.0;
        return d < 0.0 ? d + 360.0 : d;
    }

    /// <summary>A half-open byte range of <c>tyc2.bin</c>, ready to become one HTTP Range header.</summary>
    public readonly record struct ByteRange(int Start, int End)
    {
        public int Length => End - Start;
    }

    /// <summary>
    /// Collapses selected regions into byte ranges over <c>tyc2.bin</c>, merging across gaps no larger
    /// than <paramref name="maxGapBytes"/>.
    ///
    /// <para>The gap allowance is the whole trade: regions are ordered by <c>tyc1</c>, which runs in
    /// declination bands, so a view's regions arrive as runs separated by the bands it does not touch.
    /// Merging across a small gap pays a few unwanted kilobytes to save a whole round trip, and on a
    /// link where latency dominates that is nearly always the better side of the trade.</para>
    /// </summary>
    /// <param name="regions">Ascending region indices, as produced by <see cref="SelectVisible"/>.</param>
    /// <param name="header">The catalog's leading bytes: <c>streamCount</c> then one int32 start offset
    /// per region. Only the header is needed, so a client can resolve ranges from the 37 KB it already
    /// fetched.</param>
    /// <param name="fileLength">Total length of <c>tyc2.bin</c>; the last region ends there.</param>
    public static List<ByteRange> ToByteRanges(
        IReadOnlyList<int> regions, ReadOnlySpan<byte> header, int fileLength, int maxGapBytes)
    {
        var ranges = new List<ByteRange>();
        if (regions.Count == 0)
        {
            return ranges;
        }

        var streamCount = BinaryPrimitives.ReadInt32LittleEndian(header);
        var start = RegionStart(header, regions[0]);
        var end = RegionEnd(header, streamCount, fileLength, regions[0]);

        for (var i = 1; i < regions.Count; i++)
        {
            var nextStart = RegionStart(header, regions[i]);
            var nextEnd = RegionEnd(header, streamCount, fileLength, regions[i]);

            if (nextStart - end <= maxGapBytes)
            {
                end = nextEnd;
            }
            else
            {
                ranges.Add(new ByteRange(start, end));
                start = nextStart;
                end = nextEnd;
            }
        }

        ranges.Add(new ByteRange(start, end));
        return ranges;
    }

    /// <summary>The offset table is 1-based in the header: entry 0 is <c>streamCount</c> itself.</summary>
    private static int RegionStart(ReadOnlySpan<byte> header, int gscIdx)
        => BinaryPrimitives.ReadInt32LittleEndian(header[((gscIdx + 1) * 4)..]);

    private static int RegionEnd(ReadOnlySpan<byte> header, int streamCount, int fileLength, int gscIdx)
        => gscIdx + 1 < streamCount
            ? BinaryPrimitives.ReadInt32LittleEndian(header[((gscIdx + 2) * 4)..])
            : fileLength;
}
