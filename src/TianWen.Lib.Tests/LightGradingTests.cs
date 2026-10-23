using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The blink's grading: each frame scored as the stacker scores a reference candidate, relative to the best of ITS group
/// seen so far, and shown as a tint on its file-list row and in the info strip's Grade section.
/// </summary>
[Collection("UI")]
public class LightGradingTests
{
    private static Image Frame(Filter filter, double exposureSeconds = 300, string objectName = "")
        => new Image([new float[4, 4]], BitDepth.Int16, 65535f, 0f, 0f,
            new ImageMeta("FLI", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(exposureSeconds), FrameType.Light, "",
                9f, 9f, 530, FocuserPositions.None, filter, 1, 1, -25f, SensorType.Monochrome, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN) { ObjectName = objectName });

    private static LightGrade Graded(Filter filter, float score, double exposureSeconds = 300)
        => new(LightGradeKey.Of(Frame(filter, exposureSeconds)), new FrameMetrics(2f, 2.2f, 0.1f, 1000), score);

    private static ImmutableDictionary<string, LightGrade> Grades(params (string Path, LightGrade Grade)[] entries)
        => entries.ToImmutableDictionary(e => e.Path, e => e.Grade, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AGradeIsTheScoreAsAShareOfItsGroupsBest()
    {
        var best = Graded(Filter.Luminance, 10f);
        var half = Graded(Filter.Luminance, 5f);
        var grades = Grades(("a", best), ("b", half));

        LightGrading.Relative(grades, best).ShouldBe(1f);
        LightGrading.Relative(grades, half).ShouldBe(0.5f);
    }

    /// <summary>
    /// A blue sub of a field shows a fraction of the luminance sub's stars, so a set graded as one group would read every
    /// blue frame as poor: a group is one filter at one exposure.
    /// </summary>
    [Fact]
    public void FramesThroughAnotherFilterOrExposureAreGradedApart()
    {
        var luminance = Graded(Filter.Luminance, 10f);
        var blue = Graded(Filter.Blue, 2f);
        var shortLuminance = Graded(Filter.Luminance, 3f, exposureSeconds: 60);
        var grades = Grades(("l", luminance), ("b", blue), ("s", shortLuminance));

        LightGrading.Relative(grades, blue).ShouldBe(1f, "the best blue frame is its group's best");
        LightGrading.Relative(grades, shortLuminance).ShouldBe(1f, "a 60 s frame is not held to a 300 s one");
        blue.Key.Describe().ShouldBe("Blue, 300 s");
    }

    [Fact]
    public void AFrameWithNoStarsIsNotGraded()
        => LightGrading.Grade(Frame(Filter.Luminance), StarList.Empty).ShouldBeNull();

    [Fact]
    public void TheColourRunsFromErrorThroughWarnToSuccess()
    {
        var palette = GuiTheme.DarkPalette;

        LightGrading.Colour(palette, 0.2f).ShouldBe(palette.Error);
        LightGrading.Colour(palette, LightGrading.PoorBelow).ShouldBe(palette.Error);
        LightGrading.Colour(palette, (LightGrading.PoorBelow + LightGrading.GoodFrom) / 2f).ShouldBe(palette.Warn);
        LightGrading.Colour(palette, LightGrading.GoodFrom).ShouldBe(palette.Success);
        LightGrading.Colour(palette, 1f).ShouldBe(palette.Success);
    }

    [Fact]
    public void AFileListEntryFindsItsGradeThroughTheFolderOnShow()
    {
        var folder = Path.Combine(Path.GetTempPath(), "graded");
        var state = new ViewerState { CurrentFolder = folder };
        var grade = Graded(Filter.Red, 4f);

        state.RecordFrameGrade(Path.Combine(folder, "light_001.fits"), grade);

        state.GradeOf("LIGHT_001.FITS").ShouldBe(grade, "a path is matched as the file system matches it on Windows");
        state.GradeOf("light_002.fits").ShouldBeNull();
    }

    [Fact]
    public async Task TheGradeSectionSaysTheShareTheMetricsAndTheBest()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await ViewerInfoPanelCollapseTests.NewColourDocumentAsync(ct);
        var state = new ViewerState();

        InfoPanelData.GetGradeReadout(state, document)?.Relative.ShouldBeNull("not graded until its stars are in");

        var key = LightGradeKey.Of(document.UnstretchedImage);
        state.RecordFrameGrade(document.FilePath, new LightGrade(key, new FrameMetrics(2.5f, 2.8f, 0.12f, 800), 4f));
        state.RecordFrameGrade(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(document.FilePath))!, "best.fits"),
            new LightGrade(key, new FrameMetrics(2f, 2.2f, 0.1f, 1000), 8f));

        var readout = InfoPanelData.GetGradeReadout(state, document) ?? throw new InvalidOperationException("a file has a grade section");
        readout.Relative.ShouldBe(0.5f);
        readout.Lines[0].ShouldBe("Quality: 50% of the best of 2 (0 s)");
        readout.Lines[1].ShouldBe("Stars: 800, HFD 2.50, ecc 0.12");
        readout.BestName.ShouldBe("best.fits");
    }

    /// <summary>
    /// A graded row is tinted by its grade, an ungraded one keeps the list's ground, and the selected row keeps the
    /// selection colour, which says which frame is on show.
    /// </summary>
    [Fact]
    public void TheFileListTintsAGradedRowByItsGrade()
    {
        using var renderer = new RgbaImageRenderer(900, 700);
        var viewer = new ListViewer(renderer);
        viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, 400, 300);
        var folder = Path.Combine(Path.GetTempPath(), "graded-list");
        var state = new ViewerState
        {
            ShowFileList = true,
            ShowInfoPanel = false,
            ShowHistogram = false,
            StretchMode = StretchMode.None,
            CurrentFolder = folder,
            ImageFileNames = ["a.fits", "b.fits", "c.fits", "d.fits"],
            SelectedFileIndex = 0,
        };
        state.RecordFrameGrade(Path.Combine(folder, "a.fits"), Graded(Filter.Luminance, 10f));
        state.RecordFrameGrade(Path.Combine(folder, "b.fits"), Graded(Filter.Luminance, 10f));
        state.RecordFrameGrade(Path.Combine(folder, "c.fits"), Graded(Filter.Luminance, 3f));

        viewer.Render(null, state);

        var palette = ViewerTheme.Palette;
        RowColour(viewer, renderer, 0).ShouldBe(palette.Selection);
        RowColour(viewer, renderer, 1).ShouldBe(LightGrading.RowTint(palette, 1f));
        RowColour(viewer, renderer, 2).ShouldBe(LightGrading.RowTint(palette, 0.3f));
        RowColour(viewer, renderer, 3).ShouldNotBe(LightGrading.RowTint(palette, 1f), "an ungraded row is not tinted");
    }

    // A row's highlight colour, read just inside its left edge where the name is not drawn.
    private static RGBAColor32 RowColour(ListViewer viewer, RgbaImageRenderer renderer, int row)
    {
        var region = viewer.GetRegisteredRegions().First(r => r.Result is HitResult.ListItemHit { Index: var i } && i == row);
        var x = (int)(region.X + 3);
        var y = (int)(region.Y + region.Height / 2);
        var offset = ((y * renderer.Surface.Width) + x) * 4;
        var p = renderer.Surface.Pixels;
        return new RGBAColor32(p[offset], p[offset + 1], p[offset + 2], p[offset + 3]);
    }

    private sealed class ListViewer : ImageRendererBase<RgbaImage>
    {
        public ListViewer(RgbaImageRenderer renderer) : base(renderer)
        {
            Width = renderer.Width;
            Height = renderer.Height;
            DpiScale = 1f;
            FontPath = FontResolver.ResolveSystemFont();
        }

        protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
            in DisplayRendition rendition, WCS? wcs,
            float left, float top, float right, float bottom, uint projW, uint projH,
            RenditionSlot slot, bool sampleBeforeChannels) { }

        protected override void RenderHistogramQuad(StretchUniforms stretch, HistogramDisplay histogram,
            ViewerState state, float left, float top, float right, float bottom, uint projW, uint projH) { }

        protected override void DrawEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
            float rotationRad, RGBAColor32 color, float thickness) { }

        protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color) { }

        protected override void DrawLineOverlay(float x0, float y0, float x1, float y1,
            RGBAColor32 color, float thickness) { }

        protected override void OnResize(uint width, uint height) { }

        public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
            int width, int height) { }

        public override void UploadHistogramData(IPreviewSource source) { }

        protected override HistogramDisplay? GetHistogramDisplay() => null;
    }
}
