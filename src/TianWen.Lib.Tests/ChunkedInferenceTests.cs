using System;
using System.Linq;
using Shouldly;
using TianWen.AI.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class ChunkedInferenceTests
{
    [Fact]
    public void Split_ProducesCorrectChunksForExactGrid()
    {
        // 64x64 plane, 32-pixel chunks, 8-pixel overlap -> step = 24.
        // First chunk at (0,0) is 32x32. Second at (0,24) is 32x32. (0,48) is 16x32 (edge).
        // Same pattern vertically. Total chunks: 3 * 3 = 9.
        var w = 64;
        var h = 64;
        var plane = Enumerable.Range(0, w * h).Select(i => (float)i).ToArray();

        var chunks = ChunkedInference.Split(plane, w, h, chunkSize: 32, overlap: 8);
        chunks.Length.ShouldBe(9);

        // First chunk: top-left, 32x32, IsEdge (touches y=0 + x=0).
        var first = chunks[0];
        first.X.ShouldBe(0);
        first.Y.ShouldBe(0);
        first.Width.ShouldBe(32);
        first.Height.ShouldBe(32);
        first.IsEdge.ShouldBeTrue();
        first.Data.Length.ShouldBe(32 * 32);
        first.Data[0].ShouldBe(0f);                // plane[0,0]
        first.Data[31].ShouldBe(31f);              // plane[0,31]
        first.Data[32].ShouldBe((float)w);         // plane[1,0]
    }

    [Fact]
    public void Split_RoundTripsThroughStitchWhenInferenceIsIdentity()
    {
        // Splitting then stitching with the same chunk data (identity inference)
        // must reproduce the original plane within the inner stitched region.
        // We add a border first so the stitched output covers the full source.
        //
        // Geometry constraint: for the inner regions of successive chunks to be
        // contiguous (no coverage gap), overlap >= 2 * borderSize. Here
        // chunkSize=64, overlap=32 -> step=32; inner length per chunk = 64 - 2*16
        // = 32 = step, so successive inners abut exactly.
        const int w = 80, h = 60, border = 16;
        var src = Enumerable.Range(0, w * h).Select(i => (float)i).ToArray();
        var padded = ChunkedInference.AddBorder(src, w, h, border,
            out var paddedW, out var paddedH);

        var chunks = ChunkedInference.Split(padded, paddedW, paddedH, chunkSize: 64, overlap: 32);
        chunks.Length.ShouldBeGreaterThan(0);

        var stitched = new float[paddedW * paddedH];
        ChunkedInference.Stitch(chunks, stitched, paddedW, paddedH, borderSize: border);

        var unpadded = ChunkedInference.RemoveBorder(stitched, paddedW, paddedH, border);
        unpadded.Length.ShouldBe(w * h);

        // Inner region must match exactly (identity inference).
        for (var i = 0; i < src.Length; i++)
        {
            unpadded[i].ShouldBe(src[i], 1e-3f, $"@index {i}");
        }
    }

    [Fact]
    public void Stitch_AveragesOverlappingChunks()
    {
        // Two synthetic chunks of identical 16x16 shape placed at (0,0) and (8,0)
        // with values 10 and 30. The 8-column overlap in the middle should average
        // to 20.
        const int border = 0;       // skip border drop for this isolated test
        const int w = 24, h = 16;

        var c1Data = new float[16 * 16];
        c1Data.AsSpan().Fill(10f);
        var c2Data = new float[16 * 16];
        c2Data.AsSpan().Fill(30f);

        var chunks = new[]
        {
            new ChunkedInference.Chunk(c1Data, X: 0, Y: 0, Width: 16, Height: 16, IsEdge: true),
            new ChunkedInference.Chunk(c2Data, X: 8, Y: 0, Width: 16, Height: 16, IsEdge: true),
        };

        var dest = new float[w * h];
        ChunkedInference.Stitch(chunks, dest, w, h, borderSize: border);

        // Left third (x in [0, 8)): only chunk 1 -> value 10.
        for (var x = 0; x < 8; x++) dest[x].ShouldBe(10f);
        // Middle (x in [8, 16)): both chunks -> averaged to 20.
        for (var x = 8; x < 16; x++) dest[x].ShouldBe(20f);
        // Right (x in [16, 24)): only chunk 2 -> value 30.
        for (var x = 16; x < 24; x++) dest[x].ShouldBe(30f);
    }

    /// <summary>
    /// The regression guard for the N2N tile seams. Neighbouring chunks that disagree by a
    /// CONSTANT are not hypothetical: <c>N2nLinearRunner.RestoreLevel</c> gives every chunk its own
    /// median-matching offset by design, so a disagreement between neighbours is guaranteed.
    /// Unweighted averaging renders it as two hard edges of D/2 at the shared band boundaries; a
    /// feathered blend must spread it across the band instead.
    /// </summary>
    [Fact]
    public void Stitch_WithFeather_TurnsAConstantChunkDisagreementIntoARampInsteadOfTwoEdges()
    {
        // The shipped N2N ratios at quarter scale: chunk 256 / border 16 / overlap 64 -> 64 / 4 / 16.
        const int chunkSize = 64, border = 4, overlap = 16;
        const int stride = chunkSize - overlap;          // 48
        const int retained = chunkSize - 2 * border;     // 56
        const int sharedBand = retained - stride;        // 8
        // Height must exceed 2 * border, or the border drop leaves no rows and Stitch skips
        // the chunk outright -- which silently yields an all-zero plane to assert against.
        const int height = 24;
        const int sampledRow = height / 2;
        const float disagreement = 0.10f;
        sharedBand.ShouldBe(overlap - 2 * border);

        const int chunkCount = 4;
        var width = (chunkCount - 1) * stride + chunkSize - border;

        // Each chunk is internally FLAT, and neighbours alternate between two levels, so whatever
        // appears at a join is attributable to the stitch weights and nothing else.
        var chunks = new ChunkedInference.Chunk[chunkCount];
        for (var i = 0; i < chunkCount; i++)
        {
            var data = new float[chunkSize * height];
            data.AsSpan().Fill(i % 2 == 0 ? 1.0f : 1.0f + disagreement);
            chunks[i] = new ChunkedInference.Chunk(data, X: i * stride, Y: 0,
                Width: chunkSize, Height: height, IsEdge: i == 0 || i == chunkCount - 1);
        }

        var boxed = new float[width * height];
        ChunkedInference.Stitch(chunks, boxed, width, height, borderSize: border);

        var feathered = new float[width * height];
        ChunkedInference.Stitch(chunks, feathered, width, height, borderSize: border, featherPx: sharedBand);

        // First shared band, in destination coordinates.
        var bandStart = stride + border;
        var bandEnd = chunkSize - border;
        (bandEnd - bandStart).ShouldBe(sharedBand);

        var boxStep = MaxAdjacentStep(boxed, width, sampledRow, bandStart - 2, bandEnd + 2);
        var featherStep = MaxAdjacentStep(feathered, width, sampledRow, bandStart - 2, bandEnd + 2);

        // The unweighted average steps by half the disagreement: the defect being guarded against.
        boxStep.ShouldBeGreaterThan(disagreement * 0.4f);
        // Feathered, no single column may carry anything like that share of it.
        featherStep.ShouldBeLessThan(boxStep * 0.4f);

        // ... and the crossing must be monotone, which is what "ramp, not edge" actually means.
        var row = sampledRow * width;
        for (var x = bandStart + 1; x < bandEnd; x++)
        {
            feathered[row + x].ShouldBeGreaterThanOrEqualTo(feathered[row + x - 1] - 1e-6f);
        }
    }

    /// <summary>
    /// Feathering must not cost reconstruction: the divide by accumulated weight is what makes a
    /// constant field exact, including at an image edge where a ramp has no neighbour to complete
    /// it. Without that, edges darken toward the border by the ramp weight.
    /// </summary>
    [Fact]
    public void Stitch_WithFeather_StillReconstructsAConstantFieldExactly()
    {
        const int w = 80, h = 60, border = 16;
        const float value = 0.375f;
        var src = new float[w * h];
        src.AsSpan().Fill(value);
        var padded = ChunkedInference.AddBorder(src, w, h, border, out var paddedW, out var paddedH);

        var chunks = ChunkedInference.Split(padded, paddedW, paddedH, chunkSize: 64, overlap: 48);
        var stitched = new float[paddedW * paddedH];
        ChunkedInference.Stitch(chunks, stitched, paddedW, paddedH, borderSize: border, featherPx: 48 - 2 * border);

        var unpadded = ChunkedInference.RemoveBorder(stitched, paddedW, paddedH, border);
        for (var i = 0; i < unpadded.Length; i++)
        {
            unpadded[i].ShouldBe(value, 1e-5f, $"@index {i}");
        }
    }

    /// <summary>
    /// featherPx = 0 must remain the byte-for-byte unweighted mean, because the AI4 NAFNet path is
    /// a port pinned against SAS Pro and does not opt in.
    /// </summary>
    [Fact]
    public void Stitch_DefaultsToTheUnweightedMeanSoTheNafnetPortIsUnchanged()
    {
        const int w = 24, h = 16;
        var c1 = new float[16 * 16];
        c1.AsSpan().Fill(10f);
        var c2 = new float[16 * 16];
        c2.AsSpan().Fill(30f);
        var chunks = new[]
        {
            new ChunkedInference.Chunk(c1, X: 0, Y: 0, Width: 16, Height: 16, IsEdge: true),
            new ChunkedInference.Chunk(c2, X: 8, Y: 0, Width: 16, Height: 16, IsEdge: true),
        };

        var explicitZero = new float[w * h];
        ChunkedInference.Stitch(chunks, explicitZero, w, h, borderSize: 0, featherPx: 0);
        var defaulted = new float[w * h];
        ChunkedInference.Stitch(chunks, defaulted, w, h, borderSize: 0);

        defaulted.AsSpan().SequenceEqual(explicitZero).ShouldBeTrue();
        for (var x = 8; x < 16; x++) defaulted[x].ShouldBe(20f);
    }

    private const float Disagreement = 0.10f;

    private static float MaxAdjacentStep(float[] plane, int width, int row, int from, int to)
    {
        var offset = row * width;
        var worst = 0f;
        for (var x = Math.Max(1, from); x < Math.Min(width, to); x++)
        {
            worst = Math.Max(worst, Math.Abs(plane[offset + x] - plane[offset + x - 1]));
        }
        return worst;
    }

    [Fact]
    public void AddBorder_FillsWithMedian()
    {
        // Plane has a few outliers; median should be the dominant value.
        var src = new float[5 * 5];
        src.AsSpan().Fill(7f);
        src[0] = 1f;                 // outlier
        src[24] = 99f;               // outlier

        var padded = ChunkedInference.AddBorder(src, 5, 5, borderSize: 2,
            out var paddedW, out var paddedH);
        paddedW.ShouldBe(9);
        paddedH.ShouldBe(9);
        padded.Length.ShouldBe(9 * 9);

        // Border cells should be the median = 7.
        padded[0].ShouldBe(7f);                          // top-left corner
        padded[8].ShouldBe(7f);                          // top-right corner
        padded[2 * paddedW + 0].ShouldBe(7f);            // mid-left edge column
        padded[2 * paddedW + 8].ShouldBe(7f);            // mid-right edge column
        // Inner region should reproduce src.
        padded[2 * paddedW + 2].ShouldBe(1f);            // src[0,0]
        padded[6 * paddedW + 6].ShouldBe(99f);           // src[4,4]
    }

    [Fact]
    public void RemoveBorder_IsInverseOfAddBorder()
    {
        const int w = 8, h = 6, border = 3;
        var src = Enumerable.Range(0, w * h).Select(i => (float)i).ToArray();

        var padded = ChunkedInference.AddBorder(src, w, h, border,
            out var paddedW, out var paddedH);
        var unpadded = ChunkedInference.RemoveBorder(padded, paddedW, paddedH, border);

        unpadded.Length.ShouldBe(src.Length);
        for (var i = 0; i < src.Length; i++) unpadded[i].ShouldBe(src[i]);
    }
}
