using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using SdlVulkan.Renderer;
using TianWen.Lib.Tests;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

[assembly: AssemblyFixture(typeof(SharedGpuStackTeardown))]

namespace TianWen.Lib.Tests;

/// <summary>
/// The one collection every GPU test class in this project belongs to. A collection runs one class at a time, which is
/// what lets the classes share <see cref="SharedGpuStack"/>: each fixture sizes the one offscreen target for its own class,
/// so two fixtures alive at once would resize it under each other, and <see cref="OffscreenGpuFixtureBase"/> throws rather
/// than let that happen.
/// </summary>
[CollectionDefinition(Name)]
public sealed class GpuCollection
{
    public const string Name = "Gpu";
}

/// <summary>
/// The ONE offscreen Vulkan stack of this test process: a <see cref="VkInstance"/> and a <see cref="VulkanContext"/> with
/// its device, built on a thread of its own the first time a GPU fixture asks for it, and destroyed once, after the last
/// test (<see cref="SharedGpuStackTeardown"/>). Every Vulkan call runs on that thread.
/// </summary>
/// <remarks>
/// <para><b>Why one, and why these tests are a process of their own (#1634).</b> The unit suite's host died twice on
/// 2026-10-17 on a native access violation. The one dump Windows kept shows a thread NVIDIA's Vulkan driver
/// (<c>nvoglv64.dll</c>) had started, running for the first time after the data it was started with had been freed, so it
/// called into heap memory. The GPU test classes each built and destroyed an instance and a device of their own, eleven a
/// run, on a machine with every core busy, which is the window such a race needs; a run now builds one and destroys one.
/// And a fault inside a driver cannot be caught in .NET: it ends the process it happens in. In this project's own process
/// it ends the GPU tests, never the unit suite's nine thousand beside them.</para>
/// <para><b>A thread of its own, not a lock.</b> A VkQueue and a VkCommandPool need external synchronisation, and the
/// device relies on single-owner submission rather than taking a lock. Marshalling through <see cref="Invoke{T}"/> /
/// <see cref="InvokeAsync{T}"/> makes ownership correct by construction, so it cannot depend on which thread xUnit runs a
/// test method on, and the stack is destroyed on the thread that built it, which <c>vkDestroyDevice</c> needs as much as a
/// submission does.</para>
/// </remarks>
public sealed unsafe class SharedGpuStack
{
    // Placeholder only: every fixture resizes the target for its class as it takes the stack.
    private const uint InitialSize = 64;

    private static readonly Lazy<SharedGpuStack> Shared = new(() => new SharedGpuStack(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _owner;

    public static SharedGpuStack Instance => Shared.Value;

    public bool VulkanAvailable { get; private set; }
    public string? UnavailableReason { get; private set; }

    /// <summary>The one context, or null when Vulkan is unavailable on this host.</summary>
    public VulkanContext? Ctx { get; private set; }

    private SharedGpuStack()
    {
        using var ready = new ManualResetEventSlim(false);

        _owner = new Thread(() =>
        {
            Initialise();
            ready.Set();

            // GetConsumingEnumerable ends when ShutDown calls CompleteAdding, at which point the teardown item queued
            // just before it has already run.
            foreach (var item in _work.GetConsumingEnumerable())
            {
                item();
            }
        })
        {
            IsBackground = true,
            Name = "gpu-stack",
        };

        _owner.Start();

        // Waiting here publishes everything Initialise wrote, so no fixture ever sees a half-built stack.
        ready.Wait();
    }

    private void Initialise()
    {
        try
        {
            vkInitialize().CheckResult();
            VkInstanceCreateInfo ici = new();
            vkCreateInstance(&ici, null, out var instance).CheckResult();

            // VulkanContext.Dispose() destroys the device and the device the instance, so ShutDown disposing the context
            // covers the whole stack.
            Ctx = VulkanContext.CreateOffscreen(instance, InitialSize, InitialSize);
            VulkanAvailable = true;
        }
        catch (Exception ex)
        {
            VulkanAvailable = false;
            UnavailableReason = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Runs GPU work on the stack's own thread and returns its result.</summary>
    public T Invoke<T>(Func<T> work)
    {
        // Re-entrant calls run inline: already the owning thread, and posting would deadlock waiting for a pump that is
        // busy running this very item.
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
                // Captured rather than thrown: an exception escaping the pump kills the thread, and every later GPU
                // test in the run would hang waiting on a queue nothing drains.
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
    /// Awaitable form of <see cref="Invoke{T}"/>, so an async test keeps its shape without hopping the GPU work onto the
    /// thread pool.
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

    /// <summary>Destroys the stack if a fixture ever built it. Called once, by <see cref="SharedGpuStackTeardown"/>.</summary>
    internal static void ShutDownIfBuilt()
    {
        if (Shared.IsValueCreated)
        {
            Shared.Value.ShutDown();
        }
    }

    private void ShutDown()
    {
        if (Ctx is { } ctx)
        {
            _work.Add(ctx.Dispose);
        }

        _work.CompleteAdding();
        _owner.Join();
        _work.Dispose();
    }
}

/// <summary>Destroys <see cref="SharedGpuStack"/> once every test in the assembly has run.</summary>
public sealed class SharedGpuStackTeardown : IDisposable
{
    public void Dispose() => SharedGpuStack.ShutDownIfBuilt();
}
