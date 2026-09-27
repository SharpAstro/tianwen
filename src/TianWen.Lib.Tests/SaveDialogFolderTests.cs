using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The viewer's Save dialog opens in the folder of the file that is open (P35, #904), and in the
/// platform's own default when the picture came from no file.
/// </summary>
/// <remarks>
/// The dialog is a substitute that records what it was asked and answers "cancelled", so no window
/// opens and nothing is written.
/// </remarks>
public class SaveDialogFolderTests
{
    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveOpensInTheFolderTheOpenFileWasReadFrom(bool withOverlays)
    {
        var fits = await SharedTestData.ExtractGZippedFitsFileAsync("PlateSolveTestFile",
            TestContext.Current.CancellationToken);

        var asked = await SaveAndCaptureInitialDirectoryAsync(fits, withOverlays);

        asked.ShouldBe(Path.GetDirectoryName(Path.GetFullPath(fits)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ADocumentFromNoFileGetsThePlatformDefault(string? documentPath)
    {
        // A live frame adopted into the viewer carries an empty path.
        ViewerController.SaveDialogDirectory(documentPath).ShouldBeNull();
    }

    [Fact]
    public void AFileWhoseFolderHasGoneGetsThePlatformDefault()
    {
        var gone = Path.Combine(Path.GetTempPath(), $"tianwen-save-folder-{Guid.NewGuid():N}", "frame.fits");

        ViewerController.SaveDialogDirectory(gone).ShouldBeNull();
    }

    [Fact]
    public void AFileInAnExistingFolderGetsThatFolder()
    {
        var directory = Directory.CreateTempSubdirectory("tianwen-save-folder-");
        try
        {
            ViewerController.SaveDialogDirectory(Path.Combine(directory.FullName, "frame.fits"))
                .ShouldBe(directory.FullName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> through the controller, asks it to save, and returns the folder
    /// it handed the dialog.
    /// </summary>
    private static async Task<string?> SaveAndCaptureInitialDirectoryAsync(string path, bool withOverlays)
    {
        var state = new ViewerState();
        var cache = Substitute.For<IDocumentCache>();
        var dialog = Substitute.For<IFileDialogHelper>();
        var controller = new ViewerController(state, cache, dialog,
            Substitute.For<IPlateSolverFactory>(), new FakeTimeProviderWrapper(),
            new BackgroundTaskTracker(), NullLogger<ViewerController>.Instance);

        string? asked = null;
        var called = 0;
        dialog.SaveAsync(Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                called++;
                asked = call.ArgAt<string?>(3);
                return Task.FromResult<string?>(null);
            });

        cache.GetOrLoadAsync(Arg.Any<string>(), Arg.Any<DebayerAlgorithm>(), Arg.Any<CancellationToken>())
            .Returns(_ => AstroImageDocument.OpenAsync(path, DebayerAlgorithm.None, CancellationToken.None));

        state.RequestedFilePath = path;
        controller.HandleFileRequest(CancellationToken.None);
        while (controller.IsLoadPending)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        controller.Document.ShouldNotBeNull();

        controller.SaveImage(withOverlays, PngDepth.SixteenBit, CancellationToken.None);
        await controller.ShutdownAsync();

        called.ShouldBe(1);
        return asked;
    }
}
