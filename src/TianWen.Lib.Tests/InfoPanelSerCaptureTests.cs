using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A SER capture's info strip carries its header's facts and the statistics with a peak and a clip mark, as a FITS frame's does
/// (#1874). Through the real source, which caches them at open.
/// </summary>
[Collection("UI")]
public class InfoPanelSerCaptureTests : IDisposable
{
    private const int W = 8;
    private const int H = 6;
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 21, 14, 3, TimeSpan.Zero);

    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private string Write8BitMono(string observer, string instrument, string telescope, bool clip, bool timestamps = true)
    {
        var path = Path.Combine(_folders.Create("tianwen-infopanel-").FullName, "mono.ser");
        using (var writer = new SerWriter(path, W, H, SerColorId.Mono, 8, observer, instrument, telescope))
        {
            for (var i = 0; i < 3; i++)
            {
                var frame = new byte[W * H];
                Array.Fill(frame, (byte)10);
                frame[0] = clip ? (byte)255 : (byte)200;
                if (timestamps)
                {
                    writer.AppendFrame(frame, Start.AddSeconds(i * 0.5));
                }
                else
                {
                    writer.AppendFrame(frame);
                }
            }
        }
        return path;
    }

    private string Write16BitRggb(ushort redPeak = 40000)
    {
        var path = Path.Combine(_folders.Create("tianwen-infopanel-").FullName, "rggb.ser");
        using (var writer = new SerWriter(path, W, H, SerColorId.BayerRGGB, 16, "Seb", "ASI224MC", "C11"))
        {
            var samples = new ushort[W * H];
            Array.Fill(samples, (ushort)1000);
            samples[0] = redPeak; // red photosite (0, 0)
            var bytes = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            writer.AppendFrame(bytes, Start);
        }
        return path;
    }

    private static Task<SerPreviewSource> Open(string path)
        => SerPreviewSource.OpenAsync(path, new FakeTimeProviderWrapper(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task AnEightBitSerShowsItsCaptureFacts()
    {
        await using var source = await Open(Write8BitMono("Seb", "ASI120MM", "C8 f/10", clip: false));

        var lines = InfoPanelData.GetMetadataLines(source);

        lines.ShouldContain("Camera: ASI120MM");
        lines.ShouldContain("Telescope: C8 f/10");
        lines.ShouldContain("Observer: Seb");
        lines.ShouldContain("Colour: Mono");
        lines.ShouldContain("Depth: 8-bit");
        lines.ShouldContain("Frames: 3");
        lines.ShouldContain("Span: 2026-10-07 21:14:03 to 21:14:04 UTC");
        lines.ShouldContain("Rate: 2.0 fps");
    }

    private string WriteMonoStamped(params DateTimeOffset[] stamps)
    {
        var path = Path.Combine(_folders.Create("tianwen-infopanel-").FullName, "stamped.ser");
        using (var writer = new SerWriter(path, W, H, SerColorId.Mono, 8, "", "", ""))
        {
            foreach (var stamp in stamps)
            {
                var frame = new byte[W * H];
                Array.Fill(frame, (byte)10);
                writer.AppendFrame(frame, stamp);
            }
        }
        return path;
    }

    [Fact]
    public async Task ACaptureWrittenOutOfTimeOrderShowsItsEarliestToLatestSpanAndTheRateOverIt()
    {
        // Best frame first, as PIPP writes a quality-sorted capture: file order runs 04, 03, 03.5 (seconds past the start).
        await using var source = await Open(WriteMonoStamped(Start.AddSeconds(1), Start, Start.AddSeconds(0.5)));

        var lines = InfoPanelData.GetMetadataLines(source);

        lines.ShouldContain("Span: 2026-10-07 21:14:03 to 21:14:04 UTC");
        lines.ShouldContain("Rate: 2.0 fps");
    }

    [Fact]
    public async Task AnUnstampedLastFrameIsSkippedNotPrintedAsYearOne()
    {
        await using var source = await Open(WriteMonoStamped(Start, Start.AddSeconds(0.5), DateTimeOffset.MinValue));

        var lines = InfoPanelData.GetMetadataLines(source);

        lines.ShouldContain("Span: 2026-10-07 21:14:03 to 21:14:03 UTC");
        lines.ShouldContain("Rate: 2.0 fps");
        lines.ShouldNotContain(line => line.Contains("0001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABlankHeaderAndNoTimestampsSuppressTheirLinesInsteadOfPrintingSentinels()
    {
        await using var source = await Open(Write8BitMono("", "  ", "", clip: false, timestamps: false));

        var lines = InfoPanelData.GetMetadataLines(source);

        lines.ShouldNotContain(line => line.StartsWith("Camera", StringComparison.Ordinal));
        lines.ShouldNotContain(line => line.StartsWith("Telescope", StringComparison.Ordinal));
        lines.ShouldNotContain(line => line.StartsWith("Observer", StringComparison.Ordinal));
        lines.ShouldNotContain(line => line.StartsWith("Span", StringComparison.Ordinal));
        lines.ShouldNotContain(line => line.StartsWith("Rate", StringComparison.Ordinal));
        lines.ShouldContain("Frames: 3");
        lines.ShouldContain("Depth: 8-bit");
    }

    [Fact]
    public async Task ASixteenBitMosaicNamesItsBayerPatternAndDepth()
    {
        await using var source = await Open(Write16BitRggb());

        var lines = InfoPanelData.GetMetadataLines(source);

        lines.ShouldContain("Colour: RGGB Bayer");
        lines.ShouldContain("Depth: 16-bit");
        lines.ShouldContain("Camera: ASI224MC");
    }

    [Fact]
    public async Task AnEightBitCaptureThatReachesFullScaleMarksTheChannelClipping()
    {
        await using var source = await Open(Write8BitMono("", "", "", clip: true));

        var (header, rows) = InfoPanelData.GetStatisticsTable(source);

        header[^1].ShouldBe("peak");
        rows.ShouldHaveSingleItem()[^1].ShouldBe("255 clip");
    }

    [Fact]
    public async Task TheStatisticsHeadingNamesTheFrameTheTableDescribes()
    {
        await using var source = await Open(Write8BitMono("", "", "", clip: false));

        InfoPanelData.GetStatisticsHeading(source, open: true).ShouldBe("Statistics (frame 1) [-]");
        InfoPanelData.GetStatisticsHeading(source, open: false).ShouldBe("Statistics (frame 1) [+]");
    }

    private string WriteMonoPeaks(params byte[] peaks)
    {
        var path = Path.Combine(_folders.Create("tianwen-infopanel-").FullName, "peaks.ser");
        using (var writer = new SerWriter(path, W, H, SerColorId.Mono, 8, "", "", ""))
        {
            foreach (var peak in peaks)
            {
                var frame = new byte[W * H];
                Array.Fill(frame, (byte)10);
                frame[0] = peak;
                writer.AppendFrame(frame, Start);
            }
        }
        return path;
    }

    // Shows a frame the way playback does (decode off-thread, publish), then polls until its statistics have been measured.
    private static async Task ShowFrameAsync(SerPreviewSource source, int index, CancellationToken ct)
    {
        source.TryStartDecode(index).ShouldBeTrue();
        while (!source.IsDecodeReady)
        {
            await Task.Delay(1, ct);
        }
        source.TryPublishDecoded(out var shown).ShouldBeTrue();
        shown.ShouldBe(index);
        source.ReadoutPending.ShouldBeTrue("the shown frame's statistics are still to be measured");
        while (source.StatisticsFrame != index)
        {
            source.PollShownFrameReadout();
            await Task.Delay(1, ct);
        }
        source.ReadoutPending.ShouldBeFalse();
    }

    [Fact(Timeout = 30000)]
    public async Task TheStatisticsFollowTheShownFrameIncludingItsClipMark()
    {
        await using var source = await Open(WriteMonoPeaks(200, 255, 100));

        InfoPanelData.GetStatisticsTable(source).Rows.ShouldHaveSingleItem()[^1].ShouldBe("200");

        var ct = TestContext.Current.CancellationToken;
        await ShowFrameAsync(source, 1, ct);
        InfoPanelData.GetStatisticsTable(source).Rows.ShouldHaveSingleItem()[^1].ShouldBe("255 clip");
        InfoPanelData.GetStatisticsHeading(source, open: true).ShouldBe("Statistics (frame 2) [-]");

        await ShowFrameAsync(source, 2, ct);
        InfoPanelData.GetStatisticsTable(source).Rows.ShouldHaveSingleItem()[^1].ShouldBe("100", "the clip mark goes with the frame that clipped");
        InfoPanelData.GetStatisticsHeading(source, open: true).ShouldBe("Statistics (frame 3) [-]");
    }

    [Fact(Timeout = 30000)]
    public async Task TheLastTableStaysOnShowUntilTheNewFramesArrivesAndSaysWhichFrameItIs()
    {
        await using var source = await Open(WriteMonoPeaks(200, 255));
        source.TryStartDecode(1).ShouldBeTrue();
        while (!source.IsDecodeReady)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        source.TryPublishDecoded(out _).ShouldBeTrue();

        InfoPanelData.GetStatisticsHeading(source, open: true).ShouldBe("Statistics (frame 1) [-]");
        InfoPanelData.GetStatisticsTable(source).Rows.ShouldHaveSingleItem()[^1].ShouldBe("200");
    }

    [Fact]
    public async Task ABggrMosaicShowsTheBrightPhotositeColourInItsOwnRow()
    {
        var path = Path.Combine(_folders.Create("tianwen-infopanel-").FullName, "bggr.ser");
        using (var writer = new SerWriter(path, W, H, SerColorId.BayerBGGR, 16, "", "", ""))
        {
            var samples = new ushort[W * H];
            Array.Fill(samples, (ushort)1000);
            samples[0] = 40000; // photosite (0, 0) of a BGGR mosaic is BLUE
            var bytes = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            writer.AppendFrame(bytes, Start);
        }
        await using var source = await Open(path);

        var (_, rows) = InfoPanelData.GetStatisticsTable(source);

        rows.Select(row => row[0]).ToArray().ShouldBe(new[] { "R", "G", "B", "Luma" });
        rows[0][^1].ShouldBe("1000");
        rows[1][^1].ShouldBe("1000");
        rows[2][^1].ShouldBe("40000", "the blue photosite holds the bright sample");
    }

    [Fact]
    public async Task AnEightBitCaptureBelowFullScaleShowsItsPeakUnmarked()
    {
        await using var source = await Open(Write8BitMono("", "", "", clip: false));

        var (_, rows) = InfoPanelData.GetStatisticsTable(source);

        rows.ShouldHaveSingleItem()[^1].ShouldBe("200");
    }

    [Fact]
    public async Task ASixteenBitMosaicReadsItsPeakPerPhotositeColour()
    {
        await using var source = await Open(Write16BitRggb());

        var (header, rows) = InfoPanelData.GetStatisticsTable(source);

        header[^1].ShouldBe("peak");
        rows.Select(row => row[0]).ToArray().ShouldBe(new[] { "R", "G", "B", "Luma" });
        rows[0][^1].ShouldBe("40000", "the red photosite holds the bright sample");
        rows[1][^1].ShouldBe("1000");
        rows[2][^1].ShouldBe("1000");
        rows.Any(row => row[row.Length - 1].EndsWith("clip", StringComparison.Ordinal)).ShouldBeFalse("40000 of 65535 is not full scale");
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheStripOfASerHasMetadataAndStatisticsAndWrapsLongStringsInsideThePanel(float dpi)
    {
        var instrument = "ZWO ASI224MC colour planetary camera with a very long name";
        var telescope = "Celestron EdgeHD 11 inch with a 2x barlow and an IR pass filter";
        await using var source = await Open(Write8BitMono("An Observer With A Long Name", instrument, telescope, clip: false));
        using var renderer = new RgbaImageRenderer((uint)(1600 * dpi), (uint)(1000 * dpi));
        var viewer = new ViewerE2E.Surface(renderer, new SignalBus()) { DpiScale = dpi, FontPath = OrreryTests.AppFace };
        var state = new ViewerState { InfoPanelStatisticsPinned = true };

        viewer.Render(source, state);
        var strip = InfoPanelStripWidthTests.PaintedStrip(viewer);

        var texts = strip.Texts.Select(InfoPanelStripWidthTests.Text).ToArray();
        texts.ShouldContain("Metadata");
        texts.ShouldContain(text => text.StartsWith("Statistics", StringComparison.Ordinal));
        texts.ShouldContain("Colour:");
        strip.ShouldFitInside("long strings wrap inside the strip rather than run past it");
        strip.LineAfter("Telescope:").Select(a => a.Bounds.Y).Distinct().Count().ShouldBeGreaterThan(1, "the long telescope wraps onto more than one painted row");
    }

    [Fact]
    public async Task ASixteenBitCaptureAtFullScaleMarksItsRedRowClipping()
    {
        await using var source = await Open(Write16BitRggb(redPeak: 65535));

        var (_, rows) = InfoPanelData.GetStatisticsTable(source);

        rows[0][^1].ShouldBe("65535 clip", "65535 of a 16-bit capture is full scale, through the unit scale and back");
        rows[1][^1].ShouldBe("1000");
    }
}
