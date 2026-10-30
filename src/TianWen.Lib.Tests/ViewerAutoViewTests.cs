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
/// The viewer's Auto view (A4 of AUTO, #1391; the Best view folded into it, #817), driven through the host <c>tianwen-fits</c> runs
/// (<see cref="ViewerE2E"/>): the capture identified and stacked by the routine <c>planetary stack --auto</c> runs (<see cref="PlanetaryAuto"/>),
/// so with nothing chosen the two write the same masters (rule 1); a choice made in the panel is given to the identification first, and
/// a telescope only once it is set on the capture, the viewer's last one taken only where the capture names none.
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
        // The viewer's last telescope says otherwise, which the capture's header beats: it was not set on this capture.
        (e2e.State.PlanetaryApertureMm, e2e.State.PlanetaryDesign) = (102, OpticalDesign.Refractor);

        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewAuto" }, "the Auto view"));
        await ViewerBestStackTests.AutoOnShowAsync(e2e, ct);

        e2e.State.PlanetaryView.ShouldBe(PlanetaryView.Auto);
        var identity = e2e.State.AutoIdentity.ShouldNotBeNull();
        (identity.Planet, identity.FilterNm, identity.ApertureMm, identity.Design)
            .ShouldBe((CatalogIndex.Jupiter, (double?)650, (int?)254, OpticalDesign.Newtonian));
        e2e.State.WaveletDerived.ShouldBeTrue("its run derived the gains through the telescope it read");
        (e2e.State.PlanetaryApertureMm, e2e.State.PlanetaryDesign).ShouldBe(((int?)254, OpticalDesign.Newtonian), "the panel shows the telescope the run read");

        var strip = InfoPanelData.GetMetadataLines(e2e.Controller.Source.ShouldNotBeNull(), e2e.State);
        strip.ShouldContain(line => line.StartsWith("Kept: ", StringComparison.Ordinal), "the Auto master's strip says the frames kept of the graded");
        strip.ShouldContain(line => line.StartsWith("Frames: ", StringComparison.Ordinal), "and keeps the capture's own lines");

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
    public async Task ATelescopeSetOnTheCaptureIsGivenAndTheViewersLastTakenOnlyWhereTheCaptureNamesNone(float dpi)
    {
        // #817: the Best view took the panel's telescope, Auto read the capture's; folded together, a telescope set on this capture is
        // given, the capture's own word comes next, and the viewer's last telescope only where the capture names none.
        await using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        var named = ViewerBestStackTests.WriteCapture(Path.Combine(e2e.Folder, "Jupiter_Red_2024-12-15T12_56_44_OTA1.ser"),
            telescope: "254 mm f/4.7 Newtonian, Test OTA");
        await OpenAsync(e2e, named, ct);
        e2e.State.PlanetaryApertureMm.ShouldBe(254, "the header's telescope shows in the panel");
        e2e.Click(e2e.EnhanceRegion(h => h is HitResult.ButtonHit { Action: "ApertureDown" }, "the aperture's step down"));
        e2e.State.PlanetaryTelescopeSetHere.ShouldBeTrue();

        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewAuto" }, "the Auto view"));
        await ViewerBestStackTests.AutoOnShowAsync(e2e, ct);
        var set = e2e.State.AutoIdentity.ShouldNotBeNull();
        (set.ApertureMm, set.Design, set.TelescopeFrom).ShouldBe(((int?)235, OpticalDesign.Newtonian, "given"));

        // A capture that names no telescope, opened after: nothing is set on it, so the viewer's last telescope is the run's.
        var unnamed = ViewerBestStackTests.WriteCapture(Path.Combine(e2e.Folder, "2024-12-15-1256_7-Jupiter.ser"));
        await OpenAsync(e2e, unnamed, ct);
        e2e.State.PlanetaryTelescopeSetHere.ShouldBeFalse("a telescope set on the last capture is not set on this one");
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewAuto" }, "the Auto view"));
        await e2e.PumpUntilAsync(() => e2e.State.AutoIdentity is not null && !e2e.Controller.IsAutoStackPending, "the second Auto stack", ct,
            untilTimeout: true);
        var last = e2e.State.AutoIdentity.ShouldNotBeNull();
        (last.ApertureMm, last.Design, last.TelescopeFrom).ShouldBe(((int?)235, OpticalDesign.Newtonian, "the viewer's last telescope"));
        e2e.Controller.AutoStacksStarted.ShouldBe(2);
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
        e2e.State.PlanetaryTelescopeSetHere.ShouldBeFalse();
    }

    private static async Task OpenAsync(ViewerE2E e2e, string capture, System.Threading.CancellationToken ct)
    {
        e2e.Host.HandleDropFile(capture);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == capture, "the capture to open", ct);
        // Held at one frame: a capture opens playing.
        e2e.State.IsPlaying = false;
        e2e.Frame();
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
