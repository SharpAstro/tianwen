using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A SER opens linear, and STF turns on the planetary stretch, never the deep-sky auto-stretch the frame before it left (#1440,
/// reported 2026-10-10), through the host <c>tianwen-fits</c> runs (<see cref="ViewerE2E"/>), the buttons pressed where they are
/// painted. While the stretch is off the mode button names the stretch STF would turn on; it used to read "Unlinked", a mode in
/// effect nowhere.
/// </summary>
[Collection("Viewer")]
public class ViewerSerStretchTests
{
    [Theory(Timeout = 60_000)]
    [MemberData(nameof(ViewerE2ETests.Scales), MemberType = typeof(ViewerE2ETests))]
    public async Task ASerOpensLinearAndStfTurnsOnThePlanetaryStretch(float dpi)
    {
        await using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        // A deep-sky frame first, in the deep-sky stretch, which is the one STF brought back on the SER after it.
        await e2e.OpenAsync(e2e.WriteColourFits("frame.fits"), ct);
        e2e.State.StretchMode.ShouldBe(ViewerActions.DefaultStretchMode);

        var capture = ViewerBestStackTests.WriteCapture(Path.Combine(e2e.Folder, "2024-12-15-1256_7-Jupiter.ser"));
        e2e.Host.HandleDropFile(capture);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == capture, "the capture to open", ct);

        e2e.State.StretchMode.ShouldBe(StretchMode.None, "a SER's frames show linear");
        e2e.Viewer.PaintedToolbarLabel(ToolbarAction.StretchLink).ShouldBe("Planetary", "the mode button names the stretch STF turns on");

        e2e.Click(ToolbarAction.StretchToggle);
        e2e.State.StretchMode.ShouldBe(StretchMode.Planetary);
        e2e.Viewer.PaintedToolbarLabel(ToolbarAction.StretchLink).ShouldBe("Planetary");

        e2e.Click(ToolbarAction.StretchToggle);
        e2e.State.StretchMode.ShouldBe(StretchMode.None);
        e2e.Viewer.PaintedToolbarLabel(ToolbarAction.StretchLink).ShouldBe("Planetary", "off again, it still names what STF turns on");

        // A deep-sky frame after the SER is in the deep-sky stretch again, and so is what STF turns on there.
        await e2e.OpenAsync(e2e.WriteColourFits("after.fits", level: 100f), ct);
        e2e.State.StretchMode.ShouldBe(ViewerActions.DefaultStretchMode);
        e2e.State.StretchModeBeforeLinear.ShouldBe(ViewerActions.DefaultStretchMode);
    }
}
