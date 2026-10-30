using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The live stack's built-at line (#1881) through the host <c>tianwen-fits</c> runs (<see cref="ViewerE2E"/>): on the Live view only, its
/// age kept current by one redraw a second at most, and wrapping inside the strip at any width.
/// </summary>
[Collection("Viewer")]
public class InfoPanelLiveStackViewerTests
{
    private static async Task<(ViewerE2E E2E, LiveStackPreviewSource Live)> OpenLiveAsync(float dpi, CancellationToken ct)
    {
        var e2e = ViewerE2E.Start(dpi);
        var capture = ViewerBestStackTests.WriteCapture(Path.Combine(e2e.Folder, "2024-12-15-1256_7-Jupiter.ser"));
        e2e.Host.HandleDropFile(capture);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == capture, "the capture to open", ct);

        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewLive" }, "the Live view button"));
        await e2e.PumpUntilAsync(() => e2e.Controller.ViewLayers.Live is { HasMaster: true, IsBusy: false } live
            && ReferenceEquals(e2e.Controller.Source, live) && e2e.State.LiveStackBuiltLine is not null, "the live master to publish", ct);
        return (e2e, e2e.Controller.ViewLayers.Live.ShouldNotBeNull());
    }

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(ViewerE2ETests.Scales), MemberType = typeof(ViewerE2ETests))]
    public async Task TheLiveViewNamesTheFrameItsPictureWasBuiltAtAndTheOtherViewsSayNothing(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (e2e, live) = await OpenLiveAsync(dpi, ct);
        await using (e2e)
        {
            e2e.State.LiveStackBuiltLine.ShouldNotBeNull().ShouldStartWith($"Live stack: built at frame {live.BuiltFrame + 1} (");
            var withLine = e2e.Viewer.MeasureInfoStrip(live, e2e.State, 400f).Height;
            var line = e2e.State.LiveStackBuiltLine;
            e2e.State.LiveStackBuiltLine = null;
            e2e.Viewer.MeasureInfoStrip(live, e2e.State, 400f).Height.ShouldBeLessThan(withLine, "the line is a section of the strip");
            e2e.State.LiveStackBuiltLine = line;

            // Raw says nothing.
            e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewFrames" }, "the Raw view button"));
            await e2e.PumpUntilAsync(() => e2e.State.PlanetaryView == PlanetaryView.Frames && e2e.State.LiveStackBuiltLine is null, "Raw to clear the line", ct);

            // The Auto view's fixed master says nothing, though its layer has a built master too.
            e2e.Key(InputKey.K, InputModifier.Shift);
            var auto = await ViewerBestStackTests.AutoOnShowAsync(e2e, ct);
            e2e.Frame();
            e2e.State.LiveStackBuiltLine.ShouldBeNull();
            auto.FollowsStream.ShouldBeFalse();
            // Even with a line left in the state, the strip shows it only for a source that follows a stream.
            var without = e2e.Viewer.MeasureInfoStrip(auto, e2e.State, 400f).Height;
            e2e.State.LiveStackBuiltLine = line;
            e2e.Viewer.MeasureInfoStrip(auto, e2e.State, 400f).Height.ShouldBe(without);
        }
    }

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(ViewerE2ETests.Scales), MemberType = typeof(ViewerE2ETests))]
    public async Task AFiveDigitFrameCountWrapsInsideTheStripNarrowAndWide(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (e2e, live) = await OpenLiveAsync(dpi, ct);
        await using (e2e)
        {
            e2e.State.LiveStackBuiltLine = InfoPanelData.LiveStackBuiltLine(12344, new DateTimeOffset(2026, 10, 31, 13, 20, 41, TimeSpan.Zero),
                54321, new DateTimeOffset(2026, 10, 31, 13, 21, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 31, 13, 20, 52, TimeSpan.Zero));
            e2e.State.LiveStackBuiltLine.ShouldNotBeNull().ShouldContain("frame 12345");

            var line = e2e.State.LiveStackBuiltLine;
            // The strip is 300 design units wide in the app; 160 is a narrow one. Every word and row is arranged inside the strip.
            var wideWidth = e2e.Viewer.ScaledInfoPanelWidth;
            var narrowWidth = 160f * dpi;
            e2e.Viewer.InfoStripRightEdge(live, e2e.State, narrowWidth).ShouldBeLessThanOrEqualTo(narrowWidth, "a narrow strip wraps the line, it never runs past its edge");
            e2e.Viewer.InfoStripRightEdge(live, e2e.State, wideWidth).ShouldBeLessThanOrEqualTo(wideWidth);

            // And it wraps onto more rows the narrower the strip is.
            var narrowHeight = e2e.Viewer.MeasureInfoStrip(live, e2e.State, narrowWidth).Height;
            var wideHeight = e2e.Viewer.MeasureInfoStrip(live, e2e.State, wideWidth).Height;
            e2e.State.LiveStackBuiltLine = null;
            var narrowBare = e2e.Viewer.MeasureInfoStrip(live, e2e.State, narrowWidth).Height;
            var wideBare = e2e.Viewer.MeasureInfoStrip(live, e2e.State, wideWidth).Height;
            (narrowHeight - narrowBare).ShouldBeGreaterThan(wideHeight - wideBare, "the narrow strip holds the line on more rows");
            (wideHeight - wideBare).ShouldBeGreaterThan(0f, "the line takes rows");
        }
    }

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(ViewerE2ETests.Scales), MemberType = typeof(ViewerE2ETests))]
    public async Task TheAgeAsksForOneRedrawWhenItsWordingChangesAndNotBefore(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var (e2e, _) = await OpenLiveAsync(dpi, ct);
        await using (e2e)
        {
            // Let playback and the follow settle, so only the clock can move the line.
            e2e.State.IsPlaying = false;
            var quiet = 0;
            for (var i = 0; quiet < 5; i++)
            {
                i.ShouldBeLessThan(1000, "playback to settle");
                quiet = e2e.Controller.TickPlayback() ? 0 : quiet + 1;
                e2e.Frame();
                await Task.Delay(10, ct);
            }

            var before = e2e.State.LiveStackBuiltLine.ShouldNotBeNull();
            e2e.Clock.Advance(TimeSpan.FromMilliseconds(400));
            e2e.Controller.TickPlayback().ShouldBeFalse("under a second later the wording is the same");

            e2e.Clock.Advance(TimeSpan.FromSeconds(2));
            e2e.Controller.TickPlayback().ShouldBeTrue("a whole second on, the age reads differently");
            e2e.State.LiveStackBuiltLine.ShouldNotBe(before);
            e2e.State.LiveStackBuiltLine.ShouldEndWith("updated 2 s ago");
            e2e.Controller.TickPlayback().ShouldBeFalse("one request, not one a frame");
        }
    }
}

public class InfoPanelLiveStackTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 13, 21, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Stamp = new(2026, 10, 31, 13, 20, 41, 0, TimeSpan.Zero);

    [Fact]
    public void ALineNamesTheFrameItsTimeHowFarBehindAndHowOld()
        => InfoPanelData.LiveStackBuiltLine(5399, Stamp, 5811, Now, Now - TimeSpan.FromSeconds(8))
            .ShouldBe("Live stack: built at frame 5400 (13:20:41.0 UT), 412 frames behind the playhead, updated 8 s ago");

    [Fact]
    public void AMasterAtThePlayheadSaysSo()
        => InfoPanelData.LiveStackBuiltLine(99, Stamp, 99, Now, Now)
            .ShouldBe("Live stack: built at frame 100 (13:20:41.0 UT), at the playhead, updated 0 s ago");

    [Fact]
    public void OneFrameBehindIsSingular()
        => InfoPanelData.LiveStackBuiltLine(9, null, 10, Now, null)
            .ShouldBe("Live stack: built at frame 10, 1 frame behind the playhead");

    [Fact]
    public void AMissingTimestampDropsOnlyItsPart()
        => InfoPanelData.LiveStackBuiltLine(12344, null, 12400, Now, Now - TimeSpan.FromSeconds(3))
            .ShouldBe("Live stack: built at frame 12345, 56 frames behind the playhead, updated 3 s ago");

    [Fact]
    public void NoMasterYetSaysNothing()
        => InfoPanelData.LiveStackBuiltLine(-1, Stamp, 10, Now, Now).ShouldBeNull();

    [Theory]
    [InlineData(59, "updated 59 s ago")]
    [InlineData(60, "updated 1 min ago")]
    [InlineData(185, "updated 3 min ago")]
    public void TheAgeRollsFromSecondsToMinutes(int seconds, string expected)
        => InfoPanelData.LiveStackBuiltLine(0, null, -1, Now, Now - TimeSpan.FromSeconds(seconds))
            .ShouldBe($"Live stack: built at frame 1, {expected}");
}
