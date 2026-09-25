using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using nom.tam.fits;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A plain FITS file is read by FITS.Lib's <c>FitsReader</c>, a plane at a time straight into the float
/// planes; a gzipped or tile-compressed one, and any layout the reader declines, by FITS.Lib's HDU reader.
/// Both must read a file into the same image, and the header-only read must describe the same HDU.
/// </summary>
/// <remarks>
/// The reader's side is <c>Image.TryReadThroughFitsReader</c>, never the public entry, which falls back
/// to the HDU reader when the reader declines: a comparison through it would pass for every file the
/// reader quietly refused, by comparing the HDU reader with itself. So each case also says whether the
/// reader must have taken it.
/// </remarks>
[Collection("Imaging")]
public class FitsReadPathParityTests
{
    // Odd sizes on purpose: every row and plane ends inside a vector, so the scalar tails run.
    private const int Width = 37, Height = 23;

    public static TheoryData<string, bool> Cases()
    {
        var cases = new TheoryData<string, bool>();
        foreach (var name in CaseNames())
        {
            cases.Add(name, false);
            cases.Add(name, true);
        }

        return cases;
    }

    private static IEnumerable<string> CaseNames()
    {
        foreach (var bitpix in new[] { 8, 16, 32, -32 })
        {
            foreach (var scaling in new[] { "none", "unsigned", "scaled" })
            {
                yield return $"b{bitpix}_{scaling}_1p";
                yield return $"b{bitpix}_{scaling}_3p";
            }
        }

        yield return "tianwen_int8";
        yield return "tianwen_int16_wcs";
        yield return "tianwen_int32";
        yield return "tianwen_float_rgb";
        yield return "float_specials";
        yield return "empty_primary_then_image";
        yield return "placeholder_table_image";
        yield return "b64_none_1p";
        yield return "b-64_none_1p";
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void BothReadPathsReadTheSameImage(string name, bool pooled)
    {
        var path = Path.Combine(SharedTestData.CreateTempTestOutputDir(), $"{name}.fits");
        WriteCase(name, path);

        // BITPIX 64 and -64: the reader opens them, but the conversion takes neither, so the entry falls
        // back to the HDU reader, which refuses them as it always has (64 by throwing from BasicHDU.BitPix,
        // which the entry's quarantine catch turns into false).
        var readerTakesIt = !name.StartsWith("b64", StringComparison.Ordinal) && !name.StartsWith("b-64", StringComparison.Ordinal);
        var viaReader = Image.TryReadThroughFitsReader(path, out var image, out var wcs, pooled);
        viaReader.ShouldBe(readerTakesIt, $"{name}: whether FitsReader took the file");
        Image.TryReadFitsFile(path, out var viaEntry, out _).ShouldBe(readerTakesIt, $"{name}: the public entry");
        if (image is null)
        {
            return;
        }

        bool viaHdu;
        Image? read;
        WCS? referenceWcs;
        using (var fits = Image.OpenFits(path))
        {
            viaHdu = Image.TryReadFitsFile(fits, out read, out referenceWcs, pooled);
        }

        viaHdu.ShouldBeTrue($"{name}: the HDU reader reads it too");
        var reference = read.ShouldNotBeNull();

        try
        {
            ShouldBeTheSame(image, reference, name);
            ShouldBeTheSame(wcs, referenceWcs, name);
            Image.TryReadFitsHeader(path, out var info).ShouldBeTrue(name);
            info.Meta.ShouldBe(image.ImageMeta, $"{name}: the header-only read describes the same HDU");
            ShouldBeTheSame(viaEntry.ShouldNotBeNull(), reference, $"{name} through the public entry");
        }
        finally
        {
            image.Release();
            reference.Release();
        }
    }

    [Fact]
    public void TheImageBehindFitsLibsPlaceholderPrimaryIsReadByEveryPath()
    {
        // A table cannot be primary, so FITS.Lib writes a placeholder in front of one (BasicHDU.DummyHDU,
        // the image of an empty array: NAXIS = 1, NAXIS1 = 0). It IS an image HDU, one holding no sample,
        // and the walk used to stop there: the pixel read then found no data and failed, while the
        // header-only read described the placeholder rather than the image.
        var path = Path.Combine(SharedTestData.CreateTempTestOutputDir(), "placeholder-table-image.fits");
        WritePlaceholderTableImage(path);

        Image.TryReadFitsFile(path, out var image).ShouldBeTrue("the pixel read reaches the image extension");
        image.Shape.ShouldBe((1, Width, Height));
        image.ImageMeta.ObjectName.ShouldBe("NGC 7000");

        using (var fits = Image.OpenFits(path))
        {
            Image.TryReadFitsFile(fits, out var viaHdu).ShouldBeTrue("the HDU reader's walk skips the placeholder too");
            viaHdu.ImageMeta.ShouldBe(image.ImageMeta);
        }

        Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
        info.Meta.ShouldBe(image.ImageMeta, "the header-only read describes the same HDU");
    }

    /// <summary>A binary table added first, then a 16-bit image: FITS.Lib writes the placeholder primary,
    /// the table, then the image as an IMAGE extension.</summary>
    internal static void WritePlaceholderTableImage(string path)
    {
        var image = new short[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                image[y, x] = (short)((y * 300) + x - 5000);
            }
        }

        var fits = new Fits();
        fits.AddHDU(FitsFactory.HDUFactory(new object[] { new[] { 1, 2, 3 }, new[] { 1.5, 2.5, 3.5 } }));
        var imageHdu = FitsFactory.HDUFactory(image);
        imageHdu.Header.AddValue("BZERO", 32768.0, "");
        imageHdu.Header.AddValue("OBJECT", "NGC 7000", "");
        fits.AddHDU(imageHdu);
        using var output = File.Create(path);
        fits.Write(output);
    }

    private static readonly WCS Wcs = new(CenterRA: 5.5881, CenterDec: -5.3911)
    {
        CRPix1 = 18,
        CRPix2 = 11,
        CD1_1 = -3.1e-4,
        CD1_2 = 1.2e-6,
        CD2_1 = 1.1e-6,
        CD2_2 = 3.1e-4,
    };

    // Payload NaNs, the signed zero, infinities and subnormals: what a scale of one and an offset of
    // zero would change, and what adopting FITS.Lib's own float array did not.
    private static readonly float[] Specials =
    [
        -0.0f, float.NaN, BitConverter.Int32BitsToSingle(0x7FC00001), float.PositiveInfinity, float.NegativeInfinity,
        float.Epsilon, -float.Epsilon, float.MaxValue, float.MinValue, 1.5f, 0f, 65535f,
    ];

    private static void WriteCase(string name, string path)
    {
        switch (name)
        {
            case "tianwen_int8":
                TianWenImage(BitDepth.Int8, 1, (c, y, x) => ((y * 7) + x) % 256).WriteToFitsFile(path);
                return;
            case "tianwen_int16_wcs":
                TianWenImage(BitDepth.Int16, 1, (c, y, x) => ((y * 131) + (x * 17)) % 65536).WriteToFitsFile(path, Wcs);
                return;
            case "tianwen_int32":
                TianWenImage(BitDepth.Int32, 1, (c, y, x) => (y * 100_003) + (x * 77)).WriteToFitsFile(path);
                return;
            case "tianwen_float_rgb":
                TianWenImage(BitDepth.Float32, 3, (c, y, x) => (c * 0.25f) + (y * 0.01f) - (x * 0.003f)).WriteToFitsFile(path);
                return;
            case "float_specials":
                WriteSynthetic(path, -32, bzero: null, bscale: null, planes: 1, floats: i => Specials[i % Specials.Length]);
                return;
            case "empty_primary_then_image":
                WriteEmptyPrimaryThenImage(path);
                return;
            case "placeholder_table_image":
                WritePlaceholderTableImage(path);
                return;
        }

        // b{bitpix}_{scaling}_{planes}p
        var parts = name.Split('_');
        var bitpix = int.Parse(parts[0].AsSpan(1));
        var (bzero, bscale) = parts[1] switch
        {
            "none" => ((double?)null, (double?)null),
            "unsigned" => (32768.0, 1.0),
            _ => (-3.25, 0.5),
        };
        WriteSynthetic(path, bitpix, bzero, bscale, planes: parts[2][0] - '0');
    }

    /// <summary>A file as other software writes one: no DATAMIN or DATAMAX, so both paths recompute the
    /// range, and BZERO and BSCALE only when asked for.</summary>
    private static void WriteSynthetic(string path, int bitpix, double? bzero, double? bscale, int planes, Func<int, float>? floats = null)
    {
        var header = planes == 1 ? FitsWriter.ImageHeader(bitpix, Width, Height) : FitsWriter.ImageHeader(bitpix, Width, Height, planes);
        if (bzero is { } zero)
        {
            header.AddValue("BZERO", zero, "");
        }

        if (bscale is { } scale)
        {
            header.AddValue("BSCALE", scale, "");
        }

        header.AddValue("OBJECT", "M 42", "");
        header.AddValue("EXPTIME", 120.0, "seconds");
        var n = Width * Height * planes;
        using var writer = FitsWriter.CreateFile(path, header);
        switch (bitpix)
        {
            case 8: writer.Write<byte>(Samples(n, i => (byte)(i * 7))); break;
            case 16: writer.Write<short>(Samples(n, i => (short)((i * 1301) - 20000))); break;
            case 32: writer.Write<int>(Samples(n, i => (i * 1_000_003) - 7_000_000)); break;
            case 64: writer.Write<long>(Samples(n, i => ((long)i << 36) - i)); break;
            case -32: writer.Write<float>(Samples(n, floats ?? (i => (i * 0.37f) - 100f))); break;
            case -64: writer.Write<double>(Samples(n, i => (i * 1e-3) - 5)); break;
        }

        writer.Finish();
    }

    /// <summary>The fpack-style layout: a primary with NAXIS = 0, then the image as an IMAGE extension.</summary>
    private static void WriteEmptyPrimaryThenImage(string path)
    {
        using var output = File.Create(path);
        WriteHeader(output, Card("SIMPLE", "T"), Card("BITPIX", "8"), Card("NAXIS", "0"), Card("EXTEND", "T"));
        WriteHeader(output, Card("XTENSION", "'IMAGE   '"), Card("BITPIX", "16"), Card("NAXIS", "2"),
            Card("NAXIS1", $"{Width}"), Card("NAXIS2", $"{Height}"), Card("PCOUNT", "0"), Card("GCOUNT", "1"),
            Card("BZERO", "32768"), Card("OBJECT", "'M 42    '"));
        var data = new byte[Width * Height * 2];
        for (var i = 0; i < Width * Height; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(i * 2), (short)((i * 977) - 15000));
        }

        output.Write(data);
        output.Write(new byte[(2880 - (data.Length % 2880)) % 2880]);
    }

    private static string Card(string key, string value)
        => value.StartsWith('\'') ? $"{key,-8}= {value}" : $"{key,-8}= {value,20}";

    private static void WriteHeader(Stream output, params string[] cards)
    {
        var text = new StringBuilder();
        foreach (var card in cards)
        {
            text.Append(card.PadRight(80));
        }

        text.Append("END".PadRight(80));
        while (text.Length % 2880 != 0)
        {
            text.Append(' ');
        }

        output.Write(Encoding.ASCII.GetBytes(text.ToString()));
    }

    private static Image TianWenImage(BitDepth depth, int planes, Func<int, int, int, float> value)
    {
        var data = new float[planes][,];
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var c = 0; c < planes; c++)
        {
            data[c] = new float[Height, Width];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var v = value(c, y, x);
                    data[c][y, x] = v;
                    min = MathF.Min(min, v);
                    max = MathF.Max(max, v);
                }
            }
        }

        return new Image(data, depth, max, min, 0f, new ImageMeta(
            "Parity Camera", new DateTimeOffset(2026, 9, 25, 11, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(120),
            FrameType.Light, "SV 545 f4.5", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
            float.NaN, planes == 3 ? SensorType.Color : SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN));
    }

    private static T[] Samples<T>(int count, Func<int, T> value)
    {
        var samples = new T[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = value(i);
        }

        return samples;
    }

    private static void ShouldBeTheSame(Image actual, Image expected, string name)
    {
        actual.Shape.ShouldBe(expected.Shape, name);
        actual.BitDepth.ShouldBe(expected.BitDepth, name);
        Bits(actual.MaxValue).ShouldBe(Bits(expected.MaxValue), $"{name}: MaxValue {actual.MaxValue} against {expected.MaxValue}");
        Bits(actual.MinValue).ShouldBe(Bits(expected.MinValue), $"{name}: MinValue {actual.MinValue} against {expected.MinValue}");
        Bits(actual.Pedestal).ShouldBe(Bits(expected.Pedestal), $"{name}: Pedestal");
        actual.ImageMeta.ShouldBe(expected.ImageMeta, name);
        for (var c = 0; c < expected.ChannelCount; c++)
        {
            var got = MemoryMarshal.Cast<float, int>(actual.GetChannelSpan(c));
            var want = MemoryMarshal.Cast<float, int>(expected.GetChannelSpan(c));
            var at = FirstDifference(got, want);
            at.ShouldBe(-1, at < 0 ? name
                : $"{name}: channel {c} first differs at sample {at}: {actual.GetChannelSpan(c)[at]} (0x{got[at]:X8}) against {expected.GetChannelSpan(c)[at]} (0x{want[at]:X8})");
        }
    }

    private static void ShouldBeTheSame(WCS? actual, WCS? expected, string name)
    {
        actual.HasValue.ShouldBe(expected.HasValue, $"{name}: a WCS");
        if (actual is not { } a || expected is not { } e)
        {
            return;
        }

        // A record's SIP arrays compare by reference; everything else by value.
        (a with { SipA = null, SipB = null }).ShouldBe(e with { SipA = null, SipB = null }, name);
        SameSip(a.SipA, e.SipA, name);
        SameSip(a.SipB, e.SipB, name);
    }

    private static void SameSip(double[,]? actual, double[,]? expected, string name)
    {
        (actual is null).ShouldBe(expected is null, $"{name}: SIP");
        if (actual is not null && expected is not null)
        {
            MemoryMarshal.CreateReadOnlySpan(ref actual[0, 0], actual.Length).SequenceEqual(
                MemoryMarshal.CreateReadOnlySpan(ref expected[0, 0], expected.Length)).ShouldBeTrue($"{name}: SIP");
        }
    }

    private static int FirstDifference(ReadOnlySpan<int> actual, ReadOnlySpan<int> expected)
    {
        if (actual.Length != expected.Length)
        {
            return Math.Min(actual.Length, expected.Length);
        }

        for (var i = 0; i < actual.Length; i++)
        {
            if (actual[i] != expected[i])
            {
                return i;
            }
        }

        return -1;
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
}
