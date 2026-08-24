using Microsoft.Extensions.Logging;
using SdlVulkan.Renderer;
using System;

namespace TianWen.UI.Shared
{
    /// <summary>
    /// Owns an app's GPU stack -- <see cref="VulkanContext"/>, <see cref="VkRenderer"/>, and the
    /// app's top-level widget renderer -- as ONE resource, so the teardown order lives in exactly one
    /// place. Each layer draws through the one below it, so disposal must run top-down (top, renderer,
    /// context); as three separate locals that ordering was three hand-placed <c>Dispose()</c> lines
    /// at the far end of each host's Program.cs (the GUI's carried a CA2000 suppression explaining why
    /// none of them could be a <c>using</c>). One owner created with a single <c>using var</c> states
    /// it structurally instead, in both hosts.
    /// </summary>
    /// <remarks>
    /// <para>Generic over the top layer because that is the only part that differs per host:
    /// <c>VkGuiRenderer</c> for the GUI, <c>VkImageRenderer</c> for the FITS viewer. The factory
    /// receives the freshly built <see cref="Renderer"/> and closes over everything else the top
    /// layer needs (bus, DPI, trackers); returning the new instance from the factory is also what
    /// lets the analyzer see its ownership transfer into this owner.</para>
    /// <para>A class rather than the tempting <c>readonly ref struct</c>: both hosts' top-level
    /// statements await (shutdown drains), and a ref struct local cannot live across an await. The
    /// members are created HERE, not passed in -- if a later layer's constructor throws, the layers
    /// already built are disposed before the throw escapes, so a half-built stack cannot leak a
    /// Vulkan device.</para>
    /// </remarks>
    public sealed class GpuStack<TTop> : IDisposable where TTop : class, IDisposable
    {
        public VulkanContext Context { get; }

        public VkRenderer Renderer { get; }

        public TTop Top { get; }

        public GpuStack(ILogger logger, SdlVulkanWindow window, uint pixW, uint pixH, Func<VkRenderer, TTop> createTop)
        {
            Context = NativeLoaderDiagnostics.InitNative(logger, "Vulkan device",
                () => VulkanContext.Create(window.Instance, window.Surface, pixW, pixH));
            try
            {
                Renderer = new VkRenderer(Context, pixW, pixH);
            }
            catch
            {
                Context.Dispose();
                throw;
            }

            try
            {
                Top = createTop(Renderer);
            }
            catch
            {
                Renderer.Dispose();
                Context.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Top.Dispose();
            Renderer.Dispose();
            Context.Dispose();
        }
    }
}
