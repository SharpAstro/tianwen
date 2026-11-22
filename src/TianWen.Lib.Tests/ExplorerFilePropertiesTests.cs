using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The Explorer detail columns (#2429), read from a stream the way the shell hands one over: every field of the
/// issue's table from the right card or SER field, an absent card leaving its field empty, and the pixel block
/// never read.
/// </summary>
public class ExplorerFilePropertiesTests
{
    [Fact]
    public void AFitsFillsEveryColumnFromItsOwnCard()
    {
        using var stream = Fits(32, 24, 16,
            ("INSTRUME", "'ZWO ASI533MC Pro'"), ("TELESCOP", "'Sky-Watcher Esprit 100'"),
            ("EXPTIME", "300.0"), ("FOCALLEN", "550.0"), ("APTDIA", "100"), ("ISOSPEED", "800"),
            ("GAIN", "121"), ("DATE-OBS", "'2026-11-01T21:30:15'"), ("OBJECT", "'M 31'"),
            ("FILTER", "'Ha'"), ("IMAGETYP", "'Light'"), ("OBSERVER", "'Ada Lovelace'"),
            ("SWCREATE", "'N.I.N.A. 3.1'"));

        var p = ExplorerFileProperties.Read(stream);

        p.CameraModel.ShouldBe("ZWO ASI533MC Pro");
        p.CameraMaker.ShouldBe("ZWO");
        p.LensModel.ShouldBe("Sky-Watcher Esprit 100");
        p.LensMaker.ShouldBe("Sky-Watcher");
        p.ExposureSeconds.ShouldBe(300.0);
        p.FocalLengthMm.ShouldBe(550.0);
        p.FNumber.ShouldBe(5.5);
        p.IsoSpeed.ShouldBe((ushort)800);
        p.DateTaken.ShouldBe(new DateTimeOffset(2026, 11, 1, 21, 30, 15, TimeSpan.Zero));
        p.Subject.ShouldBe("M 31");
        p.Tags.ShouldBe(["Ha", "Light"]);
        p.Author.ShouldBe("Ada Lovelace");
        p.ProgramName.ShouldBe("N.I.N.A. 3.1");
        p.Width.ShouldBe(32u);
        p.Height.ShouldBe(24u);
        p.BitDepth.ShouldBe(16u);
        p.Duration.ShouldBeNull();
        p.FrameRate.ShouldBeNull();
    }

    [Fact]
    public void AMissingCardLeavesItsFieldEmptyAndTheGainIsNeverTheIso()
    {
        using var stream = Fits(32, 24, 16, ("GAIN", "121"), ("FOCALLEN", "550.0"), ("INSTRUME", "'Mystery Cam'"), ("TELESCOP", "'Home made'"));

        var p = ExplorerFileProperties.Read(stream);

        p.IsoSpeed.ShouldBeNull();
        p.ExposureSeconds.ShouldBeNull();
        p.DateTaken.ShouldBeNull();
        p.Subject.ShouldBeNull();
        p.Author.ShouldBeNull();
        p.ProgramName.ShouldBeNull();
        p.Tags.ShouldBeEmpty();
        p.FNumber.ShouldBeNull("FOCALLEN without APTDIA gives no F-stop");
        p.FocalLengthMm.ShouldBe(550.0);
        p.CameraModel.ShouldBe("Mystery Cam");
        p.CameraMaker.ShouldBeNull("an unknown brand has no maker");
        p.LensMaker.ShouldBeNull();
        p.Width.ShouldBe(32u);
    }

    [Fact]
    public void AnApertureWithoutAFocalLengthGivesNoFStop()
    {
        using var stream = Fits(32, 24, 16, ("APTDIA", "100"));

        var p = ExplorerFileProperties.Read(stream);

        p.FNumber.ShouldBeNull();
        p.FocalLengthMm.ShouldBeNull();
    }

    [Theory]
    [InlineData("qhy268m", "QHYCCD")]
    [InlineData("Canon EOS 6D", "Canon")]
    [InlineData("Player One Ares-C", "Player One")]
    [InlineData("Starlight Xpress Trius", "Starlight Xpress")]
    [InlineData("My ZWO", null)]
    [InlineData("", null)]
    public void TheCameraMakerIsDerivedFromAKnownBrandAtTheStart(string instrument, string? maker)
        => ExplorerFileProperties.CameraMakerOf(instrument).ShouldBe(maker);

    [Theory]
    [InlineData("Celestron EdgeHD 8", "Celestron")]
    [InlineData("TAKAHASHI FSQ-106", "Takahashi")]
    [InlineData("Samyang 135", null)]
    public void TheLensMakerIsDerivedFromAKnownBrandAtTheStart(string telescope, string? maker)
        => ExplorerFileProperties.OpticsMakerOf(telescope).ShouldBe(maker);

    [Fact]
    public void ATileCompressedFzReadsItsZAxesFromTheImageHdu()
    {
        using var source = SharedTestData.OpenEmbeddedFileStream("tilecompressed.fz")
            ?? throw new InvalidOperationException("Missing test data tilecompressed.fz");
        var buffered = new MemoryStream();
        source.CopyTo(buffered);
        buffered.Position = 0;

        var p = ExplorerFileProperties.Read(buffered);

        // 3 x 24 x 32 float, per FpackReadoutTileTests.
        p.Width.ShouldBe(32u);
        p.Height.ShouldBe(24u);
        p.BitDepth.ShouldBe(32u);
    }

    [Fact]
    public void ASerFillsItsColumnsAndTheTrailerGivesLengthAndFrameRate()
    {
        var start = new DateTime(2026, 11, 2, 3, 4, 5, DateTimeKind.Utc);
        using var stream = Ser(frames: 11, withTrailer: true, start, frameInterval: TimeSpan.FromMilliseconds(100));

        var p = ExplorerFileProperties.Read(stream);

        p.CameraModel.ShouldBe("ZWO ASI662MC");
        p.CameraMaker.ShouldBe("ZWO");
        p.LensModel.ShouldBe("Celestron C14");
        p.LensMaker.ShouldBe("Celestron");
        p.Author.ShouldBe("Grace Hopper");
        p.DateTaken.ShouldBe(new DateTimeOffset(start));
        p.Width.ShouldBe(16u);
        p.Height.ShouldBe(12u);
        p.BitDepth.ShouldBe(16u);
        p.Duration.ShouldBe(TimeSpan.FromSeconds(1));
        p.FrameRate!.Value.ShouldBe(10.0, 1e-9);
        p.ExposureSeconds.ShouldBeNull();
    }

    [Fact]
    public void ASerWithoutATrailerHasNoLengthOrFrameRate()
    {
        using var stream = Ser(frames: 5, withTrailer: false, new DateTime(2026, 11, 2, 3, 4, 5, DateTimeKind.Utc), TimeSpan.Zero);

        var p = ExplorerFileProperties.Read(stream);

        p.Width.ShouldBe(16u);
        p.Duration.ShouldBeNull();
        p.FrameRate.ShouldBeNull();
    }

    [Fact]
    public void AFitsDataBlockIsSkippedNotRead()
    {
        // 3000 x 3000 x 2 bytes of pixels, 18 MB.
        using var counting = new CountingStream(Fits(3000, 3000, 16, ("EXPTIME", "10")));
        var size = counting.Length;

        var p = ExplorerFileProperties.Read(counting);

        p.Width.ShouldBe(3000u);
        p.ExposureSeconds.ShouldBe(10.0);
        counting.BytesRead.ShouldBeLessThan(size / 20, $"read {counting.BytesRead} of {size}");
    }

    [Fact]
    public void ASerFrameBlockIsSkippedAndOnlyTheHeaderAndTheTrailerAreRead()
    {
        var width = 1000;
        var height = 1000;
        using var counting = new CountingStream(Ser(frames: 20, withTrailer: true,
            new DateTime(2026, 11, 2, 3, 4, 5, DateTimeKind.Utc), TimeSpan.FromMilliseconds(50), width, height));
        var size = counting.Length;

        var p = ExplorerFileProperties.Read(counting);

        p.FrameRate.ShouldNotBeNull();
        // Header 178, the magic sniff, and two 8-byte trailer entries: nowhere near one 2 MB frame.
        counting.BytesRead.ShouldBeLessThan(1024, $"read {counting.BytesRead} of {size}");
    }

    [Fact]
    public void GarbageAndTruncationNeverThrow()
    {
        var noise = new byte[4096];
        new Random(7).NextBytes(noise);
        ExplorerFileProperties.Read(new MemoryStream(noise)).ShouldBe(ExplorerFileProperties.Empty);
        ExplorerFileProperties.Read(new MemoryStream([])).ShouldBe(ExplorerFileProperties.Empty);

        var whole = ((MemoryStream)Fits(32, 24, 16, ("OBJECT", "'M 31'"))).ToArray();
        foreach (var cut in new[] { 10, 80, 1000, 2880, 2900 })
        {
            Should.NotThrow(() => ExplorerFileProperties.Read(new MemoryStream(whole, 0, cut)));
        }

        var serWhole = ((MemoryStream)Ser(3, true, DateTime.UtcNow, TimeSpan.FromMilliseconds(10))).ToArray();
        foreach (var cut in new[] { 14, 100, SerHeader.Size, SerHeader.Size + 5, serWhole.Length - 3 })
        {
            Should.NotThrow(() => ExplorerFileProperties.Read(new MemoryStream(serWhole, 0, cut)));
        }

        Should.NotThrow(() => ExplorerFileProperties.Read(new NonSeekableStream()));
    }

    /// <summary>A FITS held in memory: a primary HDU of BITPIX <paramref name="bitpix"/> with zero pixels, as long as the header says.</summary>
    private static Stream Fits(int width, int height, int bitpix, params (string Key, string Value)[] cards)
    {
        var all = new List<(string, string)>
        {
            ("SIMPLE", "T"), ("BITPIX", bitpix.ToString()), ("NAXIS", "2"), ("NAXIS1", width.ToString()), ("NAXIS2", height.ToString()),
        };
        all.AddRange(cards);

        var header = new StringBuilder();
        foreach (var (key, value) in all)
        {
            header.Append($"{key,-8}= {value}".PadRight(80));
        }

        header.Append("END".PadRight(80));
        var headerBytes = Encoding.ASCII.GetBytes(header.ToString().PadRight((header.Length + 2879) / 2880 * 2880));
        var dataBytes = (long)width * height * (Math.Abs(bitpix) / 8);
        var padded = (dataBytes + 2879) / 2880 * 2880;

        var stream = new MemoryStream(checked((int)(headerBytes.Length + padded)));
        stream.Write(headerBytes);
        stream.SetLength(headerBytes.Length + padded);
        stream.Position = 0;
        return stream;
    }

    private static Stream Ser(int frames, bool withTrailer, DateTime startUtc, TimeSpan frameInterval, int width = 16, int height = 12)
    {
        var header = SerHeader.Create(SerColorId.Mono, width, height, 16, frameCount: frames,
            observer: "Grace Hopper", instrument: "ZWO ASI662MC", telescope: "Celestron C14",
            dateTimeTicks: startUtc.Ticks, dateTimeUtcTicks: startUtc.Ticks);
        var frameBytes = (long)header.FrameSizeBytes;
        var length = SerHeader.Size + (frames * frameBytes) + (withTrailer ? frames * 8L : 0);
        var bytes = new byte[length];
        header.Write(bytes.AsSpan(0, SerHeader.Size));
        if (withTrailer)
        {
            var trailer = SerHeader.Size + (frames * frameBytes);
            for (var i = 0; i < frames; i++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan((int)(trailer + (i * 8L)), 8), startUtc.Ticks + (i * frameInterval.Ticks));
            }
        }

        return new MemoryStream(bytes);
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            BytesRead += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class NonSeekableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
