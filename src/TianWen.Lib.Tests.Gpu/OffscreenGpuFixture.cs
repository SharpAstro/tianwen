using System;
using System.Threading;
using System.Threading.Tasks;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Shared;

namespace TianWen.Lib.Tests;

/// <summary>
/// Base xUnit fixture that gives a GPU test class the process's one offscreen Vulkan stack
/// (<see cref="SharedGpuStack"/>), with its target sized for the class and a <see cref="VkRenderer"/>
/// and <see cref="VkFitsImagePipeline"/> of the class's own. Concrete size-specific fixtures derive
/// from it (see <see cref="OffscreenGpuFixture"/>, <see cref="VkPrimitiveGpuFixture"/>,
/// <see cref="VkHistogramGpuFixture"/>); each is wired as an <c>IClassFixture&lt;T&gt;</c>, so a GPU
/// test class sets up its renderer and pipeline ONCE, not per test method.
///
/// <para><b>The instance and the device are the process's, never the class's</b>
/// (<see cref="SharedGpuStack"/> says why). A fixture resizes the one offscreen target and destroys
/// only what it built, so every GPU test class belongs in <see cref="GpuCollection"/>, which runs one
/// class at a time. A second fixture asking for the stack while another holds it throws, naming
/// both, rather than resize the target under the first.</para>
///
/// <para>Why a fixture rather than per-test setup: xUnit constructs a fresh test-class instance per
/// test method, so any Vulkan setup in a test method's body (or the class ctor) runs on EVERY method,
/// and repeated Vulkan init and teardown is what made Mesa lavapipe SIGSEGV at process exit ("Test
/// process crashed with exit code 139").</para>
///
/// Channel/histogram textures inside <see cref="VkFitsImagePipeline"/> resize automatically per
/// upload, so the shared pipeline handles arbitrary per-test image dimensions transparently.
///
/// <para>Every call that touches <see cref="Ctx"/>, <see cref="Renderer"/> or <see cref="Pipeline"/>
/// goes through <see cref="Invoke{T}"/> / <see cref="InvokeAsync{T}"/>, which run it on the stack's
/// own thread: a VkQueue needs one owner, and a test that hopped onto the thread pool for its GPU work
/// would submit from a foreign one.</para>
/// </summary>
public abstract class OffscreenGpuFixtureBase : IDisposable
{
    private static OffscreenGpuFixtureBase? s_holder;

    private readonly SharedGpuStack _stack;

    public bool VulkanAvailable { get; private set; }
    public string? UnavailableReason { get; private set; }

    // Tests must guard on VulkanAvailable before dereferencing -- when Vulkan init failed all
    // three remain null and Assert.Skip is the only legitimate response.
    public VulkanContext? Ctx { get; private set; }
    public VkRenderer? Renderer { get; private set; }
    public VkFitsImagePipeline? Pipeline { get; private set; }

    protected OffscreenGpuFixtureBase(int width, int height)
    {
        if (Interlocked.CompareExchange(ref s_holder, this, null) is { } holder)
        {
            throw new InvalidOperationException(
                $"{GetType().Name} asked for the shared GPU stack while {holder.GetType().Name} still holds it. " +
                $"Every GPU test class belongs in [Collection(GpuCollection.Name)], which runs one class at a time.");
        }

        _stack = SharedGpuStack.Instance;
        if (_stack.Ctx is not { } ctx)
        {
            UnavailableReason = _stack.UnavailableReason;
            return;
        }

        try
        {
            _stack.Invoke(() =>
            {
                ctx.ResizeOffscreen((uint)width, (uint)height);
                Renderer = new VkRenderer(ctx, (uint)width, (uint)height);
                Pipeline = new VkFitsImagePipeline(ctx);
                return 0;
            });
            Ctx = ctx;
            VulkanAvailable = true;
        }
        catch (Exception ex)
        {
            UnavailableReason = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Runs GPU work on the stack's own thread and returns its result. Use this for every call that
    /// touches <see cref="Ctx"/>, <see cref="Renderer"/> or <see cref="Pipeline"/>.
    /// </summary>
    public T Invoke<T>(Func<T> work) => _stack.Invoke(work);

    /// <summary>
    /// Awaitable form of <see cref="Invoke{T}"/>, so an async test keeps its shape without hopping the
    /// GPU work onto the thread pool.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
        => _stack.InvokeAsync(work, cancellationToken);

    /// <summary>
    /// The framebuffer is sized to the largest image any test in the class renders, so a smaller test
    /// draws into the top-left sub-rectangle and the readback comes back with the clear colour around
    /// it. This slices the meaningful <paramref name="dstWidth"/> x <paramref name="dstHeight"/> corner
    /// out of it, and returns the input unchanged when there is nothing to slice.
    /// </summary>
    public static byte[] ExtractSubRect(byte[] fullRgba, int srcStrideWidth, int dstWidth, int dstHeight)
    {
        if (dstWidth == srcStrideWidth && fullRgba.Length == dstWidth * dstHeight * 4)
        {
            return fullRgba;
        }

        var result = new byte[dstWidth * dstHeight * 4];
        for (var y = 0; y < dstHeight; y++)
        {
            Buffer.BlockCopy(fullRgba, y * srcStrideWidth * 4, result, y * dstWidth * 4, dstWidth * 4);
        }

        return result;
    }

    /// <summary>
    /// Destroys any device-owned state a derived fixture built beyond the base pipeline and renderer.
    /// Runs on the stack's thread, before those two, so nothing this class built outlives its class:
    /// the device goes on to serve the next one.
    /// </summary>
    protected virtual void DisposeDeviceObjects()
    {
    }

    public void Dispose()
    {
        try
        {
            if (_stack.Ctx is { } ctx)
            {
                _stack.Invoke(() =>
                {
                    // The device outlives this class, so nothing it still runs may reference what goes next.
                    if (!ctx.TryWaitAllFramesIdle($"{GetType().Name} teardown"))
                    {
                        throw new TimeoutException(
                            $"the GPU did not go idle at the end of {GetType().Name}'s class; its pipeline and renderer are left in place " +
                            "rather than destroyed under work that may still be running");
                    }

                    DisposeDeviceObjects();
                    Pipeline?.Dispose();
                    Renderer?.Dispose();
                    return 0;
                });
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref s_holder, null, this);
        }
    }
}

/// <summary>
/// Offscreen Vulkan stack sized to the largest expected stacking/stretch test image
/// (Vela_SNR_Panel = 1310x1291); shared across every test in <see cref="GpuStretchPipelineTests"/>.
/// Smaller tests render into the top-left sub-rectangle and the helper extracts the meaningful
/// slice from the readback. The <see cref="Width"/> / <see cref="Height"/> consts stay so callers
/// keep referencing <c>OffscreenGpuFixture.Width</c> unchanged.
/// </summary>
public sealed class OffscreenGpuFixture : OffscreenGpuFixtureBase
{
    public const int Width = 1310;
    public const int Height = 1291;

    public OffscreenGpuFixture() : base(Width, Height) { }
}

/// <summary>Offscreen Vulkan stack sized for <see cref="VkRendererPrimitiveTests"/> (256x256).</summary>
public sealed class VkPrimitiveGpuFixture : OffscreenGpuFixtureBase
{
    public VkPrimitiveGpuFixture() : base(256, 256) { }
}

/// <summary>
/// Offscreen Vulkan stack for <see cref="SkyMapHorizonCullGpuTests"/> (512x512). Square, because the
/// test points the view at the zenith and at the nadir and compares the two frames, and an aspect
/// ratio would make that comparison carry the viewport's shape as well as the sky's.
/// </summary>
/// <remarks>
/// <para><b>The sky map pipeline and its star buffer are built ONCE per class, not per test.</b> The
/// buffer is the whole Tycho-2 catalogue, ~2.5 million stars, and xUnit constructs a fresh test-class
/// instance per method, so a pipeline owned by the test was built twice; on llvmpipe that is slow
/// (#749). Every render sets the view and the uniforms in full, so sharing the geometry carries no
/// state from one test to the next.</para>
/// <para>It is built on first use rather than in the ctor, because the build needs the catalogue,
/// which a test loads asynchronously, and a host without Vulkan must never pay for it. It is
/// destroyed through <see cref="DisposeDeviceObjects"/>, on this fixture's own thread and before the
/// device, since a pipeline outliving its device is a crash at exit rather than a failed test.</para>
/// </remarks>
public sealed class VkSkyMapGpuFixture : OffscreenGpuFixtureBase
{
    public const int Width = 512;
    public const int Height = 512;

    private TaskCompletionSource<VkSkyMapPipeline>? _starPipeline;
    private DateTimeOffset _starEpoch;
    private VkSkyMapPipeline? _failedStarPipeline;

    public VkSkyMapGpuFixture() : base(Width, Height) { }

    /// <summary>
    /// The sky map pipeline with the full star buffer uploaded for <paramref name="starEpoch"/>,
    /// building it on the first call and returning the same one to every later call.
    /// </summary>
    public async Task<VkSkyMapPipeline> GetStarPipelineAsync(ICelestialObjectDB db, DateTimeOffset starEpoch, CancellationToken cancellationToken)
    {
        // The placeholder is published BEFORE the build starts, so only the CAS winner builds.
        var mine = new TaskCompletionSource<VkSkyMapPipeline>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _starPipeline, mine, null) is { } existing)
        {
            if (existing.Task.IsCompletedSuccessfully && _starEpoch != starEpoch)
            {
                throw new InvalidOperationException(
                    $"the star buffer was built for {_starEpoch:O}, not {starEpoch:O}; one fixture serves one epoch");
            }

            return await existing.Task.WaitAsync(cancellationToken);
        }

        _starEpoch = starEpoch;
        try
        {
            // No test token on the build itself: it is shared, so one test's cancellation must not
            // fault it for the next. The deadline inside bounds it instead.
            mine.SetResult(await BuildStarPipelineAsync(db, starEpoch));
        }
        catch (Exception ex)
        {
            mine.SetException(ex);
        }

        return await mine.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Builds the star geometry and waits for the upload. <see cref="VkSkyMapPipeline.BuildGeometry"/>
    /// starts an async rebuild and <see cref="VkSkyMapPipeline.TryApplyPendingStarBuild"/> performs the
    /// GPU swap on a later frame, which in the app is the render thread coming round again.
    /// </summary>
    private async Task<VkSkyMapPipeline> BuildStarPipelineAsync(ICelestialObjectDB db, DateTimeOffset starEpoch)
    {
        var ctx = Ctx ?? throw new InvalidOperationException($"Vulkan is not available ({UnavailableReason})");
        var pipeline = Invoke(() => new VkSkyMapPipeline(ctx));

        Invoke(() =>
        {
            pipeline.BuildGeometry(db, starEpoch);
            return 0;
        });

        // Bounded, because a wait that cannot end turns a broken build into a hung suite.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var ready = Invoke(() =>
            {
                pipeline.TryApplyPendingStarBuild();
                return pipeline.GeometryReady && pipeline.FullStarsReady;
            });

            if (ready)
            {
                return pipeline;
            }

            await Task.Delay(50);
        }

        // Still owned by the fixture, so it is destroyed with the device like a finished one.
        _failedStarPipeline = pipeline;
        throw new TimeoutException("the star geometry was never ready; nothing would have been measuring the cull");
    }

    protected override void DisposeDeviceObjects()
    {
        if (_starPipeline?.Task is { IsCompletedSuccessfully: true } built)
        {
            built.Result.Dispose();
        }

        _failedStarPipeline?.Dispose();
    }
}

/// <summary>Offscreen Vulkan stack sized for <see cref="VkHistogramPipelineTests"/> (512x64).</summary>
public sealed class VkHistogramGpuFixture : OffscreenGpuFixtureBase
{
    public VkHistogramGpuFixture() : base(512, 64) { }
}
