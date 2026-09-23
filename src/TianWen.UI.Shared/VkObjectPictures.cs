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
/// of milliseconds); the upload is recorded into the NEXT frame, at its start, through
/// <see cref="VulkanContext.QueueTextureUpload"/>, and the picture is drawn from the frame after it lands. It
/// used to be a one-shot from the draw path: a render-thread wait for the GPU in the middle of a frame's
/// render pass, which blocks for as long as the GPU is busy, some drivers reject outright, and on a stuck
/// GPU leaked a texture per attempt. A texture evicted in the frame that drew it is safe, because
/// <see cref="VkTexture.Dispose"/> defers destruction until every frame that could reference it has retired.
/// </remarks>
internal sealed class VkObjectPictures : IDisposable
{
    private readonly ObjectPictureCache<ObjectPicture, VkTexture> _cache;
    private readonly Action _requestRedraw;

    public VkObjectPictures(VulkanContext context, Action requestRedraw, ILogger? logger)
    {
        _requestRedraw = requestRedraw;
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
            // Not drawable until its upload is recorded, at the start of a frame (see Upload): drawing it
            // sooner samples an image that has never been written. Ask for that frame instead.
            if (!texture.IsUploaded)
            {
                _requestRedraw();
                return;
            }

            // The staging copy is kept until the texture goes (a picture's is small): freed in the frame that
            // recorded the upload, a drop of that frame would find nothing left to upload again, and the
            // picture would never land.
            var fitted = ObjectPictureCache<ObjectPicture, VkTexture>.Fit(rect, texture.Width, texture.Height);
            renderer.DrawTexture(texture.DescriptorSet, fitted.X, fitted.Y, fitted.Width, fitted.Height);
        }
    }

    private static VkTexture Upload(VulkanContext context, ObjectPicture picture)
    {
        // The decoder's bytes are RGBA, so the texture is too: no per-pixel swap into BGRA.
        var texture = VkTexture.CreateDeferred(context, picture.Rgba, picture.Width, picture.Height, VkFormat.R8G8B8A8Unorm);
        context.QueueTextureUpload(texture);
        return texture;
    }

    public void Dispose() => _cache.Clear();
}
