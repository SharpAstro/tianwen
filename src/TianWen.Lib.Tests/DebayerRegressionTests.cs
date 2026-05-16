using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Byte-level snapshot of <see cref="Image.DebayerAsync"/> output for each
/// algorithm on the real 3008x3008 IMX533M Bayer fixture (RGGB OSC, 60s
/// exposure of the Statue of Liberty Nebula -- the same one
/// <c>FindStarsBenchmarks</c> and <c>DebayerBenchmarks</c> use).
///
/// <para>Each test loads the fixture, runs one debayer algorithm, and asserts
/// the SHA-256 of the resulting channel data matches a pinned value. Any
/// change to the algorithm -- intentional (algorithm fix) or accidental
/// (perf regression that shifts FP order) -- changes the hash and trips the
/// test. If the change is intentional, re-run, copy the printed actual hash,
/// and update the expected value here with a one-line explanation.</para>
///
/// <para>This was the missing regression guard during the AHD perf work.
/// Without it a "pre-compute (luma, chroma)" rewrite could silently change
/// which pixels fall on which side of the homogeneity tie-break, producing
/// visually identical but bit-different output -- the kind of thing E2E
/// numeric tolerances would absorb.</para>
/// </summary>
[Collection("Imaging")]
public class DebayerRegressionTests
{
    private const string FixtureName = "2026-02-15_00-56-23__-5.00_60.00s_0058";

    [Fact]
    public async Task DebayerBilinearMono_OnIMX533Fixture_OutputHashStable()
        => await AssertDebayerHashAsync(
            DebayerAlgorithm.BilinearMono,
            // Pinned 2026-05-16 against the post-Parallel.For refactor build.
            expectedSha256: "495f78d47c8c2bd94b102f4376f73f8827d4e7335d097d4b744c2b5467bab05c");

    [Fact]
    public async Task DebayerVNG_OnIMX533Fixture_OutputHashStable()
        => await AssertDebayerHashAsync(
            DebayerAlgorithm.VNG,
            expectedSha256: "8d7521627b3dea214ffcd48e18c1547bde99e08c02dfd5b350b3a91bd1c67fc6");

    [Fact]
    public async Task DebayerAHD_OnIMX533Fixture_OutputHashStable()
        => await AssertDebayerHashAsync(
            DebayerAlgorithm.AHD,
            expectedSha256: "c0db123e76a930186385d6d7ab0b59478ccd6490e309a0782f0be1291c163a97");

    private static async Task AssertDebayerHashAsync(DebayerAlgorithm algo, string expectedSha256)
    {
        var raw = await SharedTestData.ExtractGZippedFitsImageAsync(FixtureName, isReadOnly: false);
        var debayered = await raw.DebayerAsync(algo);
        var hash = HashChannelData(debayered);
        hash.ShouldBe(expectedSha256, $"{algo} debayer output changed -- update expectedSha256 here if intentional. " +
            $"Actual hash: {hash}");
    }

    private static string HashChannelData(Image image)
    {
        // Hash every channel's raw float bytes in order so the hash is
        // sensitive to: pixel values, channel ordering, channel count, dims.
        using var sha = SHA256.Create();
        var (channels, width, height) = image.Shape;
        Span<byte> dimBytes = stackalloc byte[12];
        BitConverter.TryWriteBytes(dimBytes[0..4], channels);
        BitConverter.TryWriteBytes(dimBytes[4..8], height);
        BitConverter.TryWriteBytes(dimBytes[8..12], width);
        sha.TransformBlock(dimBytes.ToArray(), 0, dimBytes.Length, null, 0);

        var pool = ArrayPool<byte>.Shared;
        for (var ch = 0; ch < channels; ch++)
        {
            var bytes = MemoryMarshal.AsBytes(image.GetChannelSpan(ch));
            // SHA256.TransformBlock needs byte[]; copy via ArrayPool to avoid
            // a 100+ MB heap allocation per channel.
            var buf = pool.Rent(bytes.Length);
            try
            {
                bytes.CopyTo(buf);
                sha.TransformBlock(buf, 0, bytes.Length, null, 0);
            }
            finally
            {
                pool.Return(buf);
            }
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}
