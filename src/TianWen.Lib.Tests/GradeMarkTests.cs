using System;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using TianWen.UI.Abstractions;
using Xunit;
using TestRenderer = TianWen.UI.Abstractions.ImageRendererBase<DIR.Lib.RgbaImage>;

namespace TianWen.Lib.Tests;

/// <summary>
/// The Grade section's thumbs and the file list's marks (#1951): thumbs down renames a frame with N.I.N.A.'s <c>BAD_</c>
/// prefix, which the stacker reads, thumbs up takes it off, and the list, the grade and the open document follow the new
/// name; a row's marks say the rejection and the causes its verdict held it down for.
/// </summary>
[Collection("UI")]
public sealed class GradeMarkTests : IDisposable
{
    private readonly TempFolders _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AMarkedNameAddsOrTakesOffThePrefixOnce()
    {
        CaptureRejection.MarkedPath(Path.Combine("d", "light_1.fits"), rejected: true).ShouldBe(Path.Combine("d", "BAD_light_1.fits"));
        CaptureRejection.MarkedPath(Path.Combine("d", "BAD_light_1.fits"), rejected: true).ShouldBe(Path.Combine("d", "BAD_light_1.fits"));
        CaptureRejection.MarkedPath(Path.Combine("d", "BAD_light_1.fits"), rejected: false).ShouldBe(Path.Combine("d", "light_1.fits"));
        CaptureRejection.MarkedPath(Path.Combine("d", "light_1.fits"), rejected: false).ShouldBe(Path.Combine("d", "light_1.fits"));
    }

    [Fact]
    public async Task ThumbsDownRenamesTheFrameAndEverythingFollowsAndThumbsUpUndoesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _temp.Create("marks").FullName;
        var path = Path.Combine(folder, "light_0089.fits");
        await File.WriteAllBytesAsync(path, [1, 2, 3], ct);
        var document = await DocumentAt(path);
        var state = new ViewerState { CurrentFolder = folder, ImageFileNames = ["light_0088.fits", "light_0089.fits"] };
        state.RecordFrameGrade(path, new LightGrade(LightGradeKey.Of(document.UnstretchedImage), new FrameMetrics(2f, 2.2f, 0.1f, 100), 4f));

        ViewerActions.MarkFrame(state, document, rejected: true).ShouldStartWith("Rejected: renamed to BAD_light_0089.fits");

        var rejected = Path.Combine(folder, "BAD_light_0089.fits");
        File.Exists(rejected).ShouldBeTrue();
        File.Exists(path).ShouldBeFalse();
        CaptureRejection.IsRejectedAtCapture(document.FilePath).ShouldBeTrue("the open document follows its file");
        state.ImageFileNames[1].ShouldBe("BAD_light_0089.fits", "the row keeps its place under its new name");
        state.GradeOf("BAD_light_0089.fits").ShouldNotBeNull("the grade follows the name");
        state.GradeOf("light_0089.fits").ShouldBeNull();

        ViewerActions.MarkFrame(state, document, rejected: true).ShouldEndWith("is already rejected");

        ViewerActions.MarkFrame(state, document, rejected: false).ShouldStartWith("Kept: renamed to light_0089.fits");
        File.Exists(path).ShouldBeTrue();
        state.ImageFileNames[1].ShouldBe("light_0089.fits");
    }

    [Fact]
    public async Task ARenameOntoAnExistingNameIsRefusedAndMovesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _temp.Create("clash").FullName;
        var path = Path.Combine(folder, "light_1.fits");
        await File.WriteAllBytesAsync(path, [1], ct);
        await File.WriteAllBytesAsync(Path.Combine(folder, "BAD_light_1.fits"), [2], ct);
        var document = await DocumentAt(path);
        var state = new ViewerState { CurrentFolder = folder, ImageFileNames = ["light_1.fits"] };

        ViewerActions.MarkFrame(state, document, rejected: true).ShouldStartWith("Could not reject light_1.fits");

        document.FilePath.ShouldBe(path);
        state.ImageFileNames[0].ShouldBe("light_1.fits");
        File.ReadAllBytes(Path.Combine(folder, "BAD_light_1.fits")).ShouldBe(new byte[] { 2 });
    }

    [Fact]
    public void ARowsMarksAreItsRejectionThenItsCauses()
    {
        var clear = new LightGradeVerdict(1f, 1f, GradeCause.None, 1f);
        var cloudyDawn = new LightGradeVerdict(1f, 9f, GradeCause.Cloud | GradeCause.Sunrise, 0.5f);

        TestRenderer.MarksFor("light_1.fits", clear).ShouldBeEmpty();
        TestRenderer.MarksFor("light_1.fits", null).ShouldBeEmpty("an ungraded frame has no causes yet");
        TestRenderer.MarksFor("BAD_light_1.fits", null).ShouldBe([TestRenderer.GradeMark.Rejected]);
        TestRenderer.MarksFor("BAD_light_1.fits", cloudyDawn).ShouldBe([TestRenderer.GradeMark.Rejected, TestRenderer.GradeMark.Cloud, TestRenderer.GradeMark.Twilight]);
        TestRenderer.TooltipOf(TestRenderer.GradeMark.Twilight, cloudyDawn).ShouldStartWith("Sunrise");
        TestRenderer.TooltipOf(TestRenderer.GradeMark.Cloud, cloudyDawn).ShouldBe("Cloud: the sky is 9.0x as patchy as the group's clearest");
    }

    private static async Task<AstroImageDocument> DocumentAt(string path)
    {
        var image = new Image([new float[8, 8]], BitDepth.Int16, 65535f, 0f, 0f, new ImageMeta());
        return await AstroImageDocument.AdoptImageAsync(image, DebayerAlgorithm.None, null, filePath: path,
            cancellationToken: TestContext.Current.CancellationToken);
    }
}
