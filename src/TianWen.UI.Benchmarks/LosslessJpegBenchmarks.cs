using System;
using System.Buffers.Binary;
using System.IO;
using BenchmarkDotNet.Attributes;
using StbImageSharp;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Isolated benchmark for the SOF3 lossless-JPEG decoder in
/// <c>StbImageSharp</c>. The decoder is the dominant cost in CR2 import
/// (the <c>dotnet-trace</c> cpu-sampling profile puts it at ~80% inclusive
/// time on a Canon EOS 6D CR2), but in <see cref="ImageReadBenchmarks"/>
/// that's bundled with the TIFF walk, slice unscramble, EXIF parse, and
/// the <c>Image</c> construction wrappers — so timing changes upstream
/// of FC.SDK.Raw are hard to attribute.
///
/// <para>This benchmark extracts the entropy-coded JPEG strip from the
/// committed CR2 fixture once in <see cref="GlobalSetup"/> (the CR2's
/// IFD3 has Compression=6 + StripOffsets/StripByteCounts pointing at a
/// SOF3 LJPEG), then loops <see cref="LosslessJpeg.FromMemory(ReadOnlySpan{byte})"/>
/// over the cached bytes. Result: a pure decode-time number with zero
/// upstream noise — the right thing to watch when optimizing the
/// Huffman / predictor inner loops in the StbImageSharp sibling repo.</para>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class LosslessJpegBenchmarks
{
    private byte[] _jpegStrip = null!;

    [GlobalSetup]
    public void Setup()
    {
        var cr2Path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "CR2", "_MG_7578.CR2");
        if (!File.Exists(cr2Path))
        {
            throw new FileNotFoundException(
                $"CR2 fixture missing at {cr2Path} — `git lfs pull --include=\"*.CR2\"` " +
                "from the TianWen working tree to materialise it.", cr2Path);
        }
        var bytes = File.ReadAllBytes(cr2Path);
        _jpegStrip = ExtractRawJpegStrip(bytes);
    }

    [Benchmark(Description = "LosslessJpeg.FromMemory on Canon EOS 6D CR2 raw IFD strip (~18 MB SOF3 lossless JPEG)")]
    public LosslessJpegResult DecodeRawIfdStrip()
        => LosslessJpeg.FromMemory(_jpegStrip);

    /// <summary>Walk the outer TIFF in a Canon CR2 to find the raw IFD —
    /// the one with <c>Compression == 6</c> (old-style JPEG) — and return
    /// the bytes at <c>StripOffsets[0]</c> for <c>StripByteCounts[0]</c>
    /// bytes. That payload is the entropy-coded SOF3 lossless JPEG the
    /// CR2 spec puts there; <see cref="LosslessJpeg.FromMemory"/> consumes
    /// it directly. Self-contained walker so the benchmark project doesn't
    /// pull in a TIFF library or peek at FC.SDK.Raw's internals.</summary>
    private static byte[] ExtractRawJpegStrip(byte[] bytes)
    {
        var span = bytes.AsSpan();
        // Byte-order mark: "II" = little-endian, "MM" = big-endian. Canon
        // CR2 is always "II" but we accept both for completeness.
        var littleEndian = span[0] == 'I' && span[1] == 'I';
        if (!littleEndian && !(span[0] == 'M' && span[1] == 'M'))
            throw new InvalidDataException("not a TIFF (missing II/MM byte order mark)");
        if (ReadU16(span.Slice(2, 2), littleEndian) != 42)
            throw new InvalidDataException("not a TIFF (magic != 42)");

        var ifdOffset = (int)ReadU32(span.Slice(4, 4), littleEndian);
        while (ifdOffset != 0)
        {
            var nEntries = ReadU16(span.Slice(ifdOffset, 2), littleEndian);
            var tags = span.Slice(ifdOffset + 2, nEntries * 12);
            // Pull the four tags we care about; ignore the rest.
            var compression = 0;
            var stripOffset = 0;
            var stripByteCount = 0;
            var hasCr2Slice = false;
            for (var i = 0; i < nEntries; i++)
            {
                var entry = tags.Slice(i * 12, 12);
                var tag = ReadU16(entry.Slice(0, 2), littleEndian);
                // type at entry[2..4], count at entry[4..8], value/offset at entry[8..12].
                if (tag == 0x0103) compression = ReadU16(entry.Slice(8, 2), littleEndian);
                else if (tag == 0x0111) stripOffset = (int)ReadU32(entry.Slice(8, 4), littleEndian);
                else if (tag == 0x0117) stripByteCount = (int)ReadU32(entry.Slice(8, 4), littleEndian);
                // Tag 0xC640 (CR2Slice) is the canonical raw-IFD marker. IFD0
                // also carries Compression=6 + StripOffsets, but only the raw
                // IFD has CR2Slice — without this check we'd grab the
                // embedded preview JPEG (SOF0 baseline, not SOF3 lossless).
                else if (tag == 0xC640) hasCr2Slice = true;
            }
            if (compression == 6 && stripOffset > 0 && stripByteCount > 0 && hasCr2Slice)
            {
                return bytes.AsSpan(stripOffset, stripByteCount).ToArray();
            }
            // Next IFD pointer follows the entries.
            ifdOffset = (int)ReadU32(span.Slice(ifdOffset + 2 + nEntries * 12, 4), littleEndian);
        }
        throw new InvalidDataException("no IFD with Compression=6 + StripOffsets found in CR2");
    }

    private static int ReadU16(ReadOnlySpan<byte> s, bool le)
        => le ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
    private static uint ReadU32(ReadOnlySpan<byte> s, bool le)
        => le ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
}
