using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpAstro.Ser;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>How a crop sizes its window and how much of the drive it must leave free.</summary>
/// <param name="Width">The window's width; measured from the disk when null.</param>
/// <param name="Height">The window's height; measured from the disk when null.</param>
/// <param name="Margin">Pixels kept around the disk's measured extent, on every side.</param>
/// <param name="SizeSamples">Frames, spread over the capture, whose disk extent sizes the window.</param>
/// <param name="KeepFreeBytes">What the output's drive keeps free (<see cref="ScratchSpace"/>).</param>
/// <param name="FreeBytesOf">The free space of a path's drive; the real drive's when null (tests pass their own).</param>
public sealed record CropOptions(
    int? Width = null,
    int? Height = null,
    int Margin = 32,
    int SizeSamples = 200,
    long KeepFreeBytes = ScratchSpace.DefaultKeepFreeBytes,
    Func<string, long>? FreeBytesOf = null);

/// <summary>
/// The record beside a crop: what it was cut from, the source's header (which the crop's own repeats byte for byte, bar its
/// width and height), and where each frame's window sat on the source, so the absolute image motion survives.
/// </summary>
public sealed record CropSidecar(
    int Version,
    string Source,
    string SourceId,
    long SourceLengthBytes,
    SerHeaderRecord SourceHeader,
    int Width,
    int Height,
    int Frames,
    int Located,
    int[] OriginX,
    int[] OriginY,
    bool[] LocatedFrames);

/// <summary>A crop made and verified: where it is, its window and how many frames found the disk.</summary>
public sealed record CropResult(string Output, string Sidecar, int Frames, int Width, int Height, int Located, long OutputBytes);

/// <summary>What a crop came to: a verified <see cref="Result"/>, or why it did nothing (<see cref="Refusal"/>, in words).</summary>
public sealed record CropOutcome(CropResult? Result, string? Refusal);

/// <summary>
/// A planetary capture cropped to a window tracked on its disk (docs/plans/planetary-restoration.md, R0), 15 to 20 times
/// smaller for a whole-sensor capture of a small disk. Unlike a PIPP crop it keeps EVERY frame, keeps the window's origin
/// even so a Bayer mosaic keeps its phase, keeps the source's header and its trailer byte for byte (<see cref="SerReader.CropTo"/>:
/// the start times and the site's time zone with them, the trailer never re-encoded), and records each frame's window
/// origin. The crop is read back and compared with the source byte for byte before it is kept; the source is never written.
/// </summary>
public static class PlanetaryCrop
{
    /// <summary>The sidecar's format version.</summary>
    public const int SidecarVersion = 1;

    /// <summary>
    /// Crops <paramref name="source"/> into <paramref name="outputDirectory"/> as <c>&lt;name&gt;.&lt;id&gt;.crop.ser</c> with its
    /// sidecar <c>.crop.json</c>. A window that would cover most of the frame, a disk that cannot be told from the sky, and a
    /// drive that would dip into its reserve are refusals, and nothing is written.
    /// </summary>
    public static CropOutcome Crop(string source, string outputDirectory, CropOptions options, ILogger logger, CancellationToken cancellationToken,
        string? sourceLabel = null)
    {
        var sourceLength = new FileInfo(source).Length;
        var sourceId = ContentIdOf(source, sourceLength);
        using var reader = SerReader.Open(source);
        var header = reader.Header;
        var pixelBytes = header.PlaneCount * header.BytesPerSample;
        var frames = header.FrameCount;
        if (frames <= 0)
        {
            return new CropOutcome(null, $"{source} holds no frames");
        }

        var size = options is { Width: { } w, Height: { } h }
            ? (Width: Even(Math.Min(w, header.Width)), Height: Even(Math.Min(h, header.Height)))
            : MeasureWindow(reader, options);
        if (size is not { } window)
        {
            return new CropOutcome(null, $"The disk in {source} cannot be told from the sky in its sampled frames, or it fills the frame: nothing to crop");
        }
        if (window.Width >= header.Width * 0.9 && window.Height >= header.Height * 0.9)
        {
            return new CropOutcome(null,
                $"The disk in {source} needs a {window.Width}x{window.Height} window of its {header.Width}x{header.Height} frame: nothing to gain from a crop");
        }

        var stem = Path.GetFileNameWithoutExtension(source);
        var output = Path.Combine(outputDirectory, $"{stem}.{sourceId}.crop.ser");
        var sidecarPath = Path.ChangeExtension(output, ".json");
        var windowBytes = window.Width * window.Height * pixelBytes;
        // The header, the windows, and everything after the source's frames (its trailer), which the crop copies as it is.
        var outputBytes = SerHeader.Size + ((long)frames * windowBytes) + (sourceLength - SerHeader.Size - (frames * reader.FrameSizeBytes));
        // The sidecar's two origins and a flag a frame, generously.
        var sidecarBytes = 4096 + (frames * 24L);
        Directory.CreateDirectory(outputDirectory);
        if (ScratchSpace.Refusal(outputDirectory, outputBytes + sidecarBytes, options.KeepFreeBytes, options.FreeBytesOf) is { } refusal)
        {
            return new CropOutcome(null, refusal);
        }

        var originX = new int[frames];
        var originY = new int[frames];
        var located = new bool[frames];
        var partial = output + ".partial";
        var frame = new byte[reader.FrameSizeBytes];
        var (lastX, lastY) = (Even((header.Width - window.Width) / 2), Even((header.Height - window.Height) / 2));
        try
        {
            reader.CropTo(partial, window.Width, window.Height, i =>
            {
                reader.ReadFrameBytes(i, frame);
                if (Locate(header, frame) is { } centre)
                {
                    located[i] = true;
                    lastX = Origin(centre.X, window.Width, header.Width);
                    lastY = Origin(centre.Y, window.Height, header.Height);
                }
                // A frame the disk cannot be found in (a cloud, a slew) is kept, at the last window.
                originX[i] = lastX;
                originY[i] = lastY;
                if (i > 0 && i % 10_000 == 0)
                {
                    logger.LogInformation("Cropped {Frame} of {Frames} frames of {Source}", i, frames, source);
                }
                return (lastX, lastY);
            }, cancellationToken);

            Verify(reader, source, partial, originX, originY, window.Width, window.Height, cancellationToken);
            File.Move(partial, output, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        var sidecar = new CropSidecar(SidecarVersion, sourceLabel ?? PlanetaryCorpus.Normalise(source), sourceId, sourceLength,
            SerHeaderRecord.From(header), window.Width, window.Height, frames, located.Count(static l => l), originX, originY, located);
        var sidecarPartial = sidecarPath + ".partial";
        File.WriteAllBytes(sidecarPartial, [.. JsonSerializer.SerializeToUtf8Bytes(sidecar, PlanetaryCorpusJsonContext.Default.CropSidecar), (byte)'\n']);
        File.Move(sidecarPartial, sidecarPath, overwrite: true);

        var result = new CropResult(output, sidecarPath, frames, window.Width, window.Height, sidecar.Located, new FileInfo(output).Length);
        logger.LogInformation("Cropped {Source} to {Width}x{Height}: {Frames} frames, the disk found in {Located}, {Bytes} bytes (was {SourceBytes}), verified",
            source, window.Width, window.Height, frames, result.Located, result.OutputBytes, sourceLength);
        return new CropOutcome(result, null);
    }

    /// <summary>
    /// Crops every SER inside <paramref name="archive"/>, one member at a time: unpacked into
    /// <c>&lt;outputDirectory&gt;/.unpack</c>, cropped, and the unpacked copy deleted, so the archive never needs its whole size
    /// free (and the folder with it once it is empty). Each member is checked against the drive's reserve before it is unpacked.
    /// </summary>
    public static async Task<IReadOnlyList<(string Member, CropOutcome Outcome)>> CropArchiveAsync(string archive, string outputDirectory,
        SevenZipTool sevenZip, CropOptions options, ILogger logger, CancellationToken cancellationToken)
    {
        var members = await sevenZip.ListAsync(archive, TimeSpan.FromMinutes(30), cancellationToken).ConfigureAwait(false);
        var unpack = Path.Combine(outputDirectory, ".unpack");
        var outcomes = new List<(string, CropOutcome)>();
        foreach (var member in members.Where(static m => m.Path.EndsWith(".ser", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ScratchSpace.Refusal(outputDirectory, member.Size, options.KeepFreeBytes, options.FreeBytesOf) is { } refusal)
            {
                outcomes.Add((member.Path, new CropOutcome(null, $"Not unpacked: {refusal}")));
                continue;
            }
            logger.LogInformation("Unpacking {Member} ({Bytes} bytes) from {Archive}", member.Path, member.Size, archive);
            string? unpacked = null;
            try
            {
                unpacked = await sevenZip.ExtractAsync(archive, member.Path, unpack, cancellationToken).ConfigureAwait(false);
                outcomes.Add((member.Path, Crop(unpacked, outputDirectory, options, logger, cancellationToken,
                    sourceLabel: $"{PlanetaryCorpus.Normalise(archive)}!{member.Path.Replace('\\', '/')}")));
            }
            finally
            {
                if (unpacked is not null)
                {
                    TryDelete(unpacked);
                }
            }
        }
        if (Directory.Exists(unpack) && !Directory.EnumerateFileSystemEntries(unpack).Any())
        {
            Directory.Delete(unpack);
        }
        return outcomes;
    }

    /// <summary>The same content identity the survey gives a SER on disk (<see cref="PlanetaryCorpus.ContentId"/>).</summary>
    public static string ContentIdOf(string path, long length)
    {
        var raw = new byte[SerHeader.Size];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.ReadExactly(raw);
        }
        using var reader = SerReader.Open(path);
        var first = new byte[reader.FrameSizeBytes];
        if (reader.FrameCount > 0)
        {
            reader.ReadFrameBytes(0, first);
        }
        return PlanetaryCorpus.ContentId(raw, length, reader.FrameCount > 0 ? first : []);
    }

    // The window from the disk's extent over frames spread through the capture: the 90th percentile of the sampled extents
    // (a Galilean moon or a hot pixel inflates a frame's box now and then), plus the margin on every side, rounded up to 16.
    private static (int Width, int Height)? MeasureWindow(SerReader reader, CropOptions options)
    {
        var header = reader.Header;
        var samples = Math.Max(1, Math.Min(options.SizeSamples, header.FrameCount));
        var frame = new byte[reader.FrameSizeBytes];
        var widths = new List<int>(samples);
        var heights = new List<int>(samples);
        for (var k = 0; k < samples; k++)
        {
            var index = samples == 1 ? 0 : (int)((long)k * (header.FrameCount - 1) / (samples - 1));
            reader.ReadFrameBytes(index, frame);
            var box = DiskBox(SerImageBridge.ToImage(header, frame));
            if (box != new Geometry.PixelRect(0, 0, header.Width, header.Height))
            {
                widths.Add(box.Width);
                heights.Add(box.Height);
            }
        }
        if (widths.Count == 0)
        {
            return null;
        }
        widths.Sort();
        heights.Sort();
        var p90 = (int)Math.Round(0.9 * (widths.Count - 1));
        var width = Math.Min(header.Width, RoundUp(widths[p90] + (2 * options.Margin), 16));
        var height = Math.Min(header.Height, RoundUp(heights[p90] + (2 * options.Margin), 16));
        return (Even(width), Even(height));
    }

    // The disk's centre of mass in one frame, or null when it cannot be told from the sky (PlanetaryDisk's own rule).
    private static (double X, double Y)? Locate(in SerHeader header, byte[] frame)
    {
        var image = SerImageBridge.ToImage(header, frame);
        var box = DiskBox(image);
        return box == new Geometry.PixelRect(0, 0, header.Width, header.Height) ? null : PlanetaryDisk.CenterOfMass(image, box);
    }

    // The disk's box, counting only pixels most of whose neighbours are bright too: a frame the planet has drifted out of is
    // pure noise, whose threshold falls to about one ADU, and its scattered pixels would otherwise box the whole frame (25 of
    // 200 sampled frames of a real Dobsonian capture did, 2026-09-29). One rule for the sizing and the tracking.
    private static Geometry.PixelRect DiskBox(Image image) => PlanetaryDisk.BoundingBox(image, pad: 0, minNeighbours: 4, minPixels: 64);

    // The window's origin for a disk centred at `centre`: even, so a Bayer mosaic keeps its phase, and inside the frame.
    private static int Origin(double centre, int window, int frame)
    {
        var origin = Even((int)Math.Floor(centre - (window / 2.0)));
        return Math.Clamp(origin, 0, Even(frame - window));
    }

    private static void CopyWindow(byte[] frame, int frameWidth, int pixelBytes, int x, int y, int width, int height, byte[] destination)
    {
        var rowBytes = width * pixelBytes;
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(frame, (((y + row) * frameWidth) + x) * pixelBytes, destination, row * rowBytes, rowBytes);
        }
    }

    // The crop read back: its geometry the window's, its header the source's byte for byte bar the width and height, every
    // frame the source's window, and everything after the frames (the trailer) the source's bytes.
    private static void Verify(SerReader source, string sourcePath, string crop, int[] originX, int[] originY, int width, int height,
        CancellationToken cancellationToken)
    {
        using var check = SerReader.Open(crop);
        var header = source.Header;
        if (check.FrameCount != header.FrameCount || check.Width != width || check.Height != height || check.ColorId != header.ColorId
            || check.PixelDepthPerPlane != header.PixelDepthPerPlane || check.Header.LittleEndianFlag != header.LittleEndianFlag)
        {
            throw new InvalidDataException(
                $"The crop reads back as {check.FrameCount} frames of {check.Width}x{check.Height} {check.ColorId} {check.PixelDepthPerPlane} bit, not the {header.FrameCount} of {width}x{height} {header.ColorId} {header.PixelDepthPerPlane} bit written");
        }

        var (sourceHead, sourceTail) = HeadAndTail(sourcePath, SerHeader.Size + ((long)header.FrameCount * source.FrameSizeBytes));
        var (cropHead, cropTail) = HeadAndTail(crop, SerHeader.Size + ((long)header.FrameCount * check.FrameSizeBytes));
        // Bytes 26 to 33 are the width and height, the only fields a crop changes.
        if (!sourceHead.AsSpan(0, 26).SequenceEqual(cropHead.AsSpan(0, 26)) || !sourceHead.AsSpan(34).SequenceEqual(cropHead.AsSpan(34)))
        {
            throw new InvalidDataException("The crop's header is not the source's");
        }
        if (!sourceTail.AsSpan().SequenceEqual(cropTail))
        {
            throw new InvalidDataException(
                $"The crop's {cropTail.Length} bytes after its frames (the timestamp trailer) are not the source's {sourceTail.Length}");
        }

        var pixelBytes = header.PlaneCount * header.BytesPerSample;
        var frame = new byte[source.FrameSizeBytes];
        var expected = new byte[check.FrameSizeBytes];
        var actual = new byte[check.FrameSizeBytes];
        for (var i = 0; i < header.FrameCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.ReadFrameBytes(i, frame);
            CopyWindow(frame, header.Width, pixelBytes, originX[i], originY[i], width, height, expected);
            check.ReadFrameBytes(i, actual);
            if (!expected.AsSpan().SequenceEqual(actual))
            {
                throw new InvalidDataException($"Frame {i} of the crop is not the source's window at ({originX[i]}, {originY[i]})");
            }
        }
    }

    // A SER's 178 header bytes and everything after its frames: the two parts a crop copies whole.
    private static (byte[] Head, byte[] Tail) HeadAndTail(string path, long framesEnd)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var head = new byte[SerHeader.Size];
        stream.ReadExactly(head);
        var tail = new byte[checked((int)(stream.Length - framesEnd))];
        stream.Position = framesEnd;
        stream.ReadExactly(tail);
        return (head, tail);
    }

    private static int Even(int value) => value & ~1;

    private static int RoundUp(int value, int step) => (value + step - 1) / step * step;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left for the next run to overwrite; a crop never trusts a partial file.
        }
    }
}
