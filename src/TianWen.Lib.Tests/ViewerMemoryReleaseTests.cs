using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Opening documents stays inside ONE budget across every open document: the least recently used one the file can give back
/// is released, a pinned one never is, and the next consumer reads the file once (#1852, on #368).
/// </summary>
/// <remarks>
/// CPU only. A 200 x 200 three-channel master holds 480,000 bytes of planes, so a total budget of 1,000,000 admits two and
/// not three, the same decision a viewer makes over 100 MB masters.
/// </remarks>
public class ViewerMemoryReleaseTests : IDisposable
{
    private const int W = 200, H = 200;
    private const long OneMaster = (long)W * H * 3 * sizeof(float);
    private const long TwoMasters = (2 * OneMaster) + 40_000;

    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    private static ImageMeta Meta(string name, SensorType sensor)
        => new(name, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), FrameType.Light, "",
            0f, 0f, -1, FocuserPositions.None, Filter.None, 1, 1, float.NaN, sensor, 0, 0,
            RowOrder.TopDown, float.NaN, float.NaN);

    private string WriteMaster(string name, int seed)
    {
        var rng = new Random(seed);
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[H, W];
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    planes[c][y, x] = 300f + (float)(rng.NextDouble() * 20.0);
                }
            }
        }

        var path = Path.Combine(_folders.Create("tianwen-memrelease-").FullName, name);
        new Image(planes, BitDepth.Int16, 65535f, 0f, 0f, Meta(name, SensorType.Color)).WriteToFitsFile(path);
        return path;
    }

    private static async Task<AstroImageDocument> OpenAsync(DocumentCache cache, string path)
        => (await cache.GetOrLoadAsync(path, cancellationToken: TestContext.Current.CancellationToken)).ShouldNotBeNull();

    private static void ShouldBeResident(AstroImageDocument d) => d.UnstretchedImage.PlanesResident.ShouldBeTrue();

    private static void ShouldBeReleased(AstroImageDocument d) => d.UnstretchedImage.PlanesNeedReload.ShouldBeTrue();

    [Fact(Timeout = 60_000)]
    public async Task OpeningUnderTheBudgetReleasesNothing()
    {
        TestContext.Current.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        var cache = new DocumentCache { TotalBudgetBytes = TwoMasters };

        var a = await OpenAsync(cache, WriteMaster("a.fits", 1));
        var b = await OpenAsync(cache, WriteMaster("b.fits", 2));

        ShouldBeResident(a);
        ShouldBeResident(b);
        a.PlaneReloads.ShouldBe(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheLeastRecentlyUsedDocumentGoesFirst()
    {
        TestContext.Current.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        var cache = new DocumentCache { TotalBudgetBytes = TwoMasters };
        var a = await OpenAsync(cache, WriteMaster("a.fits", 1));
        var b = await OpenAsync(cache, WriteMaster("b.fits", 2));

        // A is the older open, but it was just used: B is now the least recently used.
        using (await a.EnsurePlanesAsync(TestContext.Current.CancellationToken))
        {
        }

        var c = await OpenAsync(cache, WriteMaster("c.fits", 3));

        ShouldBeReleased(b);
        ShouldBeResident(a);
        ShouldBeResident(c);
    }

    [Fact(Timeout = 60_000)]
    public async Task APinnedDocumentIsNeverReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new DocumentCache { TotalBudgetBytes = OneMaster + 40_000 };
        var a = await OpenAsync(cache, WriteMaster("a.fits", 1));

        var pin = await a.EnsurePlanesAsync(ct);
        a.PinCount.ShouldBe(1);
        var b = await OpenAsync(cache, WriteMaster("b.fits", 2));

        ShouldBeResident(a);
        ShouldBeResident(b);
        cache.Admit(b).ShouldBeEmpty("the one document that could go is held");

        pin.Dispose();
        pin.Dispose();
        a.PinCount.ShouldBe(0, "a pin lets go once, however often it is disposed");
        cache.Admit(b).ShouldHaveSingleItem().ShouldBeSameAs(a);
        ShouldBeReleased(a);
        ShouldBeResident(b);
    }

    [Fact(Timeout = 60_000)]
    public async Task AReleasedDocumentsNextConsumerReadsTheFileExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new DocumentCache { TotalBudgetBytes = OneMaster + 40_000 };
        var a = await OpenAsync(cache, WriteMaster("a.fits", 1));
        var before = a.UnstretchedImage[1, 10, 10];
        await OpenAsync(cache, WriteMaster("b.fits", 2));
        ShouldBeReleased(a);
        a.PlaneReloads.ShouldBe(0);

        using (await a.EnsurePlanesAsync(ct))
        {
            using var second = await a.EnsurePlanesAsync(ct);
            a.UnstretchedImage[1, 10, 10].ShouldBe(before, "the planes come back bit for bit");
        }

        a.PlaneReloads.ShouldBe(1, "two consumers, one read of the file");
        a.PinCount.ShouldBe(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task ADocumentTheFileCannotGiveBackIsNeverReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        var planes = new float[1][,] { new float[H, W] };
        var frame = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, Meta("mem", SensorType.Monochrome));
        var inMemory = await AstroImageDocument.AdoptImageAsync(frame, DebayerAlgorithm.None, cancellationToken: ct);
        var crop = await AstroImageDocument.AdoptImageAsync(frame.Crop(new PixelRect(10, 10, 100, 100)), DebayerAlgorithm.None, cancellationToken: ct);

        inMemory.TryReleaseForBudget().ShouldBeFalse("an in-memory frame has no file");
        crop.TryReleaseForBudget().ShouldBeFalse("a crop is not what its file holds");
        ShouldBeResident(inMemory);
        ShouldBeResident(crop);
    }

    [Fact(Timeout = 60_000)]
    public async Task AReleaseAfterLoadWhileAConsumerHoldsTheGateKeepsThePlanes()
    {
        var cache = new DocumentCache { TotalBudgetBytes = OneMaster + 40_000 };
        var a = await OpenAsync(cache, WriteMaster("a.fits", 1));
        a.PlaneBudgetBytes = 0;

        await a.ReloadGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            a.ReleasePlanesAfterLoad().ShouldBeFalse("a pin may be being taken: the release gives up rather than read PinCount unguarded");
        }
        finally
        {
            a.ReloadGate.Release();
        }

        ShouldBeResident(a);
        a.ReleasePlanesAfterLoad().ShouldBeTrue("with the gate free and no pin, the size policy releases");
        ShouldBeReleased(a);
    }

    [Fact(Timeout = 60_000)]
    public async Task ADetectionThatFailsWhileHoldingItsPinLetsGoOfIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new DocumentCache { TotalBudgetBytes = OneMaster + 40_000 };
        var a = await OpenAsync(cache, WriteMaster("a.fits", 1));

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await a.DetectStarsAsync(ct, whileResident: static (_, _) => throw new InvalidOperationException("consumer failed")));

        a.PinCount.ShouldBe(0, "a failed read must not keep the planes for the document's life");
        await OpenAsync(cache, WriteMaster("b.fits", 2));
        ShouldBeReleased(a);
    }

    [Fact(Timeout = 60_000)]
    public async Task ADetectionThatIsCancelledWhileHoldingItsPinLetsGoOfIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new DocumentCache { TotalBudgetBytes = OneMaster + 40_000 };
        var a = await OpenAsync(cache, WriteMaster("a.fits", 1));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await a.DetectStarsAsync(cts.Token, whileResident: (_, _) =>
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }));

        a.PinCount.ShouldBe(0, "a cancelled read must not keep the planes for the document's life");
        await OpenAsync(cache, WriteMaster("b.fits", 2));
        ShouldBeReleased(a);
    }
}
