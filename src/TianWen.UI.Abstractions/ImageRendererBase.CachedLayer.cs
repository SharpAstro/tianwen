using System;
using DIR.Lib;
using TianWen.Lib.Astrometry;

namespace TianWen.UI.Abstractions
{
    // The cached image layer. The image quad runs a demosaic + stretch shader over every pixel of the
    // pane, and the viewer re-ran it for any redraw at all -- a mouse move that changed one number in
    // the status bar re-shaded the whole picture. This renders that content into an offscreen target
    // once and blits it for as long as it stays valid, so a chrome-only redraw costs a textured quad.
    //
    // Two things make it safe rather than merely fast, and both are load-bearing:
    //
    //   * "Is the cache still good?" is answered from the SHADER'S OWN INPUT (the stretch UBO bytes),
    //     not from a hand-listed key of the state that matters. A listed key is correct until someone
    //     adds a uniform, and then it is wrong by showing a stale picture -- the one failure mode with
    //     no visible cause. See VkFitsImagePipeline.StretchUboChanged.
    //   * Every uncertainty falls back to drawing directly, which is what the viewer always did. No
    //     capacity, no support, split active, pane moved too far: all of them just render.
    //
    // Panning is a UV offset, not a re-render, which is why the target is the pane plus a MARGIN. A
    // pane-sized cache would be invalidated by every pan, and panning is one of the two gestures this
    // exists to make cheap (the other being a cursor move). Pan beyond the margin and it re-renders.
    public abstract partial class ImageRendererBase<TSurface>
    {
        /// <summary>
        /// Extra fraction of the pane held on EACH side, so a pan inside it is a blit at an offset.
        /// </summary>
        /// <remarks>
        /// A quarter of the pane each way makes the target 1.5x the pane on both axes, so 2.25x the
        /// pixels: about 19 MB for a 1920x1080 pane, doubled for the two slots. Bigger buys longer pans
        /// before a re-render and costs memory squared, which is the wrong trade for a viewer that may
        /// already be holding a multi-hundred-megabyte document.
        /// </remarks>
        private const float CachedLayerMarginFraction = 0.25f;

        /// <summary>
        /// Whether this viewer caches its image content in an offscreen layer. Off by default, and it
        /// must be opted into by exactly ONE viewer per renderer.
        /// </summary>
        /// <remarks>
        /// <para>The renderer owns a single set of layer targets, so two viewers sharing one would write
        /// each other's slot -- the same cross-frame hazard the per-slot design exists to avoid, one
        /// level up. The GUI has two embedded viewers on one renderer, which is why this cannot simply
        /// default to on.</para>
        /// <para>The standalone viewer is the one that wants it: a full-window static document being
        /// looked at, where redraws are chrome-only. An embedded live preview can pan a document too, so
        /// it is not that caching would be useless there -- only that one claimant is all the facility
        /// supports. Giving each viewer its own layer is an upstream change, not a flag.</para>
        /// </remarks>
        public bool UseCachedImageLayer { get; set; }

        /// <summary>What a slot currently holds, so a later frame can tell whether it may reuse it.</summary>
        private readonly record struct CachedLayerSlotState(
            bool Rendered,
            float AnchorOffsetX, float AnchorOffsetY,
            float Zoom, float PaneW, float PaneH,
            int ImageW, int ImageH, int LayerW, int LayerH);

        private CachedLayerSlotState[] _cachedLayerSlots = [];

        /// <summary>
        /// What the cache actually did, for a host to publish to the debug inspector or a test to
        /// assert on.
        /// </summary>
        /// <remarks>
        /// <para><b>Without this the feature is unfalsifiable from outside the process.</b> A working
        /// cache draws a frame byte-identical to a re-render -- that is the point -- so no screenshot
        /// can show it and no frame-time average can separate "on and not helping" from "never
        /// engaged". The first A/B measurement here could not tell those apart and read as a null
        /// result; with these numbers it took one query to see a 98% hit rate, and a second to find the
        /// before/after split bypassing the cache entirely.</para>
        /// <para>ONE public member rather than three internals behind an InternalsVisibleTo, which was
        /// the first attempt. The friend route works and its precedent sits in LibraryAttributes.cs,
        /// but it must name the ASSEMBLY (<c>tianwen-fits</c>) where every other reference in the repo
        /// names the PROJECT -- and getting that wrong compiles the attribute happily, then fails at the
        /// call site with "does not contain a definition", which reads like a missing member rather than
        /// a mis-named friend. A friend list also grows one string-keyed coupling per host. This
        /// assembly is not packaged, so a public member commits to no published API, and the name says
        /// out loud that it is a diagnostic.</para>
        /// <para><c>LastMiss</c> is the field that earned its place: it separates not-opted-in,
        /// nothing-loaded, split-open, no-capacity and slot-stale, so a disappointing measurement names
        /// its own cause instead of needing the source read to guess at one.</para>
        /// </remarks>
        public readonly record struct CachedLayerDiagnostics(
            bool Enabled, int Renders, int Blits, string LastMiss);

        /// <summary>See <see cref="CachedLayerDiagnostics"/>.</summary>
        public CachedLayerDiagnostics CachedLayerStats
            => new CachedLayerDiagnostics(UseCachedImageLayer, _cachedLayerRenders, _cachedLayerBlits,
                _cachedLayerLastMiss);

        private int _cachedLayerRenders;
        private int _cachedLayerBlits;
        private string _cachedLayerLastMiss = "never attempted";

        // ---- the seam a GPU backend fills in; every default means "unsupported, draw directly" ----

        /// <summary>How many independent layer targets exist. Zero disables caching entirely.</summary>
        protected virtual int CachedLayerSlotCount => 0;

        /// <summary>The slot this frame must render into and sample from.</summary>
        protected virtual int CachedLayerSlotIndex => 0;

        /// <summary>Allocates the targets at a fixed capacity, or reports that it cannot.</summary>
        protected virtual bool TryEnsureCachedLayerTargets(int width, int height) => false;

        /// <summary>Opens the layer pass. Must be called before the main render pass opens.</summary>
        protected virtual bool TryBeginCachedLayerPass(int width, int height) => false;

        /// <summary>Closes the layer pass opened by <see cref="TryBeginCachedLayerPass"/>.</summary>
        protected virtual void EndCachedLayerPass() { }

        /// <summary>Blits a sub-rect of a slot over the given destination rect.</summary>
        protected virtual bool TryDrawCachedLayer(int slot, float x, float y, float w, float h,
            float u0, float v0, float u1, float v1) => false;

        /// <summary>
        /// Writes the image shader's uniforms WITHOUT drawing, so the caller can ask
        /// <see cref="ImageShaderInputChanged"/> before deciding whether a cached layer is still valid.
        /// </summary>
        protected virtual bool TryWriteImageUniforms(IPreviewSource? source, ViewerState state,
            in DisplayRendition rendition, WCS? gridWcs, RenditionSlot slot) => false;

        /// <summary>
        /// Whether the most recent uniform write differs from the one before it. Defaults to true, so a
        /// backend that cannot answer never gets a cache hit.
        /// </summary>
        protected virtual bool ImageShaderInputChanged(RenditionSlot slot) => true;

        // ---- policy ----

        /// <summary>
        /// Renders the image content into this frame's layer slot if the slot cannot be reused. Call
        /// AFTER <see cref="PrepareFrame"/> and BEFORE the main render pass opens; a host that does not
        /// call it simply gets no caching.
        /// </summary>
        public void PrepareCachedImageLayer()
        {
            if (!TryResolveCachedLayerGeometry(out var pane, out var layerW, out var layerH))
            {
                return;
            }

            var state = _state;
            if (state is null)
            {
                return;
            }

            // Ask the shader's own input whether the content moved. A changed uniform invalidates EVERY
            // slot, not just this one: the change is global to the document being displayed, and a slot
            // left marked valid would be blitted on its next turn showing the old dials.
            var rendition = DisplayRendition.FromState(_preparedStretch, state);
            if (TryWriteImageUniforms(_source, state, rendition, _preparedGridWcs, RenditionSlot.Live)
                && ImageShaderInputChanged(RenditionSlot.Live))
            {
                InvalidateCachedImageLayer();
            }

            var slot = CachedLayerSlotIndex;
            if ((uint)slot >= (uint)_cachedLayerSlots.Length)
            {
                return;
            }

            var p = _placement;
            if (IsSlotReusable(_cachedLayerSlots[slot], pane, p, layerW, layerH, out _, out _))
            {
                return;
            }

            if (!TryBeginCachedLayerPass(layerW, layerH))
            {
                return;
            }

            // The quad goes in at the SAME zoom and the same size, translated so the layer's top-left
            // corner is one margin above and left of the pane. Anything else would resample the image
            // twice -- once into the layer and once out of it -- and a viewer exists to show pixels as
            // they are.
            var (originX, originY) = CachedLayerOrigin(pane);
            RenderImageQuad(_source, state, rendition, _preparedGridWcs,
                p.OffsetX - originX, p.OffsetY - originY,
                p.OffsetX - originX + p.DrawW, p.OffsetY - originY + p.DrawH,
                (uint)layerW, (uint)layerH,
                RenditionSlot.Live, sampleBeforeChannels: false);
            EndCachedLayerPass();

            _cachedLayerSlots[slot] = new CachedLayerSlotState(
                Rendered: true,
                AnchorOffsetX: p.OffsetX, AnchorOffsetY: p.OffsetY,
                Zoom: p.Scale, PaneW: pane.Width, PaneH: pane.Height,
                ImageW: ImageWidth, ImageH: ImageHeight,
                LayerW: layerW, LayerH: layerH);
            _cachedLayerRenders++;
        }

        /// <summary>
        /// Draws the pane from this frame's cached slot, or returns false so the caller renders directly.
        /// </summary>
        private bool TryDrawImageFromCachedLayer()
        {
            if (!TryResolveCachedLayerGeometry(out var pane, out var layerW, out var layerH))
            {
                return false;
            }

            var slot = CachedLayerSlotIndex;
            if ((uint)slot >= (uint)_cachedLayerSlots.Length)
            {
                return false;
            }

            var p = _placement;
            if (!IsSlotReusable(_cachedLayerSlots[slot], pane, p, layerW, layerH, out var dx, out var dy))
            {
                _cachedLayerLastMiss = _cachedLayerSlots[slot].Rendered
                    ? "slot stale (the view moved past what it holds)"
                    : $"slot {slot} not built yet";
                return false;
            }

            // The pane's position inside the layer: one margin in, less however far the image has been
            // panned since the layer was rendered.
            var (originX, originY) = CachedLayerOrigin(pane);
            var srcX = pane.X - originX - dx;
            var srcY = pane.Y - originY - dy;

            var u0 = srcX / layerW;
            var v0 = srcY / layerH;
            var u1 = (srcX + pane.Width) / layerW;
            var v1 = (srcY + pane.Height) / layerH;

            PushClip((int)pane.X, (int)pane.Y, (int)pane.Width, (int)pane.Height);
            var drawn = TryDrawCachedLayer(slot, pane.X, pane.Y, pane.Width, pane.Height, u0, v0, u1, v1);
            PopClip();

            if (drawn)
            {
                _cachedLayerBlits++;
                _cachedLayerLastMiss = "";
            }
            else
            {
                _cachedLayerLastMiss = "backend refused the blit";
            }
            return drawn;
        }

        /// <summary>Marks every slot unusable, e.g. because the display settings changed.</summary>
        private void InvalidateCachedImageLayer()
        {
            for (var i = 0; i < _cachedLayerSlots.Length; i++)
            {
                _cachedLayerSlots[i] = default;
            }
        }

        /// <summary>
        /// The pane, and the layer capacity it needs. False whenever caching cannot apply at all, which
        /// is the common answer: not opted in, no backend support, nothing loaded, a degenerate pane, or
        /// the before/after split being open.
        /// </summary>
        private bool TryResolveCachedLayerGeometry(out RectF32 pane, out int layerW, out int layerH)
        {
            pane = default;
            layerW = 0;
            layerH = 0;

            if (!UseCachedImageLayer)
            {
                _cachedLayerLastMiss = "not opted in";
                return false;
            }

            if (CachedLayerSlotCount <= 0)
            {
                _cachedLayerLastMiss = "backend has no layer support";
                return false;
            }

            if (ImageWidth <= 0 || ImageHeight <= 0)
            {
                _cachedLayerLastMiss = "nothing loaded";
                return false;
            }

            // The split draws two renditions into complementary clips of one pane. Caching it would need
            // a layer per rendition, and the split is a deliberate, transient comparison gesture -- not
            // the state a viewer sits in while the user reads the status bar. So it renders directly.
            if (Split.ResolveDividerX(HasBeforeImageTextures, DpiScale) is not null)
            {
                _cachedLayerLastMiss = "before/after split is open";
                return false;
            }

            pane = _layout.ImageArea;
            if (pane.Width < 1f || pane.Height < 1f)
            {
                return false;
            }

            layerW = (int)MathF.Ceiling(pane.Width * (1f + 2f * CachedLayerMarginFraction));
            layerH = (int)MathF.Ceiling(pane.Height * (1f + 2f * CachedLayerMarginFraction));

            if (!TryEnsureCachedLayerTargets(layerW, layerH))
            {
                _cachedLayerLastMiss = $"no capacity for {layerW}x{layerH}";
                return false;
            }

            if (_cachedLayerSlots.Length != CachedLayerSlotCount)
            {
                _cachedLayerSlots = new CachedLayerSlotState[CachedLayerSlotCount];
            }

            return true;
        }

        /// <summary>Where the layer's top-left corner sits in surface coordinates.</summary>
        private (float X, float Y) CachedLayerOrigin(RectF32 pane)
            => (pane.X - pane.Width * CachedLayerMarginFraction,
                pane.Y - pane.Height * CachedLayerMarginFraction);

        /// <summary>
        /// Whether a slot's contents can be shown for the current placement, and by how far the image has
        /// panned since it was rendered.
        /// </summary>
        private bool IsSlotReusable(in CachedLayerSlotState slot, RectF32 pane, in ImagePlacement p,
            int layerW, int layerH, out float dx, out float dy)
        {
            dx = 0f;
            dy = 0f;

            if (!slot.Rendered
                || slot.LayerW != layerW || slot.LayerH != layerH
                || slot.ImageW != ImageWidth || slot.ImageH != ImageHeight
                || slot.Zoom != p.Scale
                || slot.PaneW != pane.Width || slot.PaneH != pane.Height)
            {
                return false;
            }

            dx = p.OffsetX - slot.AnchorOffsetX;
            dy = p.OffsetY - slot.AnchorOffsetY;

            // Inside the margin the pan is a UV offset. Outside it, the layer simply does not hold the
            // pixels being asked for.
            return MathF.Abs(dx) <= pane.Width * CachedLayerMarginFraction
                && MathF.Abs(dy) <= pane.Height * CachedLayerMarginFraction;
        }
    }
}
