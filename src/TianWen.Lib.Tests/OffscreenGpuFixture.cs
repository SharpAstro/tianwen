using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using SdlVulkan.Renderer;
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

    public void Dispose()
    {
        if (VulkanAvailable)
        {
            _work.Add(() =>
            {
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

/// <summary>Offscreen Vulkan stack sized for <see cref="VkHistogramPipelineTests"/> (512x64).</summary>
public sealed class VkHistogramGpuFixture : OffscreenGpuFixtureBase
{
    public VkHistogramGpuFixture() : base(512, 64) { }
}
