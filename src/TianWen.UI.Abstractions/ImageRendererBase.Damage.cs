using System.Collections.Generic;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    // Which rects this frame changed, so the surface can repaint those instead of everything. The
    // measurement that justifies it: repainting the whole window to update one number in the status bar
    // costs 8% GPU on an Adreno X1-85 over a 4 Mpix pane, and a viewer's most common gesture by far is
    // moving the pointer across the image, which changes exactly that one number.
    //
    // SAFE BY CONSTRUCTION, which is the only way this can be maintained. A frame that asks for a
    // repaint without saying what changed gets a FULL one, so the default for any code path -- including
    // one written later by someone who has never read this file -- is the old behaviour. Only a site
    // that positively knows what moved may narrow it, and narrowing wrongly is the one failure mode that
    // shows on screen (stale pixels), while failing to narrow merely costs what it always cost.
    public abstract partial class ImageRendererBase<TSurface>
    {
        private readonly List<RectF32> _damage = [];
        private bool _fullDamage;
        private bool _narrowedThisEvent;

        /// <summary>
        /// Declares that only <paramref name="rect"/> changed. Call ONLY where the full extent of the
        /// change is known.
        /// </summary>
        protected void RequestDamage(RectF32 rect)
        {
            if (rect.Width <= 0f || rect.Height <= 0f)
            {
                return;
            }

            _damage.Add(rect);
            _narrowedThisEvent = true;
        }

        /// <summary>Declares that an unknown region changed, so the whole surface must be repainted.</summary>
        public void RequestFullFrameDamage() => _fullDamage = true;

        /// <summary>
        /// Takes the damage accumulated for the frame about to be drawn. False means repaint everything,
        /// which is also the answer when nothing was declared at all -- a frame being drawn for a reason
        /// nobody described is not a frame to take chances with.
        /// </summary>
        public bool TryTakeFrameDamage(List<RectF32> into)
        {
            var narrow = !_fullDamage && _damage.Count > 0;
            if (narrow)
            {
                into.AddRange(_damage);
            }

            _damage.Clear();
            _fullDamage = false;
            return narrow;
        }

        /// <summary>
        /// Wraps input dispatch so any handler that requests a repaint without narrowing gets a full
        /// one.
        /// </summary>
        /// <remarks>
        /// Both signals matter and they are not the same: a handler's RETURN value asks the host to draw,
        /// and several handlers instead set <c>ViewerState.NeedsRedraw</c> and fall through. Watching only
        /// one of them would let the other silently inherit a narrowed region belonging to a different
        /// change -- which is exactly the shape of bug that makes damage tracking distrusted.
        /// </remarks>
        /// <remarks>
        /// NOT sealed: VkPlanetaryTab overrides it. A subclass that does so bypasses this wrapper and
        /// therefore never narrows, which lands on the full repaint -- the safe answer -- so overriding
        /// costs correctness nothing. Sealing it to force the wrapper was tried and simply does not
        /// compile against that subclass.
        /// </remarks>
        public override bool HandleInput(InputEvent evt)
        {
            _narrowedThisEvent = false;
            var redrawBefore = _state?.NeedsRedraw ?? false;

            var handled = HandleViewerInput(evt);

            var askedForAFrame = handled || (_state?.NeedsRedraw ?? false) != redrawBefore;
            if (askedForAFrame && !_narrowedThisEvent)
            {
                RequestFullFrameDamage();
            }

            return handled;
        }
    }
}
