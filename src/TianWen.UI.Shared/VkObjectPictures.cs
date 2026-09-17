using System;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using Vortice.Vulkan;

namespace TianWen.UI.Shared;

/// <summary>
/// Object pictures as Vulkan textures, for the object panels of the desktop hosts (the atlas tab, the viewer):
/// the <see cref="ObjectPictureCache{TLoaded, THandle}"/> policy over the desktop picture store.
/// </summary>
/// <remarks>
/// The fetch and the decode run on the thread pool (the store's cache check is file I/O and a decode is tens
/// of milliseconds); the upload happens on the render thread in a later frame, as the Milky Way texture's does.
/// A texture evicted in the frame that drew it is safe, because <see cref="VkTexture.Dispose"/> defers
/// destruction until every frame that could reference it has retired.
/// </remarks>
internal sealed class VkObjectPictures : IDisposable
{
    private readonly ObjectPictureCache<ObjectPicture, VkTexture> _cache;

    public VkObjectPictures(VulkanContext context, Action requestRedraw, ILogger? logger)
    {
        _cache = new ObjectPictureCache<ObjectPicture, VkTexture>(
            load: (image, pixels) => Store is { } store
                ? Task.Run(() => store.GetAsync(image, pixels))
                : Task.FromResult<ObjectPicture?>(null),
            adopt: picture => Upload(context, picture),
            release: texture => texture.Dispose(),
            requestRedraw: requestRedraw,
            logger: logger);
    }

    /// <summary>Where pictures come from; null draws nothing, which is the host with no network service.</summary>
    public IObjectPictureStore? Store { get; set; }

    /// <summary>
    /// Draws <paramref name="image"/> fitted inside <paramref name="rect"/> once it is in hand. The width asked of
    /// Wikimedia is the slot's own pixel width, so a high-DPI panel gets a sharper picture than a low one.
    /// </summary>
    public void Draw(VkRenderer renderer, in ObjectArticleImage image, RectF32 rect)
    {
        if (Store is null || rect.Width < 1f || rect.Height < 1f)
        {
            return;
        }

        if (_cache.TryGet(in image, (int)MathF.Ceiling(rect.Width), out var texture))
        {
            var fitted = ObjectPictureCache<ObjectPicture, VkTexture>.Fit(rect, texture.Width, texture.Height);
            renderer.DrawTexture(texture.DescriptorSet, fitted.X, fitted.Y, fitted.Width, fitted.Height);
        }
    }

    private static VkTexture Upload(VulkanContext context, ObjectPicture picture)
    {
        // The decoder's bytes are RGBA, so the texture is too: no per-pixel swap into BGRA.
        var texture = VkTexture.CreateDeferred(context, picture.Rgba, picture.Width, picture.Height, VkFormat.R8G8B8A8Unorm);
        context.ExecuteOneShot(texture.RecordUpload);
        texture.CleanupStaging();
        return texture;
    }

    public void Dispose() => _cache.Clear();
}
