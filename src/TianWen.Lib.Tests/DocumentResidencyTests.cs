using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpAstro.Png;
using SharpAstro.Tiff;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// D1' of <c>docs/plans/viewer-memory-footprint.md</c>, measured through the viewer's REAL load: an 8-bit
/// document is evicted at the end of its constructor, and the readers the viewer starts after that
/// (<c>StartStarDetection</c>: star detection, then <c>LightGrading.Grade</c>) read the float planes, which
/// restores them. These tests pin that the 6 B/px steady state holds once those readers are done.
/// </summary>
[Collection("UI")]
public class DocumentResidencyTests : IDisposable
{
    private const int Width = 128;
    private const int Height = 96;

    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    [Fact]
    public async Task AnEightBitDocumentIsEvictedAgainOnceItsLoadTimeReadersAreDone()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await OpenAsync(ct);
        var image = document.UnstretchedImage;

        image.HasSourceRaster.ShouldBeTrue("the fixture must be the retaining case");
        image.PlanesResident.ShouldBeFalse("evicted at the end of the constructor");

        // What ViewerController.StartStarDetection runs, in its order.
        await document.DetectStarsAsync(ct);
        document.Findings.Stars.ShouldNotBeNull().Count.ShouldBeGreaterThan(0, "the planted stars must be found, or the grade below never runs");
        image.PlanesResident.ShouldBeFalse("star detection must not leave the planes resident");

        LightGrading.Grade(image, document.Findings.Stars!).ShouldNotBeNull();
        image.PlanesResident.ShouldBeFalse("grading must not leave the planes resident");
    }

    /// <summary>
    /// A measurement of the two interactions a user makes after load: neither a channel toggle (state
    /// only) nor a cursor readout (served from the retained raster) restores the float planes.
    /// </summary>
    [Fact]
    public async Task ACursorReadoutAndAChannelToggleLeaveThePlanesEvicted()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await OpenAsync(ct);
        var image = document.UnstretchedImage;
        image.PlanesResident.ShouldBeFalse();

        var state = new ViewerState();
        ViewerActions.CycleChannelView(state, image.ChannelCount);
        image.PlanesResident.ShouldBeFalse("a channel toggle is state only and reads no pixel");

        ViewerActions.UpdateCursorInfo(document, document.Findings.Wcs, state, 10, 10);
        state.CursorPixelInfo.ShouldNotBeNull();
        image.PlanesResident.ShouldBeFalse("a cursor readout is served from the retained raster and restores nothing");
    }

    private async Task<AstroImageDocument> OpenAsync(CancellationToken ct)
    {
        var rgb = new byte[Width * Height * 3];
        var rng = new Random(368);
        for (var i = 0; i < rgb.Length; i++)
        {
            rgb[i] = (byte)(20 + rng.Next(6));
        }

        // Planted stars: a few Gaussians, bright and round enough for the detector.
        (int X, int Y)[] centres = [(20, 20), (60, 30), (100, 25), (30, 70), (80, 65), (110, 80), (50, 50), (90, 45)];
        foreach (var (cx, cy) in centres)
        {
            for (var y = cy - 6; y <= cy + 6; y++)
            {
                for (var x = cx - 6; x <= cx + 6; x++)
                {
                    var r2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    var v = 200 * MathF.Exp(-r2 / (2f * 1.5f * 1.5f));
                    for (var c = 0; c < 3; c++)
                    {
                        var o = (y * Width + x) * 3 + c;
                        rgb[o] = (byte)Math.Min(255, rgb[o] + (int)v);
                    }
                }
            }
        }

        var path = Path.Combine(_folders.Create("residency-").FullName, "rgb8.tif");
        var png = PngWriter.EncodeRgb8(rgb, Width, Height);
        await using (var writer = TiffWriter.Create(path))
        {
            await writer.AddPngPageAsync(png, ct: ct);
            await writer.FlushAsync(ct);
        }

        var document = await AstroImageDocument.OpenAsync(path, cancellationToken: ct);
        return document.ShouldNotBeNull();
    }
}
