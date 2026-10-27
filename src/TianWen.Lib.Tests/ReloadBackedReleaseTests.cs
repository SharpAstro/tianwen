using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A float master over the size budget drops its float planes once its load-time readers are done, and every consumer that
/// needs pixels reads them back from the FILE first, explicitly (#368, the reload-backed release of D1').
/// </summary>
/// <remarks>
/// Allocation- and residency-based, never working set. The budget is the policy's own
/// (<see cref="EnhanceRevertPolicy.RetainBudgetBytes"/>): a test over it would need a 130 MB master, so the document's
/// <c>PlaneBudgetBytes</c> seam is lowered instead, which exercises the same decision on a small file.
/// </remarks>
public class ReloadBackedReleaseTests : IDisposable
{
    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    private const int W = 200, H = 200;

    private string WriteMaster(string name, int channels = 3)
    {
        var rng = new Random(7);
        var planes = new float[channels][,];
        var bg = new[] { 200f, 200f, 600f };
        for (var c = 0; c < channels; c++)
        {
            planes[c] = new float[H, W];
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    planes[c][y, x] = bg[c] + (float)((rng.NextDouble() * 20.0) - 10.0);
                }
            }
        }

        // 16 bright round stars on a grid, the same in every channel: what the detector and the calibration need.
        for (var i = 0; i < 16; i++)
        {
            var (sx, sy) = (20 + (i % 4 * 45), 20 + (i / 4 * 45));
            for (var dy = -4; dy <= 4; dy++)
            {
                for (var dx = -4; dx <= 4; dx++)
                {
                    var g = 6000f * MathF.Exp(-((dx * dx) + (dy * dy)) / (2f * 1.4f * 1.4f));
                    for (var c = 0; c < channels; c++)
                    {
                        planes[c][sy + dy, sx + dx] += g;
                    }
                }
            }
        }

        var image = new Image(planes, BitDepth.Int16, 65535f, 0f, 0f,
            new ImageMeta("reload", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), FrameType.Light, "",
                0f, 0f, -1, FocuserPositions.None, Filter.None, 1, 1, float.NaN,
                channels == 3 ? SensorType.Color : SensorType.Monochrome, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN));
        var path = Path.Combine(_folders.Create("tianwen-reload-").FullName, name);
        image.WriteToFitsFile(path);
        return path;
    }

    /// <summary>Opens a master with the budget lowered to nothing, the star detection every load runs behind it done.</summary>
    private static async Task<AstroImageDocument> OpenReleasedAsync(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        var document = (await AstroImageDocument.OpenAsync(path, cancellationToken: ct)).ShouldNotBeNull();
        document.PlaneBudgetBytes = 1;
        await document.DetectStarsAsync(ct);
        return document;
    }

    [Fact(Timeout = 60_000)]
    public async Task AMasterOverTheBudgetHoldsNoPlanesAfterItsLoadTimeReaders()
    {
        TestContext.Current.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        var document = await OpenReleasedAsync(WriteMaster("over.fits"));

        document.UnstretchedImage.PlanesResident.ShouldBeFalse();
        document.UnstretchedImage.PlanesNeedReload.ShouldBeTrue();
        document.UnstretchedImage.Width.ShouldBe(W, "geometry survives the release");
        document.UnstretchedImage.ChannelCount.ShouldBe(3);
        document.Findings.Stars.ShouldNotBeNull().Count.ShouldBeGreaterThan(0, "the detection ran before the release");
    }

    [Fact]
    public void ThePolicyReleasesOnlyOverTheEnhanceBudgetAndOnlyWhereTheFileCanGiveThemBack()
    {
        var big = new Image(new float[1][,] { new float[4096, 8200] }, BitDepth.Float32, 1f, 0f, 0f,
            new ImageMeta("p", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), FrameType.Light, "",
                0f, 0f, -1, FocuserPositions.None, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN));
        EnhanceRevertPolicy.FootprintBytes(big).ShouldBeGreaterThan(EnhanceRevertPolicy.RetainBudgetBytes);

        EnhanceRevertPolicy.DecidePlanes(big, canReload: true).ShouldBe(PlaneResidency.Release);
        EnhanceRevertPolicy.DecidePlanes(big, canReload: false).ShouldBe(PlaneResidency.Keep, "the budget yields where nothing can restore");
        EnhanceRevertPolicy.DecidePlanes(big, canReload: true, budgetBytes: long.MaxValue).ShouldBe(PlaneResidency.Keep);
    }

    [Fact(Timeout = 60_000)]
    public async Task AMasterUnderTheBudgetKeepsItsPlanes()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = (await AstroImageDocument.OpenAsync(WriteMaster("under.fits"), cancellationToken: ct)).ShouldNotBeNull();
        await document.DetectStarsAsync(ct);

        document.UnstretchedImage.PlanesResident.ShouldBeTrue("a 480 KB master is far under the 128 MB budget");
        document.UnstretchedImage.PlanesNeedReload.ShouldBeFalse();
        document.PlaneReloads.ShouldBe(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task ADocumentWithNoFileToReadBackFromIsNeverReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        var planes = new float[1][,];
        planes[0] = new float[H, W];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                planes[0][y, x] = 0.1f + (x * 1e-4f);
            }
        }

        var document = await AstroImageDocument.AdoptImageAsync(new Image(planes, BitDepth.Float32, 1f, 0f, 0f,
            new ImageMeta("mem", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), FrameType.Light, "",
                0f, 0f, -1, FocuserPositions.None, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN)), DebayerAlgorithm.None, cancellationToken: ct);
        document.PlaneBudgetBytes = 1;
        await document.DetectStarsAsync(ct);

        document.ReleasePlanesAfterLoad().ShouldBeFalse();
        document.UnstretchedImage.PlanesResident.ShouldBeTrue("an in-memory frame has no file, so the budget yields");
    }

    [Fact(Timeout = 60_000)]
    public async Task AReadOfAReleasedImageThrowsInsteadOfReadingTheDisk()
    {
        TestContext.Current.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        var document = await OpenReleasedAsync(WriteMaster("loud.fits"));
        var image = document.UnstretchedImage;

        Should.Throw<InvalidOperationException>(() => image.GetChannelSpan(0).Length.ToString());
        Should.Throw<InvalidOperationException>(() => image[0, 1, 1].ToString());
        Should.Throw<InvalidOperationException>(() => ((IPreviewSource)document).GetChannelData(1).Length.ToString());

        image.PlanesResident.ShouldBeFalse("a refused read restores nothing");
        document.PlaneReloads.ShouldBe(0, "no hidden read of the file");
    }

    [Fact(Timeout = 60_000)]
    public async Task TheCursorReadoutNeedsNoPlanesAndRestoresNone()
    {
        var path = WriteMaster("readout.fits");
        var ct = TestContext.Current.CancellationToken;
        var resident = (await AstroImageDocument.OpenAsync(path, cancellationToken: ct)).ShouldNotBeNull();
        var released = await OpenReleasedAsync(path);

        for (var c = 0; c < 3; c++)
        {
            var fromPlanes = ((IPreviewSource)resident).SampleAt(c, 17, 23);
            var fromFile = ((IPreviewSource)released).SampleAt(c, 17, 23);
            BitConverter.SingleToInt32Bits(fromFile).ShouldBe(BitConverter.SingleToInt32Bits(fromPlanes), $"channel {c}");
        }

        released.UnstretchedImage.PlanesResident.ShouldBeFalse();
        released.PlaneReloads.ShouldBe(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task EnsurePlanesRestoresBitIdenticalPlanesOnceForEveryCaller()
    {
        var path = WriteMaster("ensure.fits");
        var ct = TestContext.Current.CancellationToken;
        var resident = (await AstroImageDocument.OpenAsync(path, cancellationToken: ct)).ShouldNotBeNull();
        var released = await OpenReleasedAsync(path);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => released.EnsurePlanesAsync(ct).AsTask()));

        released.PlaneReloads.ShouldBe(1, "six callers, one read of the file");
        released.UnstretchedImage.PlanesResident.ShouldBeTrue();
        for (var c = 0; c < 3; c++)
        {
            var want = resident.UnstretchedImage.GetChannelSpan(c);
            var got = released.UnstretchedImage.GetChannelSpan(c);
            got.Length.ShouldBe(want.Length);
            for (var i = 0; i < want.Length; i++)
            {
                BitConverter.SingleToInt32Bits(got[i]).ShouldBe(BitConverter.SingleToInt32Bits(want[i]), $"channel {c} sample {i}");
            }
        }

        await released.EnsurePlanesAsync(ct);
        released.PlaneReloads.ShouldBe(1, "resident planes are not read again");
    }

    [Fact(Timeout = 60_000)]
    public async Task AFileChangedSinceTheOpenIsRefusedNotQuotedUnderTheOldStatistics()
    {
        var path = WriteMaster("changed.fits");
        var released = await OpenReleasedAsync(path);

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));

        await Should.ThrowAsync<IOException>(async () => await released.EnsurePlanesAsync(TestContext.Current.CancellationToken));
        released.UnstretchedImage.PlanesResident.ShouldBeFalse();
        float.IsNaN(((IPreviewSource)released).SampleAt(0, 3, 3)).ShouldBeTrue("the readout shows an unknown, never throws on the render thread");
    }

    [Fact(Timeout = 120_000)]
    public async Task SavingADisplayedRasterOfAReleasedMasterIsByteIdenticalToAResidentOne()
    {
        var path = WriteMaster("save.fits");
        var ct = TestContext.Current.CancellationToken;
        var resident = (await AstroImageDocument.OpenAsync(path, cancellationToken: ct)).ShouldNotBeNull();
        await resident.DetectStarsAsync(ct);
        var released = await OpenReleasedAsync(path);
        var state = new ViewerState();

        var a = Path.Combine(_folders.Create("tianwen-reload-out-").FullName, "resident.png");
        var b = Path.Combine(_folders.Create("tianwen-reload-out-").FullName, "released.png");
        await ViewerController.WriteDisplayedRasterAsync(resident, state, a, DisplayRasterFormat.Png16, ct);
        released.UnstretchedImage.PlanesResident.ShouldBeFalse();
        await ViewerController.WriteDisplayedRasterAsync(released, state, b, DisplayRasterFormat.Png16, ct);

        released.PlaneReloads.ShouldBe(1);
        File.ReadAllBytes(b).ShouldBe(File.ReadAllBytes(a));
    }

    [Fact(Timeout = 120_000)]
    public async Task CalibrationOfAReleasedMasterGivesTheResidentAnswer()
    {
        var path = WriteMaster("calibrate.fits");
        var ct = TestContext.Current.CancellationToken;
        var resident = (await AstroImageDocument.OpenAsync(path, cancellationToken: ct)).ShouldNotBeNull();
        await resident.DetectStarsAsync(ct);
        var released = await OpenReleasedAsync(path);

        var want = await resident.ComputeColorCalibrationAsync(ct);
        var got = await released.ComputeColorCalibrationAsync(ct);

        got.ShouldBe(want);
        released.ColorCalibration.ShouldBe(resident.ColorCalibration);
        released.PlaneReloads.ShouldBe(1);
    }

    [Fact(Timeout = 120_000)]
    public async Task AChannelToggleOnAReleasedMasterReadsTheFileOffTheRenderThreadAndThenUploads()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var e2e = ViewerE2E.Start(1f);
        var path = e2e.WriteColourFits("toggle.fits", level: 7f);
        await e2e.OpenAsync(path, ct);
        var document = e2e.Controller.Document.ShouldNotBeNull();
        // The load's own detection finishes first, so it is not mid-read when this one releases the planes.
        await e2e.PumpUntilAsync(() => document.Findings.Stars is not null, "the load's star detection", ct);
        document.PlaneBudgetBytes = 1;
        await document.DetectStarsAsync(ct);
        document.UnstretchedImage.PlanesNeedReload.ShouldBeTrue();
        var reloadsBefore = document.PlaneReloads;

        e2e.Key(InputKey.C);

        await e2e.PumpUntilAsync(() => document.UnstretchedImage.PlanesResident && !e2e.State.NeedsTextureUpdate, "the pixels to come back and upload", ct);
        document.PlaneReloads.ShouldBe(reloadsBefore + 1);
        e2e.State.StatusMessage.ShouldBeNull();
    }
}
