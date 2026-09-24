using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Shared;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace TianWen.Lib.Tests;

/// <summary>
/// Base xUnit fixture that owns a single offscreen Vulkan stack (VkInstance +
/// <see cref="VulkanContext"/> + <see cref="VkRenderer"/> + <see cref="VkFitsImagePipeline"/>)
/// at a fixed size, created once and disposed once. Concrete size-specific fixtures derive from
/// it (see <see cref="OffscreenGpuFixture"/>, <see cref="VkPrimitiveGpuFixture"/>,
/// <see cref="VkHistogramGpuFixture"/>); each is wired as an <c>IClassFixture&lt;T&gt;</c> so a
/// GPU test class creates its Vulkan stack ONCE, not per test method.
///
/// Why this must be a fixture, not per-test setup: xUnit constructs a fresh test-class instance
/// per test method, so any Vulkan init in a test method's body (or the class ctor) runs on EVERY
/// method. Repeated vkInitialize + vkCreateInstance + vkCreateDebugUtilsMessengerEXT + teardown
/// makes Mesa lavapipe + the Khronos validation layer + the libvulkan loader accumulate enough
/// TLS / process-global state that the runtime SIGSEGVs during process exit -- xUnit reports
/// "Catastrophic failure: Test process crashed with exit code 139". Hoisting the stack into a
/// class fixture removes the churn (one init/destroy per class instead of per method).
///
/// Channel/histogram textures inside <see cref="VkFitsImagePipeline"/> resize automatically per
/// upload, so the shared pipeline handles arbitrary per-test image dimensions transparently.
///
/// <para><b>The fixture owns a dedicated thread, and every Vulkan call runs on it.</b> A VkQueue and
/// VkCommandPool require external synchronization, and an offscreen device relies on single-owner
/// submission rather than taking a lock, so it asserts that submissions come from the thread that
/// created it. A test that hopped onto the thread pool to do its GPU work (an <c>await Task.Run</c>
/// around the render) therefore submitted from a foreign thread. That was always unsynchronised
/// access; until the device grew the assertion it merely happened not to be caught, which is the
/// worse of the two outcomes. Marshalling through <see cref="InvokeAsync{T}"/> /
/// <see cref="Invoke{T}"/> makes ownership correct by construction and serialises the shared stack
/// as a side effect, so it cannot depend on which thread xUnit happens to run a test method on.</para>
///
/// <para>A pump thread rather than a lock: the constraint here is thread AFFINITY, not mutual
/// exclusion, and no lock can give a foreign thread permission to submit.</para>
/// </summary>
public abstract unsafe class OffscreenGpuFixtureBase : IDisposable
{
    public bool VulkanAvailable { get; private set; }
    public string? UnavailableReason { get; private set; }

    // Tests must guard on VulkanAvailable before dereferencing -- when Vulkan init failed all
    // three remain null and Assert.Skip is the only legitimate response.
    public VulkanContext? Ctx { get; private set; }
    public VkRenderer? Renderer { get; private set; }
    public VkFitsImagePipeline? Pipeline { get; private set; }

    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _owner;

    protected OffscreenGpuFixtureBase(int width, int height)
    {
        using var ready = new ManualResetEventSlim(false);

        _owner = new Thread(() =>
        {
            Initialise(width, height);
            ready.Set();

            // GetConsumingEnumerable ends when Dispose calls CompleteAdding, at which point the
            // teardown item queued just before it has already run -- so the stack is destroyed on the
            // same thread that built it, which vkDestroyDevice needs just as much as submission does.
            foreach (var item in _work.GetConsumingEnumerable())
            {
                item();
            }
        })
        {
            IsBackground = true,
            Name = $"gpu-fixture-{GetType().Name}",
        };

        _owner.Start();

        // Waiting here publishes everything Initialise wrote: the ctor returns only once the stack
        // exists (or is known unavailable), so tests never see a half-built fixture.
        ready.Wait();
    }

    private void Initialise(int width, int height)
    {
        try
        {
            vkInitialize().CheckResult();
            VkInstanceCreateInfo ici = new();
            vkCreateInstance(&ici, null, out var instance).CheckResult();

            // VulkanContext.Dispose() destroys the instance at teardown, so the fixture
            // doesn't separately track it -- Ctx.Dispose() in the fixture's Dispose covers
            // both the device + instance lifecycle.
            Ctx = VulkanContext.CreateOffscreen(instance, (uint)width, (uint)height);
            Renderer = new VkRenderer(Ctx, (uint)width, (uint)height);
            Pipeline = new VkFitsImagePipeline(Ctx);
            VulkanAvailable = true;
        }
        catch (Exception ex)
        {
            VulkanAvailable = false;
            UnavailableReason = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Runs GPU work on the fixture's own thread and returns its result. Use this for every call that
    /// touches <see cref="Ctx"/>, <see cref="Renderer"/> or <see cref="Pipeline"/>.
    /// </summary>
    public T Invoke<T>(Func<T> work)
    {
        // Re-entrant calls run inline: already the owning thread, and posting would deadlock waiting
        // for a pump that is busy running this very item.
        if (Thread.CurrentThread == _owner)
        {
            return work();
        }

        var result = default(T);
        ExceptionDispatchInfo? failure = null;
        using var done = new ManualResetEventSlim(false);

        _work.Add(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                // Captured rather than thrown: an exception escaping the pump kills the thread and
                // every later test in the class hangs waiting on a queue nothing drains.
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();
        failure?.Throw();
        return result!;
    }

    /// <summary>
    /// Awaitable form of <see cref="Invoke{T}"/>, so an async test keeps its shape without hopping the
    /// GPU work onto the thread pool.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        if (Thread.CurrentThread == _owner)
        {
            return Task.FromResult(work());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        _work.Add(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }

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
    /// Destroys any device-owned state a derived fixture built beyond the base stack. Runs on the
    /// fixture's own thread, before the base pipeline, renderer and context, so nothing outlives the
    /// device it was created on.
    /// </summary>
    protected virtual void DisposeDeviceObjects()
    {
    }

    public void Dispose()
    {
        if (VulkanAvailable)
        {
            _work.Add(() =>
            {
                DisposeDeviceObjects();
                Pipeline?.Dispose();
                Renderer?.Dispose();
                Ctx?.Dispose();
            });
        }

        _work.CompleteAdding();
        _owner.Join();
        _work.Dispose();
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
