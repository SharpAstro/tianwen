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
    /// <remarks>
    /// <b>Every channel is uploaded, because <c>ChannelDeviceBytes</c> is a sum over all three.</b>
    /// Uploading only channel 0 leaves the other two holding whatever the previous test in this shared
    /// fixture left there, and a document-sized leftover swamps a 256x256 read -- measured at ratio
    /// 0.969 when a 2048x1536 case ran first, which reads as the format change having done nothing.
    /// It passed for a long time only because nothing before it had uploaded anything big, so this was
    /// order-dependence rather than a measurement.
    /// </remarks>
    [Fact]
    public void AnEightBitChannelCostsAboutAQuarterOfTheDeviceMemory()
    {
        if (!_gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({_gpu.UnavailableReason})");
            return;
        }

        const int W = 256, H = 256;
        const int Channels = 3;
        var floats = new float[W * H];
        var bytes = new byte[W * H];

        var (floatBytes, unormBytes) = _gpu.Invoke(() =>
        {
            var pipeline = _gpu.Pipeline!;
            for (var c = 0; c < Channels; c++) { pipeline.UploadChannelTexture(floats, c, W, H); }
            var asFloat = pipeline.ChannelDeviceBytes;
            for (var c = 0; c < Channels; c++) { pipeline.UploadChannelTexture(bytes, c, W, H); }
            var asUnorm = pipeline.ChannelDeviceBytes;
            return (asFloat, asUnorm);
        });

        _output.WriteLine($"{W}x{H} x{Channels}ch: R32Sfloat = {floatBytes} B, R8Unorm = {unormBytes} B, " +
            $"ratio {unormBytes / (double)floatBytes:F3}");

        floatBytes.ShouldBeGreaterThan(0L);
        unormBytes.ShouldBeGreaterThan(0L);
        // A quarter, with room for the driver's alignment granularity at this size.
        (unormBytes / (double)floatBytes).ShouldBeLessThan(0.30);
        (unormBytes / (double)floatBytes).ShouldBeGreaterThan(0.20);
    }

    /// <summary>
    /// D3' step 4: the NET figure, which is the only one that says whether the change pays. Holding
    /// the raster costs host memory and uploading it saves device memory, so the two halves move in
    /// opposite directions and neither alone is the answer.
    /// </summary>
    /// <remarks>
    /// <para><b>Composed from two facts, not one heroic probe.</b> The DEVICE half is measured here,
    /// every channel at once, from the driver's own reported requirement so alignment padding is
    /// included. The HELD half is the size of the very array that was uploaded, which is what "retain
    /// the source raster" means; that a real 8-bit FILE yields exactly <c>w * h</c> bytes per channel
    /// is pinned on a real file by <c>ViewerByteTextureUploadTests</c>. Reading a file here as well
    /// would re-test the importer and the memory claim no better.</para>
    /// <para><b>Only the DELTA is used</b>, never either total. The fixture is shared across this
    /// class, so a channel this case does not upload still holds whatever the previous test left in
    /// it -- unchanged between the two reads, and therefore cancelled.</para>
    /// <para>Neither half is visible to working set: a <c>VkImage</c> is invisible to the GC, and
    /// run-to-run variance on a large document exceeds anything this change delivers (M2 in the plan
    /// established that the hard way).</para>
    /// </remarks>
    [Theory]
    [InlineData(1)]   // mono: the plan's -2 B/px
    [InlineData(3)]   // RGB: the plan's -6 B/px, and the case that motivated D3'
    public void AnEightBitDocumentPaysTwoFewerBytesPerPixelPerChannel(int channels)
    {
        if (!_gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({_gpu.UnavailableReason})");
            return;
        }

        // Document-sized rather than tile-sized: alignment padding is a smaller share of a big
        // texture, so measuring on a 256x256 would flatter the result.
        const int W = 2048, H = 1536;
        var px = (double)W * H;

        var floats = new float[W * H];
        var bytes = new byte[W * H];

        var (deviceFloat, deviceByte) = _gpu.Invoke(() =>
        {
            var pipeline = _gpu.Pipeline!;
            for (var c = 0; c < channels; c++) { pipeline.UploadChannelTexture(floats, c, W, H); }
            var asFloat = pipeline.ChannelDeviceBytes;
            for (var c = 0; c < channels; c++) { pipeline.UploadChannelTexture(bytes, c, W, H); }
            var asByte = pipeline.ChannelDeviceBytes;
            return (asFloat, asByte);
        });

        // Negative: a saving. The held cost is the uploaded array itself, per channel.
        var devicePerPx = (deviceByte - deviceFloat) / px;
        var heldPerPx = channels * (bytes.Length / px);
        var netPerPx = heldPerPx + devicePerPx;

        _output.WriteLine($"{W}x{H} x{channels}ch: device {deviceFloat} B -> {deviceByte} B "
            + $"({devicePerPx:F2} B/px), held +{heldPerPx:F2} B/px, NET {netPerPx:F2} B/px");

        // 4 B/px of float becomes 1 of UNORM, so 3 per channel, less any alignment granularity.
        devicePerPx.ShouldBeLessThan(-2.8 * channels);
        netPerPx.ShouldBeLessThan(-1.8 * channels);
    }

    /// <summary>
    /// A view that stops sampling a channel must give the texture back. Nothing else shrinks them: a
    /// channel texture is destroyed only when re-uploaded at a different geometry or format, so before
    /// this, pressing C on a 3-plane master held two full-size textures for a view that samples one,
    /// and stepping from a master to a mono sub in the same folder held them across documents.
    /// </summary>
    /// <remarks>
    /// The readback is the half that matters most. Freeing device memory is easy to do while freeing
    /// the WRONG slot, and the symptom of that -- the live channel sampling a 1x1 placeholder -- is a
    /// uniformly flat image, which looks like a stretch bug rather than a lifetime bug.
    /// </remarks>
    [Fact]
    public void AViewThatStopsSamplingAChannelGivesTheTextureBack()
    {
        if (!_gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({_gpu.UnavailableReason})");
            return;
        }

        const int W = 512, H = 512;
        var floats = new float[W * H];
        for (var i = 0; i < floats.Length; i++) { floats[i] = (i % 251) / 251f; }

        var (three, afterRelease, afterAgain, afterNoop, probe) = _gpu.Invoke(() =>
        {
            var pipeline = _gpu.Pipeline!;
            for (var c = 0; c < 3; c++) { pipeline.UploadChannelTexture(floats, c, W, H); }
            var all = pipeline.ChannelDeviceBytes;

            // The single-channel view: slot 0 populated, slots 1 and 2 no longer sampled.
            pipeline.ReleaseChannelTexturesFrom(1);
            var released = pipeline.ChannelDeviceBytes;

            // Idempotent, because the upload pass runs per document and would otherwise re-destroy and
            // re-create two placeholders on every one.
            pipeline.ReleaseChannelTexturesFrom(1);
            var again = pipeline.ChannelDeviceBytes;

            // Nothing to release when every slot is live.
            pipeline.ReleaseChannelTexturesFrom(3);
            var noop = pipeline.ChannelDeviceBytes;

            var read = new float[8];
            pipeline.ReadbackChannelFirstFloats(0, read);
            return (all, released, again, noop, read);
        });

        _output.WriteLine($"{W}x{H} x3ch: {three} B -> {afterRelease} B after dropping to one sampled "
            + $"slot ({afterRelease / (double)three:F3})");

        // Two of three freed, so about a third remains plus two 1x1 placeholders.
        (afterRelease / (double)three).ShouldBeLessThan(0.40);
        afterAgain.ShouldBe(afterRelease, "a second release must not churn the placeholders");
        afterNoop.ShouldBe(afterRelease, "releasing from the live count must do nothing");

        for (var i = 0; i < probe.Length; i++)
        {
            probe[i].ShouldBe(floats[i], 1e-6f, $"live channel texel {i} must survive the release");
        }
    }
}
