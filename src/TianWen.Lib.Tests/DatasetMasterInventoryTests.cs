using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>tianwen dataset masters</c> answers, from the product's own rules, the questions the gallery's
/// row builder used to answer by restating them: which <c>.fits</c> is a sidecar, which suffix is a
/// pier side, which master is the combined one of a flipped night. Each restatement had produced a
/// wrong number once (278 masters in a 139-master store; 92 rows joined to no stats).
/// </summary>
public class DatasetMasterInventoryTests
{
    [Fact]
    public async Task AFlippedNightIsThreeMastersAndTwoSidesAndTheSidecarIsNotAFourth()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tw-inv-{Guid.NewGuid():N}");
        var mastersDir = Path.Combine(root, RetainedMasterStore.DirectoryName);
        Directory.CreateDirectory(mastersDir);
        try
        {
            // Named the way the bake names them: through the session id and the store's own path rule.
            const string night = "2026/lobster|QHY294PROC|Lobster Nebula|IDAS LPS D3";
            var combined = RetainedMasterStore.PathFor(root, night);
            var sideA = RetainedMasterStore.PathFor(root, night + "|" + ImagingSession.FlipSideKey + "=a");
            var sideB = RetainedMasterStore.PathFor(root, night + "|" + ImagingSession.FlipSideKey + "=b");
            var lonely = RetainedMasterStore.PathFor(root, "2026/helix|SV605CC|Helix Nebula");
            foreach (var (path, subs) in new[] { (combined, 100), (sideA, 32), (sideB, 68), (lonely, 12) })
            {
                Flat(0.25f).WriteToFitsFile(path, wcs: null, new Dictionary<string, (object Value, string Comment)>
                {
                    ["OBJECT"] = ("Lobster Nebula", ""),
                    ["STACK_N"] = (subs, ""),
                    ["STRATEGY"] = ("BayerDrizzle", ""),
                });
            }
            // A coverage sidecar beside the combined master: a .fits in the same folder that is NOT a master.
            Flat(1f).WriteToFitsFile(IntegrationFitsWriter.RejectionPathFor(combined));

            var entries = await DatasetMasterInventory.ListAsync(root, skyPatchSize: null, cancellationToken: TestContext.Current.CancellationToken);

            entries.Length.ShouldBe(4, "the sidecar is not a master");
            var byName = entries.ToDictionary(static e => e.Name);
            var stem = Path.GetFileNameWithoutExtension(combined);

            byName[stem].FlipSide.ShouldBe(DatasetMasterInventory.BothSides);
            byName[stem].FlipGroup.ShouldBe(stem);
            byName[stem].HasCoverageSidecar.ShouldBeTrue();
            byName[stem].StackedFrames.ShouldBe(100);
            byName[stem].Object.ShouldBe("Lobster Nebula");
            byName[stem].Strategy.ShouldBe("BayerDrizzle");
            byName[stem].Solved.ShouldBeFalse("no plate solution was written");

            var a = byName[Path.GetFileNameWithoutExtension(sideA)];
            a.FlipSide.ShouldBe("a");
            a.FlipGroup.ShouldBe(stem, "the three masters of one night share a group");
            a.StackedFrames.ShouldBe(32);
            a.HasCoverageSidecar.ShouldBeFalse();
            byName[Path.GetFileNameWithoutExtension(sideB)].FlipSide.ShouldBe("b");

            var alone = byName[Path.GetFileNameWithoutExtension(lonely)];
            alone.FlipSide.ShouldBeNull("a night that never flipped has no side");
            alone.FlipGroup.ShouldBeNull();
            alone.SessionId.ShouldBeNull("no PSF store, so no record to join");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The sky patch is the square background neutralisation measures, so a card's 1:1 patch and the
    /// render's own sky are the same pixels by construction. A patch picked by any other rule can sit
    /// inside nebulosity the render correctly declined to call sky, and then reads as a colour cast.
    /// </summary>
    [Fact]
    public void TheBackgroundRegionIsTheDarkestSquareInsideTheMargin()
    {
        const int size = 512;
        var plane = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // A bright field with one dark well, and a darker still strip on the very edge that the
                // 5 percent margin must ignore (a canvas ring is exactly that). The well is wider than
                // the scan's stride (four squares, 128 px), so a grid point lands inside it whatever
                // the margin rounds to.
                plane[y, x] = x < 8 ? 0.001f
                    : (x >= 256 && x < 400 && y >= 128 && y < 272) ? 0.05f
                    : 0.5f;
            }
        }
        var image = new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());

        var region = image.FindBackgroundRegion(squareSize: 32);

        region.Width.ShouldBe(32);
        region.X.ShouldBeGreaterThanOrEqualTo(256);
        region.X.ShouldBeLessThanOrEqualTo(400 - 32);
        region.Y.ShouldBeGreaterThanOrEqualTo(128);
        region.Y.ShouldBeLessThanOrEqualTo(272 - 32);
    }

    /// <summary>
    /// The sky patch is asked on the CROPPED frame, the one the render sees. A retained master keeps
    /// its zero canvas ring, and the region scan's 5 percent inner margin sits inside that ring on
    /// most of them; asked on the uncropped master every square failed the luma floor and the answer
    /// was the margin's corner, (153, 153) on a 3,000 px frame, a black patch of nothing, for every
    /// master in the store. The patch must land in covered sky, in full-canvas coordinates.
    /// </summary>
    [Fact]
    public void TheSkyPatchIsInsideTheCoveredAreaNotTheCanvasRing()
    {
        const int size = 1024;
        const int ring = 200;
        var plane = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var inRing = x < ring || y < ring || x >= size - ring || y >= size - ring;
                plane[y, x] = inRing ? 0f : 0.02f;
            }
        }
        var image = new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var path = Path.Combine(Path.GetTempPath(), $"tw-patch-{Guid.NewGuid():N}.fits");
        try
        {
            image.WriteToFitsFile(path);

            var (x, y) = DatasetMasterInventory.SkyPatchOf(image, path, patch: 320);

            x.ShouldBeGreaterThanOrEqualTo(ring, "the patch starts in covered sky, not in the ring");
            y.ShouldBeGreaterThanOrEqualTo(ring);
            (x + 320).ShouldBeLessThanOrEqualTo(size - ring, "and ends there too");
            (y + 320).ShouldBeLessThanOrEqualTo(size - ring);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Image Flat(float level)
    {
        var plane = new float[16, 16];
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                plane[y, x] = level;
            }
        }
        return new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
    }
}
