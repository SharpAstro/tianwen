using System;
using System.IO;
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
/// The viewer's Auto view (A4 of AUTO, #1391), driven through the host <c>tianwen-fits</c> runs (<see cref="ViewerE2E"/>): the capture
/// identified and stacked with nothing asked by the routine <c>planetary stack --auto</c> runs (<see cref="PlanetaryAuto"/>), so the two
/// write the same masters (rule 1), kept beside Best's so switching between them stacks nothing again.
/// </summary>
[Collection("Viewer")]
public class ViewerAutoViewTests
{
    [Theory(Timeout = 300_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AutoReadsTheCaptureAndWritesTheMastersPlanetaryStackAutoWrites(float dpi)
    {
        await using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        // A TianWen recording: its name says the planet and the filter, its header the telescope (#1179).
        var capture = ViewerBestStackTests.WriteCapture(Path.Combine(e2e.Folder, "Jupiter_Red_2024-12-15T12_56_44_OTA1.ser"),
            telescope: "254 mm f/4.7 Newtonian, Test OTA");
        await OpenAsync(e2e, capture, ct);
        // The panel says otherwise, which Auto does not read.
        (e2e.State.PlanetaryBody, e2e.State.PlanetaryApertureMm, e2e.State.PlanetaryDesign) = (CatalogIndex.Saturn, 102, OpticalDesign.Refractor);

        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewAuto" }, "the Auto view"));
        await AutoOnShowAsync(e2e, ct);

        e2e.State.PlanetaryView.ShouldBe(PlanetaryView.Auto);
        var identity = e2e.State.AutoIdentity.ShouldNotBeNull();
        (identity.Planet, identity.FilterNm, identity.ApertureMm, identity.Design)
            .ShouldBe((CatalogIndex.Jupiter, (double?)650, (int?)254, OpticalDesign.Newtonian));
        e2e.State.WaveletDerived.ShouldBeTrue("its run derived the gains through the telescope it read");

        // Rule 1: the masters the view wrote are, to the bit, those the routine planetary stack --auto runs writes.
        var viewer = PlanetaryAuto.OutputPaths(e2e.Folder, Path.GetFileNameWithoutExtension(capture));
        var direct = Path.Combine(e2e.Folder, "direct");
        Directory.CreateDirectory(direct);
        var paths = PlanetaryAuto.OutputPaths(direct, Path.GetFileNameWithoutExtension(capture));
        var read = await PlanetaryIdentification.IdentifyAsync(capture, cancellationToken: ct);
        read.ShouldBe(identity);
        PlanetaryBestStackResult result;
        using (var stream = SerFrameStream.Open(capture))
        {
            result = await PlanetaryAuto.RunAsync(stream, read, cancellationToken: ct);
        }
        try
        {
            PlanetaryAuto.Write(result, paths);
        }
        finally
        {
            result.Stack.Master.Release();
            result.Sharpened.Release();
            result.Layer?.Master.Release();
        }
        SameBits(viewer.Master, paths.Master);
        SameBits(viewer.Sharpened, paths.Sharpened);
    }

    [Theory(Timeout = 300_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task SwitchingBetweenAutoAndBestStacksEachOnce(float dpi)
    {
        await using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        var capture = ViewerBestStackTests.WriteCapture(Path.Combine(e2e.Folder, "2024-12-15-1256_7-Jupiter.ser"));
        await OpenAsync(e2e, capture, ct);
        (e2e.State.PlanetaryApertureMm, e2e.State.PlanetaryDesign) = (254, OpticalDesign.Newtonian);

        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewAuto" }, "the Auto view"));
        var auto = await AutoOnShowAsync(e2e, ct);
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewBest" }, "the Best view"));
        var best = await ViewerBestStackTests.BestOnShowAsync(e2e, ct);
        e2e.Controller.BestStacksStarted.ShouldBe(2);

        for (var round = 0; round < 2; round++)
        {
            e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewAuto" }, "the Auto view"));
            await e2e.PumpUntilAsync(() => ReferenceEquals(e2e.Controller.Source, auto) && !auto.IsBusy, "the Auto view back", ct);
            e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewBest" }, "the Best view"));
            await e2e.PumpUntilAsync(() => ReferenceEquals(e2e.Controller.Source, best) && !best.IsBusy, "the Best view back", ct);
        }
        e2e.Controller.BestStacksStarted.ShouldBe(2, "each view shows the run it has");
        e2e.State.PlanetaryView.ShouldBe(PlanetaryView.Best);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(1f)]
    public async Task AnotherCaptureOpenedTakesItsOwnPlanetAndFilterNotTheLastOnes(float dpi)
    {
        // The panel's planet and filter stuck from one capture to the next (#1391's survey): a Saturn chosen for the last one was the
        // next one's too, whatever its name said.
        await using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        await OpenAsync(e2e, ViewerBestStackTests.WriteCapture(Path.Combine(e2e.Folder, "calibrated.ser")), ct);
        (e2e.State.PlanetaryBody, e2e.State.PlanetaryFilterNm) = (CatalogIndex.Saturn, 460);

        await OpenAsync(e2e, ViewerBestStackTests.WriteCapture(Path.Combine(e2e.Folder, "2024-12-15-1256_7-Jupiter.ser")), ct);

        (e2e.State.PlanetaryBody, e2e.State.PlanetaryFilterNm).ShouldBe((null, null));
        e2e.State.AutoIdentity.ShouldBeNull();
    }

    private static async Task OpenAsync(ViewerE2E e2e, string capture, System.Threading.CancellationToken ct)
    {
        e2e.Host.HandleDropFile(capture);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == capture, "the capture to open", ct);
        // Held at one frame: a capture opens playing.
        e2e.State.IsPlaying = false;
        e2e.Frame();
    }

    // The Auto view on show: its master built behind its layer, the source the viewer draws, and nothing left in flight.
    private static async Task<LiveStackPreviewSource> AutoOnShowAsync(ViewerE2E e2e, System.Threading.CancellationToken ct)
    {
        await e2e.PumpUntilAsync(() => e2e.Controller.ViewLayers.Auto is { HasMaster: true, IsBusy: false } auto
            && ReferenceEquals(e2e.Controller.Source, auto) && !e2e.Controller.IsBestStackPending, "the Auto view to show", ct, untilTimeout: true);
        return e2e.Controller.ViewLayers.Auto.ShouldNotBeNull();
    }

    // Two FITS masters equal sample for sample, and in what their headers say of the capture.
    private static void SameBits(string expectedPath, string actualPath)
    {
        Image.TryReadFitsFile(expectedPath, out var expected).ShouldBeTrue(expectedPath);
        Image.TryReadFitsFile(actualPath, out var actual).ShouldBeTrue(actualPath);
        try
        {
            (actual.ChannelCount, actual.Width, actual.Height).ShouldBe((expected.ChannelCount, expected.Width, expected.Height));
            for (var c = 0; c < expected.ChannelCount; c++)
            {
                actual.GetChannelSpan(c).SequenceEqual(expected.GetChannelSpan(c)).ShouldBeTrue($"channel {c} of {Path.GetFileName(actualPath)}");
            }
            (actual.ImageMeta.ObjectName, actual.ImageMeta.Telescope, actual.ImageMeta.Aperture, actual.ImageMeta.Filter)
                .ShouldBe((expected.ImageMeta.ObjectName, expected.ImageMeta.Telescope, expected.ImageMeta.Aperture, expected.ImageMeta.Filter));
        }
        finally
        {
            expected.Release();
            actual.Release();
        }
    }
}
