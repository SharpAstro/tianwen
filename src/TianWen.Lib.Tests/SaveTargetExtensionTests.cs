using System;
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
/// A save never writes one format's bytes under another format's name.
/// </summary>
/// <remarks>
/// <para>A native save dialog hands back whatever was TYPED, its filter list notwithstanding, so a
/// path can name a container neither writer has an encoder for: <c>.tif</c> for an annotated save,
/// which is PNG or JPEG only, and anything at all for either. Both paths used to answer that by
/// writing PNG bytes under the typed name, and the file being fine while its NAME lies is the worse
/// half -- it surfaces later, somewhere else, as a file that will not open, instead of now as a save
/// that went differently than asked.</para>
/// <para>Reachable only by typing the extension by hand, which is exactly why it needs a test: no
/// amount of clicking through the dropdown will ever produce it.</para>
/// </remarks>
public class SaveTargetExtensionTests
{
    [Fact]
    public void TheAnnotatedWriterNoLongerGuessesAtAnExtensionItCannotProduce()
    {
        // The guess is what made the bug silent. Null forces the caller to have a policy, and to have
        // it somewhere a reader can see it.
        AnnotatedRasterExport.FromExtension("shot.png").ShouldBe(AnnotatedRasterFormat.Png);
        AnnotatedRasterExport.FromExtension("shot.jpg").ShouldBe(AnnotatedRasterFormat.Jpeg);
        AnnotatedRasterExport.FromExtension("shot.jpeg").ShouldBe(AnnotatedRasterFormat.Jpeg);
        AnnotatedRasterExport.FromExtension("shot.tif").ShouldBeNull();
        AnnotatedRasterExport.FromExtension("shot.bmp").ShouldBeNull();
        AnnotatedRasterExport.FromExtension("shot").ShouldBeNull();
    }

    [Theory]
    [InlineData("shot.tif")]     // a real format, just not one the ANNOTATED writer produces
    [InlineData("shot.bmp")]
    [InlineData("shot.txt")]
    public async Task AnAnnotatedSaveToAnUnsupportedExtensionLandsAsAPngNamedPng(string typed)
    {
        var written = await SaveAsync(typed, withOverlays: true);

        Path.GetExtension(written.Name).ShouldBe(".png");
        Path.GetFileNameWithoutExtension(written.Name).ShouldBe("shot");
        written.IsPng.ShouldBeTrue("the bytes are a PNG, so the name must say PNG");
    }

    [Theory]
    [InlineData("shot.bmp")]
    [InlineData("shot.gif")]
    [InlineData("shot")]
    public async Task ACleanSaveToAnUnsupportedExtensionLandsAsAPngNamedPng(string typed)
    {
        // The clean path had the same defect and kept it a release longer, because its FromExtension
        // returned null honestly and the CALLER then wrote PNG under the typed name anyway.
        var written = await SaveAsync(typed, withOverlays: false);

        Path.GetExtension(written.Name).ShouldBe(".png");
        written.IsPng.ShouldBeTrue();
    }

    [Theory]
    [InlineData("shot.png", ".png")]
    [InlineData("shot.jpg", ".jpg")]
    [InlineData("shot.tif", ".tif")]
    public async Task ACleanSaveToASupportedExtensionKeepsTheNameExactly(string typed, string expected)
    {
        // The correction must be confined to the case that needs it: a path naming a format we DO
        // write is written under that name, untouched, including the TIFF the annotated path cannot do.
        var written = await SaveAsync(typed, withOverlays: false);

        Path.GetExtension(written.Name).ShouldBe(expected);
    }

    private static bool IsPng(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[8];
        ReadOnlySpan<byte> pngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        return file.ReadAtLeast(signature, 8, throwOnEndOfStream: false) == 8
            && signature.SequenceEqual(pngSignature);
    }

    /// <summary>
    /// Drives a real save through the controller with the dialog stubbed to "type" <paramref name="typed"/>,
    /// and reports the file that actually appeared on disk. The container is sniffed HERE, before the
    /// temp directory goes, rather than handed back as a path the caller could only find deleted.
    /// </summary>
    private static async Task<(string Name, bool IsPng)> SaveAsync(string typed, bool withOverlays)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tianwen-save-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var state = new ViewerState();
            var cache = Substitute.For<IDocumentCache>();
            var dialog = Substitute.For<IFileDialogHelper>();
            var controller = new ViewerController(state, cache, dialog,
                Substitute.For<IPlateSolverFactory>(), new FakeTimeProviderWrapper(),
                new BackgroundTaskTracker(), NullLogger<ViewerController>.Instance);

            dialog.SaveAsync(Arg.Any<System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IReadOnlyList<string>>>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Path.Combine(directory, typed));

            var fits = await SharedTestData.ExtractGZippedFitsFileAsync("PlateSolveTestFile",
                TestContext.Current.CancellationToken);
            cache.GetOrLoadAsync(Arg.Any<string>(), Arg.Any<DebayerAlgorithm>(), Arg.Any<CancellationToken>())
                .Returns(_ => AstroImageDocument.OpenAsync(fits, DebayerAlgorithm.None, CancellationToken.None));

            state.RequestedFilePath = fits;
            controller.HandleFileRequest(CancellationToken.None);
            var deadline = Environment.TickCount64 + 20_000;
            while (controller.IsLoadPending && Environment.TickCount64 < deadline)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            controller.Document.ShouldNotBeNull();

            controller.SaveImage(withOverlays, PngDepth.SixteenBit, CancellationToken.None);
            await controller.ShutdownAsync();

            var files = Directory.GetFiles(directory);
            files.Length.ShouldBe(1, $"exactly one file should have been written; got [{string.Join(", ", files)}]");
            return (Path.GetFileName(files[0]), IsPng(files[0]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
