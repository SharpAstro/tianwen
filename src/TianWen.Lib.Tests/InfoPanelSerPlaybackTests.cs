using System;
using System.IO;
using System.Threading.Tasks;
using SharpAstro.Ser;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A SER's statistics follow the shown frame only once it settles (#1874): a playing capture's frame never does, so playback must
/// still let the render loop idle between frames and measure nothing, and a pause brings the table to the shown frame.
/// </summary>
[Collection("Viewer")]
public class InfoPanelSerPlaybackTests
{
    [Fact(Timeout = 120_000)]
    public async Task APlayingSerIdlesBetweenFramesAndItsInfoPanelTableCatchesUpOncePaused()
    {
        await using var e2e = ViewerE2E.Start(1f);
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(e2e.Folder, "playback.ser");
        const int n = 16;
        var start = new DateTimeOffset(2026, 10, 7, 21, 14, 3, TimeSpan.Zero);
        using (var writer = new SerWriter(path, n, n, SerColorId.Mono, 8, "", "", ""))
        {
            for (var i = 0; i < 6; i++)
            {
                var frame = new byte[n * n];
                Array.Fill(frame, (byte)10);
                frame[0] = (byte)(100 + (20 * i));
                writer.AppendFrame(frame, start.AddSeconds(i));
            }
        }
        e2e.Host.HandleDropFile(path);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == path, "the capture to open", ct);
        var ser = e2e.Controller.Source.ShouldBeOfType<SerPreviewSource>();
        e2e.State.IsPlaying.ShouldBeTrue("a SER plays on open");
        // The slowest pace the player allows, a second a frame, so the ticks just after a publish all fall between two frames.
        e2e.State.PlaybackFps = 1f;

        // Play until a frame past the first is published, then tick again at once: nothing is due, so the loop may idle.
        var published = false;
        while (!published)
        {
            published = e2e.Controller.TickPlayback() && e2e.State.FrameIndex > 0;
            if (!published)
            {
                await Task.Delay(5, ct);
            }
        }
        for (var i = 0; i < 5; i++)
        {
            e2e.Controller.TickPlayback().ShouldBeFalse("steady playback between frames lets the loop idle");
            ser.IsMeasuringReadout.ShouldBeFalse("a playing frame is not measured");
        }

        // Paused, the shown frame settles: its table is measured, and once it is on show the loop idles again.
        e2e.State.IsPlaying = false;
        var shown = ser.FrameIndex;
        while (ser.StatisticsFrame != shown)
        {
            e2e.Controller.TickPlayback();
            await Task.Delay(5, ct);
        }
        ser.ReadoutPending.ShouldBeFalse();
        e2e.Controller.TickPlayback().ShouldBeFalse("the paused table is the shown frame's, so nothing is left to wake for");
        InfoPanelData.GetStatisticsHeading(ser, open: true).ShouldBe($"Statistics (frame {shown + 1}) [-]");
        InfoPanelData.GetStatisticsTable(ser).Rows.ShouldHaveSingleItem()[^1].ShouldBe($"{100 + (20 * shown)}");
    }
}
