using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The other stack of a <see cref="LiveStackPreviewSource"/> (#1883, Live | Full): placed so its disk lands on this one's even when it is
/// another size (a Full master is cropped to where its frames reached), re-published as it is sharpened, and built only when wanted.
/// </summary>
public class LiveStackPeerTests
{
    // A smooth disk of radius 9 on a faint sky in a width x height frame, its centre at (cx, cy).
    private static Image Disk(int width, int height, double cx, double cy)
    {
        var plane = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var r = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                var edge = Math.Clamp((9.0 + 1.5 - r) / 3.0, 0, 1);
                plane[y, x] = (float)(0.02 + (0.7 * edge * edge * (3 - (2 * edge))) * (1 - (0.25 * Math.Min(1, r / 9))));
            }
        }

        return new Image([plane], BitDepth.Float32, 1f, 0f, 0f,
            new ImageMeta("test", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), FrameType.Light, "",
                0f, 0f, -1, FocuserPositions.None, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN, ObjectName: "Jupiter"));
    }

    private static async Task<LiveStackPreviewSource> PublishedAsync(Image master, string name)
    {
        var source = new LiveStackPreviewSource(new FixedMaster(master), name, new SystemTimeProvider());
        source.RequestFollow(0);
        while (!source.TryPublishMaster())
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        return source;
    }

    [Fact(Timeout = 60_000)]
    public async Task TheOtherStacksDiskLandsOnThisOnesAcrossASizeChangeAndIsBuiltOnlyWhenWanted()
    {
        var ct = TestContext.Current.CancellationToken;
        // Live: the whole 64 x 64 frame, the disk at (32, 30). Full: cropped to 48 x 44, the same planet at (20, 18).
        await using var live = await PublishedAsync(Disk(64, 64, 32, 30), "live");
        await using var full = await PublishedAsync(Disk(48, 44, 20, 18), "full");
        live.AttachPeer(full, known: true, "Live", "Full");

        live.PeerKnown.ShouldBeTrue();
        live.CanShowPeer.ShouldBeTrue();
        live.Peer.ShouldBeNull("not wanted: nothing is placed or held");
        live.PeerWanted.ShouldBeFalse();

        while (live.Peer is null)
        {
            live.WantPeer(true);
            await Task.Delay(5, ct);
        }

        live.PeerWanted.ShouldBeTrue();
        live.Peer.ShouldBeSameAs(full);
        live.PeerOffset.X.ShouldBe(12f, 1f, "the Full master is drawn where its disk coincides with the Live one: 32 - 20");
        live.PeerOffset.Y.ShouldBe(12f, 1f, "30 - 18");
        var generation = live.PeerGeneration;

        // The same pair is not placed again, and the generation holds still until the peer publishes.
        live.WantPeer(true);
        await Task.Delay(30, ct);
        live.WantPeer(true);
        live.PeerGeneration.ShouldBe(generation);

        // A re-sharpened peer is a new image to upload, under the same placement.
        full.SetSharpen(WaveletSharpenOptions.Uniform(6, 1.5f));
        while (!full.TryPublishMaster())
        {
            await Task.Delay(5, ct);
        }
        live.WantPeer(true);
        live.PeerGeneration.ShouldBeGreaterThan(generation);
        live.PeerOffset.X.ShouldBe(12f, 1f);

        live.WantPeer(false);
        live.Peer.ShouldBeNull("let go with the comparison");
    }

    [Fact]
    public void AViewWithNoOtherStackKnowsNoPeerAndAFileBesideTheSerCountsAsKnown()
    {
        using var alone = new LiveStackPreviewSource(new NodeMasters(), "alone", new SystemTimeProvider());
        alone.PeerKnown.ShouldBeFalse();
        alone.CanShowPeer.ShouldBeFalse();

        // The Full master is beside the SER but not read yet: offered, with nothing to draw until it is.
        alone.AttachPeer(null, known: true, "Live", "Full");
        alone.PeerKnown.ShouldBeTrue();
        alone.CanShowPeer.ShouldBeFalse();
    }
}
