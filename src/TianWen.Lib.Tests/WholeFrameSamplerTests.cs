using Shouldly;
using System;
using System.Linq;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="WholeFrameSampler"/>, the reduction the gradient exporter and runner share
/// (docs/plans/gradient-remover-training.md, section 3 "Edges"): the placement, the partition, and that
/// an absent edge never enters a mean.
/// </summary>
[Collection("Imaging")]
public class WholeFrameSamplerTests
{
    [Fact]
    public void AWideFrameFillsTheSquareAlongItsLongSideAndIsCentredAcrossItsShortOne()
    {
        var p = WholeFramePlacement.For(4000, 2000, 256);
        p.FrameWidth.ShouldBe(256);
        p.FrameHeight.ShouldBe(128);
        p.OffsetX.ShouldBe(0);
        p.OffsetY.ShouldBe(64);
        p.SourcePixelsPerSample.ShouldBe(4000.0 / 256, 1e-12);

        // The pad has no source; the frame's boxes partition it exactly.
        p.SourceRows(0).ShouldBe((0, 0));
        p.SourceRows(63).ShouldBe((0, 0));
        p.SourceRows(64).Start.ShouldBe(0);
        p.SourceRows(191).End.ShouldBe(2000);
        p.SourceRows(192).ShouldBe((0, 0));
        Enumerable.Range(0, 256).Sum(x => p.SourceColumns(x).End - p.SourceColumns(x).Start).ShouldBe(4000);
        Enumerable.Range(0, 256).Sum(y => p.SourceRows(y).End - p.SourceRows(y).Start).ShouldBe(2000);
        for (var x = 1; x < 256; x++)
        {
            p.SourceColumns(x).Start.ShouldBe(p.SourceColumns(x - 1).End);
        }

        var tall = WholeFramePlacement.For(2000, 4000, 256);
        (tall.FrameWidth, tall.FrameHeight, tall.OffsetX, tall.OffsetY).ShouldBe((128, 256, 64, 0));
    }

    [Fact]
    public void AnAbsentRingNeverPullsTheEdgeSamplesTowardZero()
    {
        // A flat sky at 0.25 on a 640 x 480 canvas with a 7 px exact-zero ring, as the integrator writes
        // where no frame landed. A mean that took the ring in reads the edge samples low: GraXpert's failure.
        const int w = 640, h = 480, ring = 7;
        var plane = new float[h, w];
        for (var y = ring; y < h - ring; y++)
        {
            for (var x = ring; x < w - ring; x++)
            {
                plane[y, x] = 0.25f;
            }
        }
        var image = new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Monochrome });
        var absent = image.AbsentPixels();
        absent.ShouldNotBeNull();
        var placement = WholeFramePlacement.For(w, h, 64);
        var sample = WholeFrameSampler.Sample(image, absent, placement);

        var size = placement.Size;
        var present = Enumerable.Range(0, size * size).Where(i => sample.Presence[i] > 0).ToArray();
        present.ShouldNotBeEmpty();
        foreach (var i in present)
        {
            sample.Planes[0][i].ShouldBe(0.25f, 1e-6f);
        }

        // The first row of the frame is 10 px of source over a 7 px ring: presence 3/10 there, and full
        // in the interior; the pad above the frame is absent.
        var firstRow = placement.OffsetY;
        var middle = placement.OffsetX + placement.FrameWidth / 2;
        sample.Presence[firstRow * size + middle].ShouldBe(0.3f, 1e-6f);
        sample.Presence[(size / 2) * size + middle].ShouldBe(1f);
        sample.Presence[(firstRow - 1) * size + middle].ShouldBe(0f);
        float.IsNaN(sample.Planes[0][(firstRow - 1) * size + middle]).ShouldBeTrue();

        // Without the mask the same edge sample reads 0.3 x 0.25: the bias the mask exists to remove.
        var naive = WholeFrameSampler.Sample(image, absent: null, placement);
        naive.Planes[0][firstRow * size + middle].ShouldBe(0.075f, 1e-6f);
        image.Release();
    }

    [Fact]
    public void ARampIsSampledAtEachBoxsCentreAndEveryChannelKeepsItsOwnLevel()
    {
        const int w = 512, h = 256;
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[h, w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    planes[c][y, x] = 0.1f * (c + 1) + 1e-4f * x + 2e-4f * y;
                }
            }
        }
        var image = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });
        var placement = WholeFramePlacement.For(w, h, 64);
        var sample = WholeFrameSampler.Sample(image, absent: null, placement);

        for (var y = placement.OffsetY; y < placement.OffsetY + placement.FrameHeight; y++)
        {
            var (r0, r1) = placement.SourceRows(y);
            for (var x = 0; x < placement.FrameWidth; x++)
            {
                var (c0, c1) = placement.SourceColumns(x + placement.OffsetX);
                var cx = (c0 + c1 - 1) / 2.0;
                var cy = (r0 + r1 - 1) / 2.0;
                for (var c = 0; c < 3; c++)
                {
                    sample.Planes[c][y * 64 + x + placement.OffsetX].ShouldBe((float)(0.1 * (c + 1) + 1e-4 * cx + 2e-4 * cy), 1e-5f);
                }
            }
        }
        image.Release();
    }

    [Fact]
    public void ThePlacementMustBeTheImagesOwn()
    {
        var image = new Image([new float[10, 20]], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Monochrome });
        Should.Throw<ArgumentException>(() => WholeFrameSampler.Sample(image, null, WholeFramePlacement.For(21, 10, 8)));
        image.Release();
    }
}
