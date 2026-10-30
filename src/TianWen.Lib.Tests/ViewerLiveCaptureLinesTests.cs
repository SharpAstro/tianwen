using System;
using System.IO;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Ser;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A SER's capture lines stay on the info strip when the viewer switches from Raw to Live (#1874). Through the controller, so
/// the live source it opens is the real one, not a stub that already names the telescope.
/// </summary>
[Collection("Viewer")]
public class ViewerLiveCaptureLinesTests
{
    [Fact(Timeout = 120_000)]
    public async Task TheLiveViewKeepsTheTelescopeObserverColourDepthAndRateOfTheSerHeader()
    {
        await using var e2e = ViewerE2E.Start(1f);
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(e2e.Folder, "2024-12-15-1256_7-Jupiter.ser");
        var start = new DateTimeOffset(2024, 12, 15, 12, 56, 44, TimeSpan.Zero);
        const int n = 32;
        using (var writer = new SerWriter(path, n, n, SerColorId.BayerRGGB, 8, "Seb", "ASI224MC", "C11"))
        {
            var frame = new byte[n * n];
            Array.Fill(frame, (byte)40);
            for (var i = 0; i < 8; i++)
            {
                writer.AppendFrame(frame, start.AddMilliseconds(100 * i));
            }
        }
        e2e.Host.HandleDropFile(path);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == path, "the capture to open", ct);
        e2e.State.IsPlaying = false;
        e2e.Frame();

        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewLive" }, "the Live view"));
        await e2e.PumpUntilAsync(() => e2e.Controller.ViewLayers.Live is { HasMaster: true, IsBusy: false }, "the live stack's first master", ct);

        var lines = InfoPanelData.GetMetadataLines(e2e.Controller.Source.ShouldNotBeNull(), e2e.State);

        lines.ShouldContain("Telescope: C11");
        lines.ShouldContain("Observer: Seb");
        lines.ShouldContain("Colour: RGGB Bayer");
        lines.ShouldContain("Depth: 8-bit");
        lines.ShouldContain(line => line.StartsWith("Rate:", StringComparison.Ordinal));
    }
}
