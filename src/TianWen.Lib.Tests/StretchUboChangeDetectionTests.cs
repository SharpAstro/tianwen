using System;
using Shouldly;
using TianWen.UI.Shared;
using Vortice.Vulkan;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The stretch UBO is the image shader's COMPLETE input, so comparing the bytes just written against
    /// the previous write answers "would this draw produce the same pixels?" exactly. That is what makes
    /// a cached image layer safe to reuse.
    /// </summary>
    /// <remarks>
    /// <para>The alternative -- a cache key listing the state that matters -- is what this exists to
    /// avoid. Such a key is correct only until someone adds a uniform, and then it is silently wrong in
    /// the worst possible way: a stale picture on screen, with nothing in the diff that looks like a
    /// caching change. Deriving the answer from the bytes cannot go out of date, because the bytes ARE
    /// the contract.</para>
    /// <para>The decisive test is the last one. It changes a field near the END of the 416-byte block, so
    /// a comparison that only covered a prefix -- the shape a hand-rolled or lazily-optimised version
    /// would take -- passes every other test here and fails that one.</para>
    /// </remarks>
    [Collection("Imaging")]
    public sealed class StretchUboChangeDetectionTests : IClassFixture<OffscreenGpuFixture>
    {
        private readonly OffscreenGpuFixture _gpu;

        public StretchUboChangeDetectionTests(OffscreenGpuFixture gpu) => _gpu = gpu;

        [Fact]
        public void TheFirstWriteIsAlwaysAChange()
        {
            if (Skip(out var reason)) { Assert.Skip(reason); return; }

            // An untouched shadow is all zeroes, so a UBO that happened to be all zeroes could otherwise
            // be mistaken for "same as last time" on the very first draw -- and the layer would be
            // blitted before anything had ever been rendered into it.
            var changed = _gpu.Invoke(() =>
            {
                var p = _gpu.Pipeline!;
                Write(p, slot: 1);
                return p.StretchUboChanged(1);
            });

            changed.ShouldBeTrue();
        }

        [Fact]
        public void WritingTheSameValuesTwiceIsNotAChange()
        {
            if (Skip(out var reason)) { Assert.Skip(reason); return; }

            var (first, second) = _gpu.Invoke(() =>
            {
                var p = _gpu.Pipeline!;
                Write(p);
                var a = p.StretchUboChanged();
                Write(p);
                var b = p.StretchUboChanged();
                return (a, b);
            });

            first.ShouldBeTrue("the slot had not been written in this shape before");
            second.ShouldBeFalse("identical uniforms must not read as a change, or nothing is ever reusable");
        }

        [Fact]
        public void ASingleAlteredValueIsAChange()
        {
            if (Skip(out var reason)) { Assert.Skip(reason); return; }

            var (settled, afterEdit) = _gpu.Invoke(() =>
            {
                var p = _gpu.Pipeline!;
                Write(p);
                Write(p);
                var a = p.StretchUboChanged();
                Write(p, normFactor: 0.5f);
                return (a, p.StretchUboChanged());
            });

            settled.ShouldBeFalse();
            afterEdit.ShouldBeTrue("a changed uniform must invalidate, or the view freezes on stale pixels");
        }

        [Fact]
        public void TheSlotsAreTrackedIndependently()
        {
            if (Skip(out var reason)) { Assert.Skip(reason); return; }

            // The split comparison draws from a second slot. If the two shared one shadow they would
            // report each other's changes, and each half would invalidate the other every frame.
            var (primaryQuiet, otherChanged) = _gpu.Invoke(() =>
            {
                var p = _gpu.Pipeline!;
                Write(p);
                Write(p);
                Write(p, normFactor: 0.25f, slot: 1);
                return (p.StretchUboChanged(), p.StretchUboChanged(1));
            });

            primaryQuiet.ShouldBeFalse("writing the other slot must not disturb this one");
            otherChanged.ShouldBeTrue();
        }

        [Fact]
        public void AChangeInTheLastFieldOfTheBlockIsStillSeen()
        {
            if (Skip(out var reason)) { Assert.Skip(reason); return; }

            // debayerMode lands at byte offset 408 of 416 -- the far end. A comparison covering only a
            // prefix of the block would pass every test above and fail here, and in production it would
            // mean toggling the debayer left the cached layer in place showing the old demosaic.
            var (settled, afterEdit) = _gpu.Invoke(() =>
            {
                var p = _gpu.Pipeline!;
                Write(p, debayerMode: 1);
                Write(p, debayerMode: 1);
                var a = p.StretchUboChanged();
                Write(p, debayerMode: 0);
                return (a, p.StretchUboChanged());
            });

            settled.ShouldBeFalse();
            afterEdit.ShouldBeTrue("the comparison must cover the whole block, not a prefix of it");
        }

        [Fact]
        public void AnOutOfRangeSlotReportsChanged()
        {
            if (Skip(out var reason)) { Assert.Skip(reason); return; }

            // "Do not reuse" is the only safe answer to a question about a slot that does not exist.
            _gpu.Invoke(() => _gpu.Pipeline!.StretchUboChanged(99)).ShouldBeTrue();
        }

        private bool Skip(out string reason)
        {
            reason = $"Vulkan runtime not available on this host ({_gpu.UnavailableReason})";
            return !_gpu.VulkanAvailable;
        }

        /// <summary>One representative write, with the fields the tests vary exposed as parameters.</summary>
        private static void Write(VkFitsImagePipeline pipeline,
            int channelCount = 3, float normFactor = 1f, int debayerMode = 1,
            int slot = VkFitsImagePipeline.UboSlotPrimary)
        {
            ReadOnlySpan<float> cd = [1f, 0f, 0f, 1f];
            pipeline.UpdateStretchUBO(
                VkCommandBuffer.Null,
                channelCount: channelCount, stretchMode: 0, normFactor: normFactor,
                curvesBoost: 0f, curvesMidpoint: 0.15f, hdrAmount: 0f, hdrKnee: 0.5f,
                pedestal: (0f, 0f, 0f),
                shadows: (0f, 0f, 0f),
                midtones: (0.5f, 0.5f, 0.5f),
                highlights: (1f, 1f, 1f),
                rescale: (1f, 1f, 1f),
                gridEnabled: false, gridSpacingRA: 0f, gridSpacingDec: 0f, gridLineWidth: 0f,
                imageW: 64f, imageH: 64f, crPix1: 0f, crPix2: 0f, crValRA: 0f, crValDec: 0f,
                cdMatrix: cd,
                debayerMode: debayerMode,
                slot: slot);
        }
    }
}
