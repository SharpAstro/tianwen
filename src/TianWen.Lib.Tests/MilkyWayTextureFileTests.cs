using System;
using System.Buffers.Binary;
using System.IO;
using SharpAstro.Lzip;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="MilkyWayTextureFile"/> reads what <see cref="MilkyWayTextureBaker.WriteRaw"/> writes, refuses
/// a header its pixels do not fill, and hands the web build premultiplied RGB in the right channel order.
/// </summary>
/// <remarks>
/// It is the one parser for both consumers: the desktop sky map uploads its BGRA, and
/// <c>tools/bake-milkyway</c> turns it into the browser's PNG. A channel swapped here paints the web sky
/// blue where the desktop's is red, and nothing else would notice.
/// </remarks>
public class MilkyWayTextureFileTests
{
    private static byte[] Compressed(int width, int height, ReadOnlySpan<byte> bgra)
    {
        var dir = Directory.CreateTempSubdirectory("MilkyWayTextureFileTests_");
        try
        {
            var path = Path.Combine(dir.FullName, "milkyway.bgra");
            MilkyWayTextureBaker.WriteRaw(path, width, height, bgra);
            return LzipEncoder.Compress(File.ReadAllBytes(path));
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void DecodeReadsWhatTheBakerWrites()
    {
        const int width = 3;
        const int height = 2;
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i++)
        {
            bgra[i] = (byte)(i * 7 + 1);
        }

        var decoded = MilkyWayTextureFile.Decode(Compressed(width, height, bgra));

        decoded.Width.ShouldBe(width);
        decoded.Height.ShouldBe(height);
        decoded.Bgra.ToArray().ShouldBe(bgra);
    }

    [Fact]
    public void PremultipliedRgbIsInRedGreenBlueOrderWithAlphaFoldedIn()
    {
        // Pixel 0 half bright, pixel 1 fully bright, pixel 2 dark. BGRA on disk.
        byte[] bgra =
        [
            10, 20, 200, 128,
            30, 60, 90, 255,
            255, 255, 255, 0,
        ];

        var rgb = MilkyWayTextureFile.Decode(Compressed(3, 1, bgra)).ToPremultipliedRgb();

        // round(200 * 128 / 255) = 100, round(20 * 128 / 255) = 10, round(10 * 128 / 255) = 5
        rgb.ShouldBe(new byte[]
        {
            100, 10, 5,
            90, 60, 30,
            0, 0, 0,
        });
    }

    [Fact]
    public void AHeaderThePixelsDoNotFillIsRefused()
    {
        var header = new byte[MilkyWayTextureFile.HeaderBytes + 4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 2);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 2);

        Should.Throw<InvalidDataException>(() => MilkyWayTextureFile.Decode(LzipEncoder.Compress(header)))
            .Message.ShouldContain("2x2");
        Should.Throw<InvalidDataException>(() => MilkyWayTextureFile.Decode(LzipEncoder.Compress(new byte[5])));
    }
}
