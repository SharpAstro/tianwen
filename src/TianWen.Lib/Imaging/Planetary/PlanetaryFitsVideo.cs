using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using SharpAstro.Ser;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>Where a FITS video's SER may go: the space its drive keeps free (<see cref="ScratchSpace"/>).</summary>
/// <param name="KeepFreeBytes">What the output's drive keeps free.</param>
/// <param name="FreeBytesOf">The free space of a path's drive; the real drive's when null (tests pass their own).</param>
public sealed record FitsVideoOptions(long KeepFreeBytes = ScratchSpace.DefaultKeepFreeBytes, Func<string, long>? FreeBytesOf = null);

/// <summary>
/// The record beside a SER converted from a FITS video: the folder and the file each frame came from (frame i is
/// <see cref="Files"/>[i]), the first frame's header cards as written, and what the trailer's times are.
/// </summary>
public sealed record FitsVideoSidecar(
    int Version,
    string Source,
    string SourceId,
    int Frames,
    string Timestamps,
    bool? TimestampsMonotonic,
    string[] Files,
    SortedDictionary<string, string> FirstHeader);

/// <summary>A FITS video made a SER and verified: where it is and what it holds.</summary>
public sealed record FitsVideoResult(string Output, string Sidecar, int Frames, int Width, int Height, SerColorId ColorId, int Depth, long OutputBytes);

/// <summary>What a conversion came to: a verified <see cref="Result"/>, or why it did nothing (<see cref="Refusal"/>, in words).</summary>
public sealed record FitsVideoOutcome(FitsVideoResult? Result, string? Refusal);

/// <summary>
/// A planetary video saved one FITS file a frame (SharpCap does it when its output format is left on FITS, which for the
/// 2022 Jupiter LRGB captures was an accident) made the SER it should have been (docs/plans/planetary-restoration.md, R0),
/// so every later phase reads one format. The samples are the files' own, the trailer holds each frame's DATE-OBS (its
/// exposure start) to the tick, and the sidecar keeps what a SER header cannot: the file of every frame and the first
/// frame's cards. The SER is read back and compared with the FILES' bytes and dates, not with what this program's reader
/// made of them, before it is kept; the folder is never written.
/// </summary>
public static class PlanetaryFitsVideo
{
    /// <summary>The sidecar's format version.</summary>
    public const int SidecarVersion = 1;

    /// <summary>
    /// Converts the FITS frames directly in <paramref name="folder"/> into <c>&lt;name&gt;.&lt;id&gt;.ser</c> in
    /// <paramref name="outputDirectory"/>, with its sidecar <c>.ser.json</c>, where the id is the survey's for the sequence.
    /// A layout it does not take (only a primary image of two axes, 8 bits or unsigned 16, is) and a drive that would dip into
    /// its reserve are refusals, and nothing is written; a frame that does not match the first throws, leaving nothing.
    /// </summary>
    public static FitsVideoOutcome Convert(string folder, string outputDirectory, FitsVideoOptions options, ILogger logger, CancellationToken cancellationToken)
    {
        var (files, id, _) = PlanetaryCorpus.SequenceOf(PlanetaryCorpus.FitsFramesIn(folder));
        if (files.Length == 0)
        {
            return new FitsVideoOutcome(null, $"No FITS frames in {folder}");
        }
        var head = RawHeader.Read(files[0]);
        if (head.Refusal() is { } layout)
        {
            return new FitsVideoOutcome(null, $"{files[0]}: {layout}");
        }
        if (!Image.TryReadFitsFile(files[0], out var first, out _, pooled: true))
        {
            return new FitsVideoOutcome(null, $"{files[0]} cannot be read");
        }

        var (width, height, channels) = (first.Width, first.Height, first.ChannelCount);
        var color = SerImageBridge.SerColorOf(first.ImageMeta, channels);
        var depth = SerImageBridge.SerDepthOf(first);
        var meta = first.ImageMeta;
        first.Release();
        var timed = HasTime(meta);
        var frameBytes = width * height * color.PlaneCount * (depth / 8);
        var outputBytes = SerHeader.Size + ((long)files.Length * frameBytes) + (timed ? files.Length * 8L : 0);
        var sidecarBytes = 8192 + files.Sum(static f => Path.GetFileName(f).Length + 8L);
        Directory.CreateDirectory(outputDirectory);
        if (ScratchSpace.Refusal(outputDirectory, outputBytes + sidecarBytes, options.KeepFreeBytes, options.FreeBytesOf) is { } refusal)
        {
            return new FitsVideoOutcome(null, refusal);
        }

        var output = Path.Combine(outputDirectory, $"{NameOf(files[0], folder)}.{id}.ser");
        var sidecarPath = output + ".json";
        var partial = output + ".partial";
        var samples = new byte[frameBytes];
        bool? monotonic = timed ? true : null;
        var previous = DateTimeOffset.MinValue;
        try
        {
            using (var writer = new SerWriter(partial, width, height, color, depth, instrument: meta.Instrument, telescope: meta.Telescope))
            {
                for (var i = 0; i < files.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Image.TryReadFitsFile(files[i], out var frame, out _, pooled: true))
                    {
                        throw new InvalidDataException($"Frame {i}, {files[i]}, cannot be read");
                    }
                    try
                    {
                        if ((frame.Width, frame.Height, frame.ChannelCount, SerImageBridge.SerDepthOf(frame)) != (width, height, channels, depth))
                        {
                            throw new InvalidDataException(
                                $"Frame {i}, {files[i]}, is {frame.Width}x{frame.Height} in {SerImageBridge.SerDepthOf(frame)} bits, not the {width}x{height} in {depth} of the first");
                        }
                        SerImageBridge.FillSerFrame(frame, depth, samples);
                        if (!timed)
                        {
                            writer.AppendFrame(samples);
                            continue;
                        }
                        if (!HasTime(frame.ImageMeta))
                        {
                            throw new InvalidDataException($"Frame {i}, {files[i]}, has no DATE-OBS where the first has one");
                        }
                        var start = frame.ImageMeta.ExposureStartTime;
                        if (start < previous)
                        {
                            monotonic = false;
                        }
                        previous = start;
                        writer.AppendFrame(samples, start);
                    }
                    finally
                    {
                        frame.Release();
                    }
                    if (i > 0 && i % 5_000 == 0)
                    {
                        logger.LogInformation("Converted {Frame} of {Frames} frames of {Folder}", i, files.Length, folder);
                    }
                }
            }

            Verify(files, partial, width, height, color, depth, timed, cancellationToken);
            File.Move(partial, output, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        var sidecar = new FitsVideoSidecar(SidecarVersion, PlanetaryCorpus.Normalise(folder), id, files.Length,
            timed ? "date-obs" : "none", monotonic, [.. files.Select(static f => Path.GetFileName(f))], head.Cards);
        var sidecarPartial = sidecarPath + ".partial";
        File.WriteAllBytes(sidecarPartial, [.. JsonSerializer.SerializeToUtf8Bytes(sidecar, PlanetaryCorpusJsonContext.Default.FitsVideoSidecar), (byte)'\n']);
        File.Move(sidecarPartial, sidecarPath, overwrite: true);

        var result = new FitsVideoResult(output, sidecarPath, files.Length, width, height, color, depth, new FileInfo(output).Length);
        logger.LogInformation("Converted {Folder}: {Frames} FITS frames of {Width}x{Height} {Color} {Depth} bit into {Output}, verified",
            folder, files.Length, width, height, color, depth, output);
        return new FitsVideoOutcome(result, null);
    }

    // A missing DATE-OBS reads as year 1, not as null.
    private static bool HasTime(in ImageMeta meta) => meta.ExposureStartTime.Year > 1;

    // SharpCap names each frame "<capture>_00001.fits": the capture's name is the first frame's without its number.
    private static string NameOf(string firstFile, string folder)
    {
        var stem = Path.GetFileNameWithoutExtension(firstFile);
        var cut = stem.LastIndexOf('_');
        var name = cut > 0 && stem.AsSpan(cut + 1).IndexOfAnyExceptInRange('0', '9') < 0 ? stem[..cut] : stem;
        return name.Length > 0 ? name : Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
    }

    // The SER read back against each FILE, independently of this program's FITS reader: its data block's own samples (the
    // unsigned 16-bit offset undone, a bottom-up frame turned over) and its DATE-OBS card's own text, parsed here.
    private static void Verify(string[] files, string ser, int width, int height, SerColorId color, int depth, bool timed, CancellationToken cancellationToken)
    {
        using var check = SerReader.Open(ser);
        if (check.FrameCount != files.Length || check.Width != width || check.Height != height || check.ColorId != color || check.PixelDepthPerPlane != depth)
        {
            throw new InvalidDataException(
                $"The SER reads back as {check.FrameCount} frames of {check.Width}x{check.Height} {check.ColorId} {check.PixelDepthPerPlane} bit, not the {files.Length} of {width}x{height} {color} {depth} bit written");
        }
        if (check.HasTimestamps != timed)
        {
            throw new InvalidDataException($"The SER {(timed ? "has no" : "has")} timestamps where the files {(timed ? "have" : "have none")}");
        }

        var expected = new byte[check.FrameSizeBytes];
        var actual = new byte[check.FrameSizeBytes];
        for (var i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var head = RawHeader.Read(files[i]);
            head.ReadSamples(files[i], width, height, expected);
            check.ReadFrameBytes(i, actual);
            if (!expected.AsSpan().SequenceEqual(actual))
            {
                throw new InvalidDataException($"Frame {i} of the SER is not {files[i]}'s samples");
            }
            if (timed && head.DateObs() is var date && date != check.Timestamps[i])
            {
                throw new InvalidDataException($"Frame {i}'s time {check.Timestamps[i]:o} is not {files[i]}'s DATE-OBS {date:o}");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left for the next run to overwrite; a conversion never trusts a partial file.
        }
    }

    /// <summary>
    /// A FITS file's primary header as its cards say it, and where its data starts: the file's own words, for the check and
    /// the sidecar, never an <see cref="ImageMeta"/> (which the one header parse makes).
    /// </summary>
    private sealed class RawHeader
    {
        private const int Block = 2880;
        private const int Card = 80;

        private RawHeader(SortedDictionary<string, string> cards, long dataOffset)
        {
            Cards = cards;
            DataOffset = dataOffset;
        }

        /// <summary>Each card's value as written (a string without its quotes and padding), by keyword.</summary>
        public SortedDictionary<string, string> Cards { get; }

        public long DataOffset { get; }

        public static RawHeader Read(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var cards = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var block = new byte[Block];
            for (long offset = 0; ; offset += Block)
            {
                stream.ReadExactly(block);
                for (var at = 0; at < Block; at += Card)
                {
                    var card = Encoding.ASCII.GetString(block, at, Card);
                    var keyword = card[..8].TrimEnd();
                    if (keyword == "END")
                    {
                        return new RawHeader(cards, offset + Block);
                    }
                    if (card.AsSpan(8, 2).SequenceEqual("= ") && !cards.ContainsKey(keyword))
                    {
                        cards[keyword] = ValueOf(card[10..]);
                    }
                }
            }
        }

        // A card's value: a quoted string (a doubled quote is a quote), or what stands before the comment's slash.
        private static string ValueOf(string text)
        {
            var trimmed = text.TrimStart();
            if (!trimmed.StartsWith('\''))
            {
                var slash = trimmed.IndexOf('/');
                return (slash >= 0 ? trimmed[..slash] : trimmed).Trim();
            }
            var value = new StringBuilder();
            for (var i = 1; i < trimmed.Length; i++)
            {
                if (trimmed[i] == '\'')
                {
                    if (i + 1 < trimmed.Length && trimmed[i + 1] == '\'')
                    {
                        value.Append('\'');
                        i++;
                        continue;
                    }
                    break;
                }
                value.Append(trimmed[i]);
            }
            return value.ToString().TrimEnd();
        }

        private int Integer(string keyword, int absent) => Cards.TryGetValue(keyword, out var text)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : absent;

        private double Real(string keyword, double absent) => Cards.TryGetValue(keyword, out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : absent;

        private bool BottomUp => Cards.TryGetValue("ROWORDER", out var order) && order.Equals("BOTTOM-UP", StringComparison.OrdinalIgnoreCase);

        /// <summary>Why this file is not a frame the conversion takes, in words; null when it is.</summary>
        public string? Refusal()
        {
            if (Integer("NAXIS", 0) != 2)
            {
                return $"NAXIS = {Integer("NAXIS", 0)}: only a primary image of two axes is converted (no FITS video in the corpus has another)";
            }
            if (Real("BSCALE", 1) != 1)
            {
                return $"BSCALE = {Real("BSCALE", 1)}: scaled samples are not a camera's";
            }
            return (Integer("BITPIX", 0), Real("BZERO", 0)) switch
            {
                (8, 0) => null,
                (16, 32768) => null,
                var (bitpix, bzero) => $"BITPIX = {bitpix}, BZERO = {bzero}: only 8-bit and unsigned 16-bit samples are converted",
            };
        }

        /// <summary>The data block's samples as a SER frame holds them: host order, top row first.</summary>
        public void ReadSamples(string path, int width, int height, Span<byte> destination)
        {
            var bytesPerSample = Integer("BITPIX", 0) / 8;
            var rowBytes = width * bytesPerSample;
            var raw = new byte[rowBytes * height];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                stream.Position = DataOffset;
                stream.ReadExactly(raw);
            }
            for (var row = 0; row < height; row++)
            {
                var from = raw.AsSpan((BottomUp ? height - 1 - row : row) * rowBytes, rowBytes);
                var to = destination.Slice(row * rowBytes, rowBytes);
                if (bytesPerSample == 1)
                {
                    from.CopyTo(to);
                    continue;
                }
                for (var x = 0; x < width; x++)
                {
                    // Stored signed and big-endian with BZERO 32768: the unsigned sample is the stored one plus 32768.
                    var sample = (ushort)(BinaryPrimitives.ReadInt16BigEndian(from[(x * 2)..]) + 32768);
                    if (BitConverter.IsLittleEndian)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(to[(x * 2)..], sample);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(to[(x * 2)..], sample);
                    }
                }
            }
        }

        /// <summary>The DATE-OBS card as UTC, parsed from its own text (a FITS date carries no zone and means UTC).</summary>
        public DateTimeOffset DateObs() => Cards.TryGetValue("DATE-OBS", out var text)
            ? new DateTimeOffset(DateTime.SpecifyKind(DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), DateTimeKind.Utc))
            : DateTimeOffset.MinValue;
    }
}
