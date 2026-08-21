using System;
using Shouldly;
using TianWen.UI.Shared;
using Vortice.Vulkan;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// D3' of <c>docs/plans/viewer-memory-footprint.md</c>: a document whose SOURCE was 8-bit uploads its
/// original bytes as <see cref="VkFormat.R8Unorm"/> instead of re-quantised floats -- a quarter of the
/// device memory for the same picture, and lossless with respect to the file.
/// </summary>
/// <remarks>
/// <para>The claim that makes this a format change rather than a pipeline change is that a UNORM
/// sampler returns [0,1], exactly the range the float path uploads. These tests check that claim
/// end to end through the driver rather than trusting the spec reading.</para>
/// <para>Skips when Vulkan is unavailable, like the other GPU suites, so a driverless CI stays
/// green.</para>
/// </remarks>
// Same collection as the other OffscreenGpuFixture consumers: it is what serialises them, and
// repeated Vulkan init/destroy across concurrent classes is what makes the runtime SIGSEGV at exit.
[Collection("Imaging")]
public sealed class GpuChannelFormatTests : IClassFixture<OffscreenGpuFixture>
{
    private readonly OffscreenGpuFixture _gpu;
    private readonly ITestOutputHelper _output;

    public GpuChannelFormatTests(OffscreenGpuFixture gpu, ITestOutputHelper output)
    {
        _gpu = gpu;
        _output = output;
    }

    [Fact]
    public void AnEightBitChannelReadsBackAsTheSameUnitFloatsAFloatChannelWouldGive()
    {
        if (!_gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({_gpu.UnavailableReason})");
            return;
        }

        const int W = 64, H = 64;
        var bytes = new byte[W * H];
        for (var i = 0; i < bytes.Length; i++) { bytes[i] = (byte)(i & 0xFF); }

        var probed = _gpu.Invoke(() =>
        {
            var pipeline = _gpu.Pipeline!;
            pipeline.UploadChannelTexture(bytes, 0, W, H);
            var probe = new float[8];
            pipeline.ReadbackChannelFirstFloats(0, probe);
            return probe;
        });

        for (var i = 0; i < probed.Length; i++)
        {
            probed[i].ShouldBe(bytes[i] / 255f, 1e-6f, $"texel {i}");
        }
    }

    /// <summary>
    /// The failure this guards is silent and looks like a stretch bug. A texture cannot change texel
    /// format in place, so an 8-bit upload at the SAME dimensions as a preceding float upload must
    /// still reallocate. Without the format term in the recreate condition the copy reinterprets the
    /// new bytes through the old format and draws garbage at exactly the right size.
    /// </summary>
    [Fact]
    public void SwitchingFormatAtIdenticalDimensionsStillRecreatesTheTexture()
    {
        if (!_gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({_gpu.UnavailableReason})");
            return;
        }

        const int W = 64, H = 64;
        var floats = new float[W * H];
        for (var i = 0; i < floats.Length; i++) { floats[i] = 0.75f; }
        var bytes = new byte[W * H];
        for (var i = 0; i < bytes.Length; i++) { bytes[i] = 32; }

        var (afterFloat, afterBytes, backToFloat) = _gpu.Invoke(() =>
        {
            var pipeline = _gpu.Pipeline!;
            var probe = new float[4];

            pipeline.UploadChannelTexture(floats, 0, W, H);
            pipeline.ReadbackChannelFirstFloats(0, probe);
            var one = probe[0];

            // Same geometry, different format.
            pipeline.UploadChannelTexture(bytes, 0, W, H);
            pipeline.ReadbackChannelFirstFloats(0, probe);
            var two = probe[0];

            // And back, because the recreate has to work in both directions.
            pipeline.UploadChannelTexture(floats, 0, W, H);
            pipeline.ReadbackChannelFirstFloats(0, probe);
            var three = probe[0];

            return (one, two, three);
        });

        afterFloat.ShouldBe(0.75f, 1e-6f);
        afterBytes.ShouldBe(32 / 255f, 1e-6f, "the 8-bit upload must be read through R8Unorm, not the old float format");
        backToFloat.ShouldBe(0.75f, 1e-6f, "and switching back must recreate again");
    }

    /// <summary>
    /// The point of D3': the device pays a quarter. Asserted against the driver's own reported
    /// requirement rather than a computed <c>w * h</c>, so alignment padding is included -- that is
    /// what the memory claim in the plan is actually about.
    /// </summary>
    [Fact]
    public void AnEightBitChannelCostsAboutAQuarterOfTheDeviceMemory()
    {
        if (!_gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({_gpu.UnavailableReason})");
            return;
        }

        const int W = 256, H = 256;
        var floats = new float[W * H];
        var bytes = new byte[W * H];

        var (floatBytes, unormBytes) = _gpu.Invoke(() =>
        {
            var pipeline = _gpu.Pipeline!;
            pipeline.UploadChannelTexture(floats, 0, W, H);
            var asFloat = pipeline.ChannelDeviceBytes;
            pipeline.UploadChannelTexture(bytes, 0, W, H);
            var asUnorm = pipeline.ChannelDeviceBytes;
            return (asFloat, asUnorm);
        });

        _output.WriteLine($"{W}x{H}: R32Sfloat = {floatBytes} B, R8Unorm = {unormBytes} B, " +
            $"ratio {unormBytes / (double)floatBytes:F3}");

        floatBytes.ShouldBeGreaterThan(0L);
        unormBytes.ShouldBeGreaterThan(0L);
        // A quarter, with room for the driver's alignment granularity at this size.
        (unormBytes / (double)floatBytes).ShouldBeLessThan(0.30);
        (unormBytes / (double)floatBytes).ShouldBeGreaterThan(0.20);
    }
}
