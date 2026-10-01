using System;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The viewer reads the PICTURE from its source, whatever kind of source it is, and only the file viewer's own
/// features from a document (step 1 of P1, docs/plans/live-session-preview.md). A live frame, a SER and the
/// planetary stack are sources that are not documents, and the Live Session preview and the planetary view show
/// nothing else.
/// </summary>
[Collection("UI")]
public class ViewerReadsTheSourceTests
{
    // Wide enough for the whole bar on one row, so every button is painted (and so registered) or not purely
    // by whether it is enabled.
    private const uint SurfaceW = 2400;
    private const uint SurfaceH = 700;

    private const float FullScale = 4000f;

    private static Image Frame(int channels, int w = 40, int h = 30)
    {
        var planes = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var plane = new float[h, w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    // A gradient with a different level per channel, so a channel mixed up is a wrong value.
                    plane[y, x] = 100f * (c + 1) + x + (y * w);
                }
            }
            planes[c] = plane;
        }

        var meta = new ImageMeta("synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2),
            FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
            float.NaN, channels >= 3 ? SensorType.Color : SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        return new Image(planes, BitDepth.Float32, maxValue: FullScale, minValue: 0f, pedestal: 0f, imageMeta: meta);
    }

    private static LiveFramePreviewSource LiveSource(int channels)
    {
        var source = new LiveFramePreviewSource();
        source.AcceptFrame(Frame(channels), freezeStats: false).ShouldBeTrue();
        return source;
    }

    private static ViewerState NewState() => new ViewerState
    {
        ShowFileList = false,
        ShowInfoPanel = false,
        ShowHistogram = false,
    };

    /// <summary>
    /// The buttons that only need a picture work on any source. They asked for a document, so the planetary
    /// view, whose source is never one, painted six of its eight buttons dim, and a dim button registers no
    /// press: STF, Link, Params, Channel, Tone and Fit did nothing there.
    /// </summary>
    [Fact]
    public void A_source_that_is_not_a_document_is_stretched_zoomed_and_toned_from_the_toolbar()
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var viewer = new ViewerE2E.Surface(renderer, new SignalBus());
        var state = NewState();

        viewer.Render(LiveSource(channels: 3), state);

        foreach (var action in new[]
        {
            ToolbarAction.StretchToggle, ToolbarAction.StretchLink, ToolbarAction.StretchParams,
            ToolbarAction.Channel, ToolbarAction.Tone, ToolbarAction.Zoom,
        })
        {
            viewer.TryGetPaintedToolbarRect(action, out _).ShouldBeTrue($"{action} needs a picture, and a live frame is one");
        }

        foreach (var action in new[] { ToolbarAction.Save, ToolbarAction.PlateSolve, ToolbarAction.AutoCrop })
        {
            viewer.TryGetPaintedToolbarRect(action, out _).ShouldBeFalse($"{action} is the file viewer's, and stays with a document");
        }
    }

    /// <summary>
    /// The pointer over a live frame reads its pixel, every channel of it, as it does over a file. Only a
    /// document could answer before, so the planetary view and the preview had no readout at all.
    /// </summary>
    [Fact]
    public void The_pointer_over_a_live_frame_reads_its_pixel()
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var viewer = new ViewerE2E.Surface(renderer, new SignalBus());
        var state = NewState();
        viewer.Render(LiveSource(channels: 3), state);

        var area = viewer.ImageArea;
        viewer.HandleInput(new InputEvent.MouseMove(area.X + (area.Width / 2f), area.Y + (area.Height / 2f)));

        var info = state.CursorPixelInfo.ShouldNotBeNull("a live frame is a picture the pointer can read");
        info.Values.Length.ShouldBe(3, "a composite view reads every channel");
        for (var c = 0; c < 3; c++)
        {
            var expected = (100f * (c + 1) + info.X + (info.Y * 40)) / FullScale;
            info.Values[c].ShouldBe(expected, 1e-6f, $"channel {c} at ({info.X}, {info.Y}), on the [0, 1] scale the display uses");
        }
        info.RA.ShouldBeNull("nothing has placed the frame on the sky");
    }

    /// <summary>The single channel on screen is the one read, as for a file.</summary>
    [Fact]
    public void A_single_channel_view_reads_only_that_channel_of_a_live_frame()
    {
        var source = LiveSource(channels: 3);

        var info = ViewerActions.ReadPixel(source, wcs: null, x: 5, y: 7, channel: 2);

        info.Values.Length.ShouldBe(1, "only the channel on screen");
        info.Values[0].ShouldBe((300f + 5 + (7 * 40)) / FullScale, 1e-6f);
    }

    /// <summary>
    /// The metadata lines and the statistics table read any source, so the preview's readouts (P3) and the
    /// viewer's are one set of numbers.
    /// </summary>
    [Fact]
    public void The_metadata_and_the_statistics_table_read_a_live_frame()
    {
        var source = LiveSource(channels: 3);

        var lines = InfoPanelData.GetMetadataLines(source);
        lines.ShouldContain("Size: 40 x 30 x 3ch");
        lines.ShouldContain("Exposure: 2.0s");
        lines.ShouldContain("Camera: synth");

        var (header, rows) = InfoPanelData.GetStatisticsTable(source);
        header.ToArray().ShouldBe(new[] { "", "mean", "med", "MAD", "bg" });
        rows.Select(row => row[0]).ToArray().ShouldBe(new[] { "R", "G", "B", "Luma" });
    }

    /// <summary>
    /// The same frame, live and opened from disk, reads the same pixel and the same background: both divide by
    /// the sensor's full scale. The live preview divided by the frame's brightest pixel, so its readout and its
    /// statistics were relative to that pixel and disagreed with the file it saved.
    /// </summary>
    [Fact]
    public async Task A_live_frame_and_the_same_frame_as_a_document_read_the_same_numbers()
    {
        const float sensorFullScale = 16383f;
        // The gradient runs from 100 at (0, 0) to 100 + 39 + 29 * 40 = 1299, and the frame says so, as a camera's does.
        const float floor = 100f;
        const float peak = 1299f;
        Image Declared()
        {
            var frame = Frame(channels: 1);
            return new Image([frame.GetChannelArray(0)], BitDepth.Int16, maxValue: peak, minValue: floor,
                pedestal: 0f, imageMeta: frame.ImageMeta with { SensorFullScaleAdu = sensorFullScale });
        }

        var live = new LiveFramePreviewSource();
        live.AcceptFrame(Declared(), freezeStats: false).ShouldBeTrue();
        var document = await AstroImageDocument.AdoptImageAsync(Declared(), cancellationToken: TestContext.Current.CancellationToken);

        var fromLive = ViewerActions.ReadPixel(live, wcs: null, x: 5, y: 7).Values.ShouldHaveSingleItem();
        var fromFile = ViewerActions.ReadPixel(document, wcs: null, x: 5, y: 7).Values.ShouldHaveSingleItem();

        fromLive.ShouldBe((100f + 5 + (7 * 40)) / sensorFullScale, 1e-6f, "on the sensor's scale, not the frame's brightest pixel");
        fromLive.ShouldBe(fromFile, 1e-6f);

        var liveMedian = live.ChannelStatistics[0].Median.ShouldNotBeNull();
        var fileMedian = ((IPreviewSource)document).ChannelStatistics[0].Median.ShouldNotBeNull();
        liveMedian.ShouldBe(fileMedian, 1e-5, "the statistics table's numbers agree");
        live.PerChannelBackground[0].ShouldBe(floor / sensorFullScale, 1e-6f,
            "the stretch's pedestal is the frame's floor on the same scale as its pixels");
    }

    /// <summary>A frame nothing describes still reports its size, and nothing it would have to make up.</summary>
    [Fact]
    public void A_source_with_no_header_reports_only_its_size()
    {
        var lines = InfoPanelData.GetMetadataLines(new HeaderlessSource());

        lines.ToArray().ShouldBe(new[] { "Size: 4 x 3 x 1ch" });
    }

    /// <summary>Two arcseconds a pixel about the frame's centre: a CD matrix is all the overlays ask for.</summary>
    private static WCS Solved()
    {
        const double scaleDeg = 2.0 / 3600.0;
        return new WCS(5.0, -25.0)
        {
            CRPix1 = 20.0,
            CRPix2 = 15.0,
            CD1_1 = -scaleDeg,
            CD1_2 = 0.0,
            CD2_1 = 0.0,
            CD2_2 = -scaleDeg,
        };
    }

    private static StarList OneStar() => new StarList(
    [
        new ImagedStar(HFD: 4f, StarFWHM: 3f, SNR: 50f, Flux: 1000f, XCentroid: 20f, YCentroid: 15f, Ellipticity: 0f),
    ]);

    /// <summary>
    /// A live frame a solve has placed gets what a placed file gets: its grid, the object overlays and its
    /// stars, from the source's findings. A host used to hand the viewer a WCS through an override a still
    /// image ignored, and a live frame's stars could not reach it at all.
    /// </summary>
    [Fact]
    public void A_live_frame_a_solve_placed_gets_the_grid_the_overlays_and_its_stars()
    {
        using var renderer = new RgbaImageRenderer(SurfaceW, SurfaceH);
        var viewer = new ViewerE2E.Surface(renderer, new SignalBus());
        var state = NewState();
        state.ShowGrid = true;
        var source = LiveSource(channels: 1);
        source.Findings = FrameFindings.Placed(Solved()).WithStars(OneStar());

        viewer.Render(source, state);

        viewer.PreparedGridWcs.ShouldNotBeNull("the frame's own WCS draws its grid");
        viewer.TryGetPaintedToolbarRect(ToolbarAction.Overlays, out _).ShouldBeTrue("a placed frame can show what is on it");
        viewer.TryGetPaintedToolbarRect(ToolbarAction.Stars, out _).ShouldBeTrue("a frame with stars can show them");
        source.Findings.MedianHfd.ShouldBe(4f);
        source.Findings.MedianFwhm.ShouldBe(3f);
    }

    /// <summary>
    /// A new frame drops the previous frame's findings: a solve describes the exposure it came from, and a grid
    /// over the next one would be drawn where that frame is not.
    /// </summary>
    [Fact]
    public void A_new_live_frame_drops_the_previous_frames_findings()
    {
        var source = LiveSource(channels: 1);
        source.Findings = FrameFindings.Placed(Solved());

        source.AcceptFrame(Frame(channels: 1), freezeStats: false).ShouldBeTrue();

        source.Findings.ShouldBeSameAs(FrameFindings.None);
    }

    /// <summary>
    /// A document's stars and its solve are one record, and replacing the stars keeps the solve: the two run on
    /// tasks of their own, and either may land first.
    /// </summary>
    [Fact]
    public async Task A_documents_stars_and_solve_are_one_record_and_neither_replaces_the_other()
    {
        var document = await AstroImageDocument.AdoptImageAsync(Frame(channels: 1), wcs: Solved(),
            cancellationToken: TestContext.Current.CancellationToken);
        document.Findings.Wcs.ShouldNotBeNull("the WCS it was opened with");

        document.RecordStars(OneStar());

        document.Findings.Wcs.ShouldNotBeNull("recording the stars kept the solve");
        document.Findings.Stars.ShouldNotBeNull().Count.ShouldBe(1);
        ((IPreviewSource)document).Findings.ShouldBeSameAs(document.Findings, "the viewer reads the document's own record");
    }

    private sealed class HeaderlessSource : IPreviewSource
    {
        public int Width => 4;
        public int Height => 3;
        public int ChannelCount => 1;
        public SensorType SensorType => SensorType.Monochrome;
        public int BayerOffsetX => 0;
        public int BayerOffsetY => 0;
        public int FrameCount => 1;
        public int FrameIndex => 0;
        public float[] PerChannelBackground => [0f];
        public float LumaBackground => 0f;
        public ImageHistogram[] ChannelStatistics => [];
        public ReadOnlySpan<float> GetChannelData(int channel) => new float[12];
        public bool SelectFrame(int index) => false;
        public bool HasTimestamps => false;
        public DateTimeOffset TimestampOf(int index) => DateTimeOffset.MinValue;

        public StretchUniforms ComputeStretchUniforms(
            StretchMode mode, StretchParameters parameters,
            LumaWeighting weighting = LumaWeighting.Rec709, float lumaBlend = 1f, bool normalize = false,
            int curvesMode = 0, ReadOnlySpan<float> curveLut = default, float curvesBoost = 0f,
            float curvesMidpoint = 0.25f, float hdrAmount = 0f, float hdrKnee = 0.8f,
            float bgNeutralizationStrength = 1f, (float R, float G, float B)? manualWhiteBalance = null,
            bool applyColorCalibration = true)
            => new StretchUniforms(mode, 1f, default, default, default, default, default);
    }
}
