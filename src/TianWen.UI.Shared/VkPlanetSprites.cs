using System;
using System.Collections.Generic;
using SdlVulkan.Renderer;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using Vortice.Vulkan;

namespace TianWen.UI.Shared;

/// <summary>
/// The atlas's planet sprites (<see cref="PlanetSpriteCache"/>) as Vulkan textures, one per body (docs/plans/atlas-planet-detail.md,
/// "A4: the textured disc"). A new sprite's upload is QUEUED into the next frame (<see cref="VulkanContext.QueueTextureUpload"/>), as
/// the object pictures' are: no render-thread wait for the GPU mid-frame. Until it has landed the body's previous texture is drawn, so
/// a rebuilt sprite never blinks back to the dot; a replaced texture is disposed, which the context defers until no frame in flight
/// can sample it.
/// </summary>
internal sealed class VkPlanetSprites(VulkanContext context, Action requestRedraw) : IDisposable
{
    private sealed class Slot
    {
        public PlanetSprite? Sprite;
        public VkTexture? Shown;
        public PlanetSprite? ShownSprite;
        public VkTexture? Pending;
    }

    private readonly Dictionary<CatalogIndex, Slot> _slots = [];

    /// <summary>Draws <paramref name="sprite"/> centred on (x, y), its globe <paramref name="radiusPx"/> on screen. False until a texture of the body has landed.</summary>
    public bool Draw(VkRenderer renderer, PlanetSprite sprite, float x, float y, float radiusPx)
    {
        if (!_slots.TryGetValue(sprite.Body, out var slot))
        {
            _slots[sprite.Body] = slot = new Slot();
        }
        if (!ReferenceEquals(slot.Sprite, sprite))
        {
            // A newer sprite than the one uploading: the upload in flight is superseded.
            slot.Pending?.Dispose();
            slot.Pending = VkTexture.CreateDeferred(context, sprite.Rgba, sprite.Side, sprite.Side, VkFormat.R8G8B8A8Unorm);
            context.QueueTextureUpload(slot.Pending);
            slot.Sprite = sprite;
        }
        if (slot.Pending is { IsUploaded: true } landed)
        {
            slot.Shown?.Dispose();
            slot.Shown = landed;
            slot.ShownSprite = slot.Sprite;
            slot.Pending = null;
        }
        else if (slot.Pending is not null)
        {
            // Recorded at the start of the next frame: ask for it.
            requestRedraw();
        }
        if (slot.Shown is not { } texture || slot.ShownSprite is not { } shown)
        {
            return false;
        }
        var half = (float)(shown.Side * radiusPx / shown.RadiusPx / 2);
        renderer.DrawTexture(texture.DescriptorSet, x - half, y - half, 2 * half, 2 * half);
        return true;
    }

    public void Dispose()
    {
        foreach (var slot in _slots.Values)
        {
            slot.Shown?.Dispose();
            slot.Pending?.Dispose();
        }
        _slots.Clear();
    }
}
