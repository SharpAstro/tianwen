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
public class SaveDialogFolderTests : IDisposable
{
    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveOpensInTheFolderTheOpenFileWasReadFrom(bool withOverlays)
    {
        var fits = await SharedTestData.ExtractGZippedFitsFileAsync("PlateSolveTestFile",
            TestContext.Current.CancellationToken);

        var asked = await SaveAndCaptureRequestAsync(fits, withOverlays, new ViewerState());

        asked.InitialDirectory.ShouldBe(Path.GetDirectoryName(Path.GetFullPath(fits)));
    }

    [Fact(Timeout = 60_000)]
    public async Task TheDisplayedSaveSuggestsANameThatSaysHowThePictureWasRendered()
    {
        var fits = await SharedTestData.ExtractGZippedFitsFileAsync("PlateSolveTestFile",
            TestContext.Current.CancellationToken);
        var stem = Path.GetFileNameWithoutExtension(fits);

        var plain = await SaveAndCaptureRequestAsync(fits, withOverlays: false, new ViewerState());
        var moved = await SaveAndCaptureRequestAsync(fits, withOverlays: false, new ViewerState { FinishSaturation = 1.24f });

        // The stretch on show is always named; a moved dial adds its code, so the second save does not land on the first.
        plain.SuggestedFileName.ShouldStartWith(stem + "_");
        plain.SuggestedFileName.ShouldEndWith(".png");
        moved.SuggestedFileName.ShouldBe(Path.GetFileNameWithoutExtension(plain.SuggestedFileName) + "_sat1.24.png");
    }

    [Fact(Timeout = 60_000)]
    public async Task TheAnnotatedSaveKeepsItsOwnName()
    {
        var fits = await SharedTestData.ExtractGZippedFitsFileAsync("PlateSolveTestFile",
            TestContext.Current.CancellationToken);

        var asked = await SaveAndCaptureRequestAsync(fits, withOverlays: true, new ViewerState { FinishSaturation = 1.24f });

        asked.SuggestedFileName.ShouldBe(Path.GetFileNameWithoutExtension(fits) + "-annotated.png");
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
        var directory = _folders.Create("tianwen-save-folder-");
        ViewerController.SaveDialogDirectory(Path.Combine(directory.FullName, "frame.fits"))
            .ShouldBe(directory.FullName);
    }

    /// <summary>
    /// Opens <paramref name="path"/> through the controller under <paramref name="state"/>, asks it to save, and returns
    /// what it asked the dialog.
    /// </summary>
    private static async Task<FileDialogRequest> SaveAndCaptureRequestAsync(string path, bool withOverlays, ViewerState state)
    {
        var cache = Substitute.For<IDocumentCache>();
        var dialog = Substitute.For<IFileDialogs>();
        var controller = new ViewerController(state, cache, dialog,
            Substitute.For<IPlateSolverFactory>(), new FakeTimeProviderWrapper(),
            new BackgroundTaskTracker(), NullLogger<ViewerController>.Instance);

        FileDialogRequest? asked = null;
        var called = 0;
        dialog.SaveAsync(Arg.Any<FileDialogRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                called++;
                asked = call.ArgAt<FileDialogRequest>(0);
                return Task.FromResult(new FileDialogResult());
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
        return asked ?? throw new InvalidOperationException("The dialog was never asked");
    }
}
