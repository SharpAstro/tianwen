using System;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging.Grading;
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
    public void AMarkedNameCarriesItsCauseAndGoodTakesItAllOff()
    {
        string In(string name) => Path.Combine("d", name);
        CaptureRejection.MarkedPath(In("light_1.fits"), CaptureRejection.Cause.Cloud).ShouldBe(In("BAD_CLOUD_light_1.fits"));
        CaptureRejection.MarkedPath(In("light_1.fits"), CaptureRejection.Cause.Unspecified).ShouldBe(In("BAD_light_1.fits"));
        CaptureRejection.MarkedPath(In("BAD_CLOUD_light_1.fits"), CaptureRejection.Cause.Streaky).ShouldBe(In("BAD_STREAKY_light_1.fits"),
            "a frame moves from one cause to another in one rename");
        CaptureRejection.MarkedPath(In("BAD_light_1.fits"), CaptureRejection.Cause.Defocused).ShouldBe(In("BAD_DEFOCUSED_light_1.fits"));
        CaptureRejection.MarkedPath(In("BAD_OBSTRUCTION_light_1.fits"), null).ShouldBe(In("light_1.fits"));
        CaptureRejection.MarkedPath(In("BAD_light_1.fits"), null).ShouldBe(In("light_1.fits"));
        CaptureRejection.MarkedPath(In("light_1.fits"), null).ShouldBe(In("light_1.fits"));
    }

    [Fact]
    public void ANamesCauseIsReadAfterThePrefixAndABarePrefixIsUnspecified()
    {
        CaptureRejection.CauseOf("light_1.fits").ShouldBeNull();
        CaptureRejection.CauseOf("BAD_light_1.fits").ShouldBe(CaptureRejection.Cause.Unspecified, "N.I.N.A.'s own prefix names no cause");
        CaptureRejection.CauseOf("bad_cloud_light_1.fits").ShouldBe(CaptureRejection.Cause.Cloud, "matched as the prefix is, ignoring case");
        CaptureRejection.CauseOf("BAD_STREAKY_light_1.fits").ShouldBe(CaptureRejection.Cause.Streaky);
        CaptureRejection.IsRejectedAtCapture("BAD_DEFOCUSED_light_1.fits").ShouldBeTrue("every reader of the prefix still reads it rejected");
    }

    [Fact]
    public void ACloudMarkFromTheNameSaysSoWhenTheGradeFoundNoCloud()
    {
        var missed = new LightGradeVerdict(1f, 1.0f, GradeCause.None, 1f);
        TestRenderer.TooltipOf(TestRenderer.GradeMark.Cloud, missed).ShouldBe("Rejected as cloud (BAD_CLOUD_)");
        TestRenderer.TooltipOf(TestRenderer.GradeMark.Cloud, null).ShouldBe("Rejected as cloud (BAD_CLOUD_)");
        var found = new LightGradeVerdict(0.5f, 3.0f, GradeCause.Cloud, 0.5f);
        TestRenderer.TooltipOf(TestRenderer.GradeMark.Cloud, found).ShouldStartWith("Cloud: the sky is 3.0x");
    }

    [Fact(Timeout = 30_000)]
    public async Task AThumbsPressRenamesOffTheDrawThreadAndASecondPressWhilePendingIsIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _temp.Create("marks-async").FullName;
        var path = Path.Combine(folder, "light_0001.fits");
        await File.WriteAllBytesAsync(path, [1, 2, 3], ct);
        var document = await DocumentAt(path);
        var state = new ViewerState { CurrentFolder = folder, ImageFileNames = ["light_0001.fits"] };

        ViewerActions.StartMarkFrame(state, document, CaptureRejection.Cause.Cloud);
        state.StatusMessage.ShouldBe("Renaming...");
        ViewerActions.StartMarkFrame(state, document, CaptureRejection.Cause.Streaky);
        while (!ViewerActions.TryCompleteMark(state))
        {
            await Task.Delay(5, ct);
        }
        state.StatusMessage.ShouldStartWith("Rejected as cloud: renamed to BAD_CLOUD_light_0001.fits");
        File.Exists(Path.Combine(folder, "BAD_CLOUD_light_0001.fits")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, "BAD_STREAKY_light_0001.fits")).ShouldBeFalse("the second press was ignored");
        state.PendingMark.ShouldBeNull();
    }

    [Fact(Timeout = 30_000)]
    public async Task AMarkThatFinishesAfterAnotherFolderWasOpenedLeavesThatFoldersListAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var folderA = _temp.Create("late-a").FullName;
        var folderB = _temp.Create("late-b").FullName;
        var path = Path.Combine(folderA, "light_0001.fits");
        await File.WriteAllBytesAsync(path, [1, 2, 3], ct);
        await File.WriteAllBytesAsync(Path.Combine(folderB, "light_0001.fits"), [4], ct);
        var document = await DocumentAt(path);
        var state = new ViewerState { CurrentFolder = folderA, ImageFileNames = ["light_0001.fits"] };

        ViewerActions.StartMarkFrame(state, document, CaptureRejection.Cause.Cloud);
        state.CurrentFolder = folderB;
        state.ImageFileNames = ["light_0001.fits"];
        while (!ViewerActions.TryCompleteMark(state))
        {
            await Task.Delay(5, ct);
        }

        File.Exists(Path.Combine(folderA, "BAD_CLOUD_light_0001.fits")).ShouldBeTrue();
        state.ImageFileNames[0].ShouldBe("light_0001.fits", "folder B's row is not A's file");
    }

    [Fact]
    public async Task ARejectRenamesTheFrameWithItsCauseAndEverythingFollowsAndGoodUndoesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _temp.Create("marks").FullName;
        var path = Path.Combine(folder, "light_0089.fits");
        await File.WriteAllBytesAsync(path, [1, 2, 3], ct);
        var document = await DocumentAt(path);
        var state = new ViewerState { CurrentFolder = folder, ImageFileNames = ["light_0088.fits", "light_0089.fits"] };
        state.RecordFrameGrade(path, new LightGrade(LightGradeKey.Of(document.UnstretchedImage), new FrameMetrics(2f, 2.2f, 0.1f, 100), 4f));

        ViewerActions.MarkFrame(state, document, CaptureRejection.Cause.Cloud).ShouldStartWith("Rejected as cloud: renamed to BAD_CLOUD_light_0089.fits");

        File.Exists(Path.Combine(folder, "BAD_CLOUD_light_0089.fits")).ShouldBeTrue();
        File.Exists(path).ShouldBeFalse();
        CaptureRejection.CauseOf(document.FilePath).ShouldBe(CaptureRejection.Cause.Cloud, "the open document follows its file");
        state.ImageFileNames[1].ShouldBe("BAD_CLOUD_light_0089.fits", "the row keeps its place under its new name");
        state.GradeOf("BAD_CLOUD_light_0089.fits").ShouldNotBeNull("the grade follows the name");

        ViewerActions.MarkFrame(state, document, CaptureRejection.Cause.Cloud).ShouldEndWith("is already rejected as cloud");
        ViewerActions.MarkFrame(state, document, CaptureRejection.Cause.Streaky).ShouldStartWith("Rejected as streaky stars: renamed to BAD_STREAKY_light_0089.fits");

        ViewerActions.MarkFrame(state, document, null).ShouldStartWith("Kept: renamed to light_0089.fits");
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

        ViewerActions.MarkFrame(state, document, CaptureRejection.Cause.Unspecified).ShouldStartWith("Could not reject light_1.fits");

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
        // The owner's own cause draws as its mark; a cloud the owner named and the grade found is drawn once.
        TestRenderer.MarksFor("BAD_STREAKY_light_1.fits", null).ShouldBe([TestRenderer.GradeMark.Streaky]);
        TestRenderer.MarksFor("BAD_CLOUD_light_1.fits", cloudyDawn).ShouldBe([TestRenderer.GradeMark.Cloud, TestRenderer.GradeMark.Twilight]);
    }

    /// <summary>
    /// In colour the file list draws each mark as its emoji (the owner's preference: clearer than a silhouette), a twilight
    /// saying which one; Night and a host with no emoji face keep the baked masks.
    /// </summary>
    [Fact]
    public void AMarkInColourIsItsEmojiAndATwilightSaysWhichOne()
    {
        var dawn = new LightGradeVerdict(1f, float.NaN, GradeCause.Sunrise, 0.5f);
        var dusk = new LightGradeVerdict(1f, float.NaN, GradeCause.Sunset, 0.5f);

        TestRenderer.EmojiOf(TestRenderer.GradeMark.Streaky, null).ShouldBe("💫", "the owner's pick for trailed stars");
        TestRenderer.EmojiOf(TestRenderer.GradeMark.Cloud, null).ShouldBe("☁️");
        TestRenderer.EmojiOf(TestRenderer.GradeMark.Twilight, dawn).ShouldBe("🌅");
        TestRenderer.EmojiOf(TestRenderer.GradeMark.Twilight, dusk).ShouldBe("🌇");

        var colour = TestRenderer.MarkNodes([TestRenderer.GradeMark.Defocused], null, 12f, default, colour: true);
        var baked = TestRenderer.MarkNodes([TestRenderer.GradeMark.Defocused], null, 12f, default, colour: false);
        colour.Length.ShouldBe(1);
        baked.Length.ShouldBe(1);
        colour[0].ShouldNotBe(baked[0], "the colour mark is the emoji, the baked one the mask");
    }

    private static async Task<AstroImageDocument> DocumentAt(string path)
    {
        var image = new Image([new float[8, 8]], BitDepth.Int16, 65535f, 0f, 0f, new ImageMeta());
        return await AstroImageDocument.AdoptImageAsync(image, DebayerAlgorithm.None, null, filePath: path,
            cancellationToken: TestContext.Current.CancellationToken);
    }
}
