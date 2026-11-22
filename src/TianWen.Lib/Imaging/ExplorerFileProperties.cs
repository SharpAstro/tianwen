using System;
using System.Collections.Immutable;
using System.IO;
using nom.tam.fits;
using SharpAstro.Ser;

namespace TianWen.Lib.Imaging;

/// <summary>
/// The Explorer detail-column values of a FITS (<c>.fits .fit .fts .fz</c>) or SER file, read from a seekable
/// stream WITHOUT reading its pixels. One pure, platform-neutral reader, so the shell DLL that serves Explorer
/// only marshals these fields into properties (#2429).
/// </summary>
/// <remarks>
/// <para>A field the file does not state is <c>null</c> (or empty for the tags), never 0 and never a made-up
/// value. A FITS header becomes an <see cref="ImageMeta"/> in one place, <c>ParseImageMetaFromHeader</c>, so
/// this reader adds no card parsing of its own beyond asking whether a card is present at all.</para>
/// <para><b>Read never throws</b>: it sits under a COM boundary, so a malformed or truncated input gives
/// <see cref="Empty"/>, except that a SER whose header read keeps the fields read before a damaged or short
    /// timestamp trailer. A FITS whose header cannot be parsed gives <see cref="Empty"/>.</para>
/// <para>A FITS data block and a SER frame block are skipped by seeking, never read. A SER's per-frame timestamp
/// trailer is read at its first and last entry only.</para>
/// </remarks>
public sealed record ExplorerFileProperties
{
    /// <summary>The answer for a stream that is neither FITS nor SER, or is unreadable.</summary>
    public static ExplorerFileProperties Empty { get; } = new();

    public string? CameraModel { get; init; }
    public string? CameraMaker { get; init; }
    public string? LensModel { get; init; }
    public string? LensMaker { get; init; }
    public double? ExposureSeconds { get; init; }
    public double? FocalLengthMm { get; init; }
    public double? FNumber { get; init; }
    public ushort? IsoSpeed { get; init; }
    public DateTimeOffset? DateTaken { get; init; }
    public string? Subject { get; init; }
    public ImmutableArray<string> Tags { get; init; } = [];
    public string? Author { get; init; }
    public string? ProgramName { get; init; }
    public uint? Width { get; init; }
    public uint? Height { get; init; }
    public uint? BitDepth { get; init; }
    public TimeSpan? Duration { get; init; }
    public double? FrameRate { get; init; }

    private static ReadOnlySpan<byte> FitsMagic => "SIMPLE  ="u8;
    private static ReadOnlySpan<byte> SerMagic => "LUCAM-RECORDER"u8;

    // Matched at the start of INSTRUME / TELESCOP, case-insensitively; the first hit wins. The value is the
    // maker as the brand writes it. A prefix that is a whole word ends in a space ("FLI "), so FLIR is not FLI;
    // the bare name "FLI" is matched by MakerOf as an exact value.
    private static readonly (string Prefix, string Maker)[] CameraMakers =
    [
        ("ZWO", "ZWO"), ("QHY", "QHYCCD"), ("Canon", "Canon"), ("Nikon", "Nikon"), ("Sony", "Sony"),
        ("Atik", "Atik"), ("Moravian", "Moravian Instruments"), ("Player One", "Player One"), ("PlayerOne", "Player One"),
        ("SBIG", "SBIG"), ("FLI ", "Finger Lakes Instrumentation"), ("Altair", "Altair Astro"), ("ToupTek", "ToupTek"),
        ("Starlight Xpress", "Starlight Xpress"),
    ];

    private static readonly (string Prefix, string Maker)[] OpticsMakers =
    [
        ("Celestron", "Celestron"), ("Sky-Watcher", "Sky-Watcher"), ("SkyWatcher", "Sky-Watcher"),
        ("William Optics", "William Optics"), ("Askar", "Askar"), ("Takahashi", "Takahashi"),
        ("Explore Scientific", "Explore Scientific"), ("Meade", "Meade"), ("Sharpstar", "Sharpstar"),
        ("Stellarvue", "Stellarvue"), ("Vixen", "Vixen"),
    ];

    /// <summary>The maker of a camera named <paramref name="name"/>, or null when the brand is not known.</summary>
    public static string? CameraMakerOf(string? name) => MakerOf(CameraMakers, name);

    /// <summary>The maker of the optics named <paramref name="name"/>, or null when the brand is not known.</summary>
    public static string? OpticsMakerOf(string? name) => MakerOf(OpticsMakers, name);

    private static string? MakerOf((string Prefix, string Maker)[] table, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.AsSpan().Trim();
        foreach (var (prefix, maker) in table)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || (prefix[^1] == ' ' && trimmed.Equals(prefix.AsSpan().TrimEnd(), StringComparison.OrdinalIgnoreCase)))
            {
                return maker;
            }
        }

        return null;
    }

    /// <summary>Reads the properties of the FITS or SER file in <paramref name="source"/>; never throws.</summary>
    public static ExplorerFileProperties Read(Stream source)
    {
        try
        {
            if (source is null || !source.CanSeek || !source.CanRead)
            {
                return Empty;
            }

            var start = source.Position;
            Span<byte> head = stackalloc byte[14];
            var read = source.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            source.Position = start;

            if (read >= FitsMagic.Length && head[..FitsMagic.Length].SequenceEqual(FitsMagic))
            {
                return ReadFits(source);
            }

            if (read >= SerMagic.Length && head.SequenceEqual(SerMagic))
            {
                return ReadSer(source, start);
            }
        }
        catch (Exception)
        {
            // Under a COM boundary: an unreadable stream is an empty answer.
        }

        return Empty;
    }

    private static ExplorerFileProperties ReadFits(Stream source)
    {
        using var fits = new Fits(source);
        if (fits.ReadFirstImageHduHeaderOnly() is not { Axes: { Length: >= 2 } axes } hdu)
        {
            return Empty;
        }

        var header = hdu.Header;

        // hdu.ObservationDate throws on a missing or unparseable DATE-OBS, and the one parse below reads it.
        // A placeholder lets the rest of the header through; the date itself is reported only when it was real.
        DateTimeOffset? dateTaken = null;
        try
        {
            // A missing DATE-OBS may come back as the default (year 1) rather than throw: that is no date taken.
            var observed = hdu.ObservationDate;
            if (header.ContainsKey("DATE-OBS") && observed.Ticks > 0)
            {
                dateTaken = new DateTimeOffset(new DateTime(observed.Ticks, DateTimeKind.Utc));
            }
        }
        catch (Exception)
        {
            header.AddValue("DATE-OBS", "1970-01-01T00:00:00", null);
        }

        var channelCount = axes.Length == 3 ? axes[0] : 1;
        var meta = Image.ParseImageMetaFromHeader(hdu, channelCount);

        // A 2-D image is [NAXIS2, NAXIS1] in FITS.Lib's order, a 3-D one [planes, rows, columns].
        var (height, width) = axes.Length == 3 ? (axes[1], axes[2]) : (axes[0], axes[1]);

        var hasExposure = header.ContainsKey("EXPTIME") || header.ContainsKey("EXPOSURE");
        double? focal = meta.FocalLength > 0 ? meta.FocalLength : null;
        double? aperture = meta.Aperture > 0 ? meta.Aperture : null;

        // ImageMeta holds both as whole millimetres; the f-number is taken from the cards' own values where
        // they are numbers, so 270.5 / 80.0 is not rounded before the division. Same card names as the one parse.
        var rawFocal = header.GetDoubleValue("FOCALLEN", header.GetDoubleValue("FOCLEN", -1.0));
        var rawAperture = header.GetDoubleValue("APTDIA", -1.0);
        double? fNumber = rawFocal > 0 && rawAperture > 0 ? rawFocal / rawAperture
            : focal is { } f0 && aperture is { } a0 ? f0 / a0 : null;

        var tags = ImmutableArray.CreateBuilder<string>();
        if (meta.Filter.RawName?.Trim() is { Length: > 0 } filterName)
        {
            tags.Add(filterName);
        }

        if (meta.FrameType != FrameType.None)
        {
            tags.Add(meta.FrameType.ToFITSValue());
        }

        return new ExplorerFileProperties
        {
            CameraModel = NullIfBlank(meta.Instrument),
            CameraMaker = CameraMakerOf(meta.Instrument),
            LensModel = NullIfBlank(meta.Telescope),
            LensMaker = OpticsMakerOf(meta.Telescope),
            ExposureSeconds = hasExposure ? meta.ExposureDuration.TotalSeconds : null,
            FocalLengthMm = focal,
            FNumber = fNumber,
            IsoSpeed = meta.Iso is > 0 and <= ushort.MaxValue ? (ushort)meta.Iso : null,
            DateTaken = dateTaken,
            Subject = NullIfBlank(meta.ObjectName),
            Tags = tags.ToImmutable(),
            Author = NullIfBlank(meta.Observer),
            ProgramName = NullIfBlank(meta.SWCreator),
            Width = width > 0 ? (uint)width : null,
            Height = height > 0 ? (uint)height : null,
            BitDepth = hdu.BitPix != 0 ? (uint)Math.Abs(hdu.BitPix) : null,
        };
    }

    private static ExplorerFileProperties ReadSer(Stream source, long start)
    {
        Span<byte> headerBytes = stackalloc byte[SerHeader.Size];
        source.ReadExactly(headerBytes);
        var header = SerHeader.Parse(headerBytes);

        var props = new ExplorerFileProperties
        {
            CameraModel = NullIfBlank(header.Instrument),
            CameraMaker = CameraMakerOf(header.Instrument),
            LensModel = NullIfBlank(header.Telescope),
            LensMaker = OpticsMakerOf(header.Telescope),
            Author = NullIfBlank(header.Observer),
            DateTaken = header.DateTimeUtcTicks > 0 && header.DateTimeUtcTicks <= DateTime.MaxValue.Ticks
                ? new DateTimeOffset(header.DateTimeUtcTicks, TimeSpan.Zero) : null,
            Width = header.Width > 0 ? (uint)header.Width : null,
            Height = header.Height > 0 ? (uint)header.Height : null,
            BitDepth = header.PixelDepthPerPlane > 0 ? (uint)header.PixelDepthPerPlane : null,
        };

        // The trailer is one 8-byte tick count per frame after the frame block. Seek there; the frames are never read.
        var frames = (long)header.FrameCount;
        if (frames < 2 || header.FrameSizeBytes <= 0)
        {
            return props;
        }

        var trailerStart = start + SerHeader.Size + (frames * header.FrameSizeBytes);
        if (source.Length < trailerStart + (frames * 8))
        {
            return props;
        }

        long first, last;
        try
        {
            Span<byte> tick = stackalloc byte[8];
            source.Position = trailerStart;
            source.ReadExactly(tick);
            first = BitConverter.ToInt64(tick);
            source.Position = trailerStart + ((frames - 1) * 8);
            source.ReadExactly(tick);
            last = BitConverter.ToInt64(tick);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException)
        {
            // A damaged trailer costs the duration and rate only; the header fields already read stay.
            return props;
        }

        if (first <= 0 || last <= first || last > DateTime.MaxValue.Ticks)
        {
            return props;
        }

        var length = TimeSpan.FromTicks(last - first);
        return props with
        {
            Duration = length,
            // N frames span N-1 intervals, so the rate is over that, as SerReader.FramesPerSecond reports it.
            FrameRate = (frames - 1) / length.TotalSeconds,
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
