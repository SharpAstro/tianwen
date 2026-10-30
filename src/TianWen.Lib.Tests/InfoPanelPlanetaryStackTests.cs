using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The info strip on a SER's Auto and Live views says what the planetary pipeline knows (#1874): the planet, filter and telescope
/// the Auto run read and where, the frames it kept, and the frames the rolling stack holds, beside the master's own statistics.
/// </summary>
[Collection("UI")]
public class InfoPanelPlanetaryStackTests
{
    private static Image Master(int width = 32, int height = 16)
    {
        var plane = new float[height, width];
        plane[height / 2, width / 2] = 0.5f;
        return Image.FromChannel(plane, 1f, 0f);
    }

    private static PlanetaryIdentity Mono(int? apertureMm = 254) =>
        new(CatalogIndex.Jupiter, "the file name", "Red", 650d, "the file name", apertureMm, OpticalDesign.Newtonian,
            "the capture's header", "ASI224MC", PlanetaryFrameLayout.Mono);

    private static ViewerState AutoState(PlanetaryIdentity identity, (int, int)? frames = null)
        => new() { ShowAuto = true, AutoIdentity = identity, AutoFrames = frames };

    private sealed class Stub(int? folded, PreviewCapture? capture) : ILiveMasterSource
    {
        public int FrameCount => 1;
        public int? FramesFolded => folded;
        public PreviewCapture? Capture => capture;
        public bool HasTimestamps => false;
        public DateTimeOffset? TimestampOf(int index) => null;
        public Task<Image> MasterAtAsync(int playhead, CancellationToken cancellationToken) => Task.FromResult(Master());
        public (int Width, int Height, int Channels, SensorType Sensor) ExpectedGeometry => (32, 16, 1, SensorType.Monochrome);
        public void Dispose()
        {
        }
    }

    private static async Task<LiveStackPreviewSource> ShownAsync(ILiveMasterSource masters, CancellationToken ct)
    {
        var source = new LiveStackPreviewSource(masters, "x.ser", new SystemTimeProvider());
        while (source.DisplayMaster is null)
        {
            source.TryPublishMaster();
            source.RequestFollowLatest();
            await Task.Delay(10, ct);
        }
        return source;
    }

    [Fact(Timeout = 30_000)]
    public async Task InfoPanelAutoLinesSayWhatTheRunReadWhereAndHowManyFramesItKept()
    {
        await using var source = await ShownAsync(new Stub(null, null), TestContext.Current.CancellationToken);

        var lines = InfoPanelData.GetMetadataLines(source, AutoState(Mono(), (12, 40)));

        lines.ShouldContain("Planet: Jupiter (the file name)");
        lines.ShouldContain("Filter: Red at 650 nm (the file name)");
        lines.ShouldContain("Telescope: 254 mm Newtonian (the capture's header)");
        lines.ShouldContain("Kept: 12 of 40");
    }

    [Fact(Timeout = 30_000)]
    public async Task InfoPanelAutoLinesSuppressWhatIsNotKnown()
    {
        await using var source = await ShownAsync(new Stub(null, null), TestContext.Current.CancellationToken);
        var colourNoTelescope = new PlanetaryIdentity(null, "the frames say nothing", null, null, "colour", null, OpticalDesign.Unknown,
            "nothing names one", null, PlanetaryFrameLayout.Rgb);

        var lines = InfoPanelData.GetMetadataLines(source, AutoState(colourNoTelescope));

        lines.ShouldContain("Planet: none (the frames say nothing)");
        lines.ShouldNotContain(line => line.StartsWith("Filter", StringComparison.Ordinal), "a colour capture reads each channel at its own wavelength");
        lines.ShouldNotContain(line => line.StartsWith("Telescope", StringComparison.Ordinal));
        lines.ShouldNotContain(line => line.StartsWith("Kept", StringComparison.Ordinal), "no run result yet");
        lines.ShouldNotContain(line => line.StartsWith("Folded", StringComparison.Ordinal));
    }

    [Fact(Timeout = 30_000)]
    public async Task InfoPanelAutoStripKeepsTheCapturesTelescopeWhenTheIdentityNamesNone()
    {
        var capture = new PreviewCapture("ASI224MC", "C11", "", "Mono", 8, 3000, null, null, null);
        await using var source = await ShownAsync(new Stub(null, capture), TestContext.Current.CancellationToken);
        var noTelescope = new PlanetaryIdentity(null, "the frames say nothing", null, null, "colour", null, OpticalDesign.Unknown,
            "nothing names one", null, PlanetaryFrameLayout.Rgb);

        var lines = InfoPanelData.GetMetadataLines(source, AutoState(noTelescope));

        lines.Count(line => line == "Telescope: C11").ShouldBe(1);
    }

    [Fact(Timeout = 30_000)]
    public async Task InfoPanelNeitherAutoLinesNorStaleOnesShowOutsideTheAutoView()
    {
        await using var source = await ShownAsync(new Stub(null, null), TestContext.Current.CancellationToken);
        var state = AutoState(Mono(), (12, 40));
        state.ShowAuto = false;

        InfoPanelData.GetMetadataLines(source, state).ShouldNotContain(line => line.StartsWith("Planet", StringComparison.Ordinal)
            || line.StartsWith("Kept", StringComparison.Ordinal));
    }

    [Fact(Timeout = 30_000)]
    public async Task InfoPanelLiveViewShowsTheCaptureTheFramesFoldedAndTheMastersStatistics()
    {
        var capture = new PreviewCapture("ASI224MC", "C11", "", "Mono", 8, 3000, null, null, null);
        await using var source = await ShownAsync(new Stub(7, capture), TestContext.Current.CancellationToken);

        var lines = InfoPanelData.GetMetadataLines(source, new ViewerState { ShowStacked = true });
        var (header, rows) = InfoPanelData.GetStatisticsTable(source);

        lines.ShouldContain("Camera: ASI224MC");
        lines.ShouldContain("Telescope: C11");
        lines.ShouldContain("Frames: 3000");
        lines.ShouldContain("Folded: 7");
        header.ShouldContain("mean");
        rows.ShouldNotBeEmpty("the master's own statistics, through its document");
    }

    [Theory(Timeout = 30_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task InfoPanelAutoStripWrapsLongValuesInsideANarrowPanel(float dpi)
    {
        await using var source = await ShownAsync(new Stub(null, null), TestContext.Current.CancellationToken);
        var identity = Mono() with
        {
            PlanetFrom = "the capture's file name, which carries the planet, the filter and the time of the first frame",
            TelescopeFrom = "the viewer's last telescope, since the capture's header names none and none was set on it",
        };
        using var renderer = new RgbaImageRenderer((uint)(1600 * dpi), (uint)(1000 * dpi));
        var viewer = new ViewerE2E.Surface(renderer, new SignalBus()) { DpiScale = dpi, FontPath = OrreryTests.AppFace };
        var state = AutoState(identity, (12, 40));
        state.InfoPanelStatisticsPinned = true;

        viewer.Render(source, state);
        var strip = InfoPanelStripWidthTests.PaintedStrip(viewer);

        strip.Texts.Select(InfoPanelStripWidthTests.Text).ShouldContain("Planet:");
        strip.ShouldFitInside("long values wrap inside the strip rather than run past it");
        var planet = strip.LineAfter("Planet:");
        planet.Select(InfoPanelStripWidthTests.Text).ShouldContain("Jupiter");
        planet.Select(a => a.Bounds.Y).Distinct().Count().ShouldBeGreaterThan(1, "the long source wraps onto more than one painted row");
    }
}
