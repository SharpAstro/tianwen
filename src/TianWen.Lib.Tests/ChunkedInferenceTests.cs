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
