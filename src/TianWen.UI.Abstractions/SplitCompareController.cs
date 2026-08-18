using System;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The before/after split: the divider's position, its drag, which comparison is shown, and the
    /// pinned settings it compares against. A <b>control</b> in the taxonomy sense
    /// (docs/architecture/widgets-and-controls.md) -- the owning widget delegates to it, and it owns all
    /// of its own state.
    /// </summary>
    /// <remarks>
    /// <para><b>This exists because the alternative was five fields on <see cref="ViewerState"/> plus a
    /// press, a move and a release branch in each of the viewer's TWO press dispatchers.</b> That shape
    /// is what rule 1 of the taxonomy calls a defect of layering, and it fails in practice as well as in
    /// principle: the split divider was added to the shared dispatcher only, so it drew, stated a resize
    /// cursor, and could not be dragged in the standalone viewer -- silently, because nothing connects
    /// the two copies.</para>
    /// <para><b>The press needs no host branch at all.</b> The divider arms its own drag through the
    /// <c>onClick</c> of the region it registers, from the same rect it painted, so "draw == hit"
    /// extends to "draw == drag". Only move and release are routed, in one line, in the one place both
    /// hosts already forward to (<c>HandleInput</c>).</para>
    /// <para>Lives in UI.Abstractions rather than DIR.Lib for now, following the <c>TrackSlider</c>
    /// precedent: prove it with a consumer, then promote. The drag half has no TianWen dependency and is
    /// a promotion candidate; see docs/plans/controls-upstreaming.md.</para>
    /// </remarks>
    public sealed class SplitCompareController
    {
        // The image-area rect the divider slides along, restated by the paint that draws it. Holding the
        // track here is what lets the drag convert a pointer position without the widget doing the
        // arithmetic -- and what keeps the drag and the divider reading the same geometry.
        private RectF32 _track;

        /// <summary>Design-unit width each half keeps, so neither can collapse to nothing (a zero-width
        /// half reads as the feature being broken rather than as the divider being at its limit).</summary>
        public const float MinHalfWidth = 24f;

        /// <summary>
        /// Divider position as a fraction (0..1) of the image area's width, or <c>null</c> when the split
        /// is off.
        /// </summary>
        public float? Fraction { get; set; }

        /// <summary>True while the user is dragging the divider.</summary>
        public bool IsDragging { get; private set; }

        /// <summary>What the left half shows.</summary>
        public SplitCompare Mode { get; set; } = SplitCompare.PinnedSettings;

        /// <summary>The pinned display settings the split compares against in
        /// <see cref="SplitCompare.PinnedSettings"/> mode, or <c>null</c> when nothing is pinned.</summary>
        public DisplayRendition? Pinned { get; private set; }

        /// <summary>
        /// The <see cref="ViewerState.SourceGeneration"/> the retained before-pixels belong to, or
        /// <c>null</c> when nothing is retained.
        /// </summary>
        public int? PixelsGeneration { get; set; }

        // One-shot: the live uniforms are solved per frame during rendering and are not stored anywhere,
        // so an input handler has nothing to copy and can only ask for the pin to happen next paint.
        private bool _pinRequested;

        /// <summary>True when the split is showing.</summary>
        public bool IsOn => Fraction is not null;

        /// <summary>Restates the track the divider slides along. Call from the paint that draws it.</summary>
        public void SetTrack(in RectF32 track) => _track = track;

        /// <summary>
        /// Arms the drag. Wired as the divider region's <c>onClick</c>, which is what keeps the press off
        /// every host's dispatcher.
        /// </summary>
        public void BeginDrag() => IsDragging = true;

        /// <summary>
        /// Routes pointer motion and release. Returns true when the event was consumed. The press is NOT
        /// handled here -- it arrives through <see cref="BeginDrag"/> off the registered region.
        /// </summary>
        public bool HandleInput(InputEvent evt)
        {
            if (!IsDragging)
            {
                return false;
            }

            switch (evt)
            {
                case InputEvent.MouseMove(var px, _):
                    if (_track.Width > 0f)
                    {
                        Fraction = Math.Clamp((px - _track.X) / _track.Width, 0f, 1f);
                    }
                    return true;

                case InputEvent.MouseUp:
                    IsDragging = false;
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Toggles the split on or off.
        /// </summary>
        /// <remarks>
        /// Enabling and pinning are ONE action, deliberately: a divider that is up with nothing to
        /// compare against draws two identical halves, which is indistinguishable from the feature being
        /// broken. When pre-enhance pixels are retained those win, because a user who just enhanced wants
        /// to compare PIXELS -- which also makes the mode a consequence of what exists rather than a
        /// setting to learn.
        /// </remarks>
        public void Toggle(bool hasBeforePixels)
        {
            if (IsOn)
            {
                Fraction = null;
                IsDragging = false;
                return;
            }

            if (hasBeforePixels)
            {
                Mode = SplitCompare.BeforePixels;
            }
            else
            {
                Mode = SplitCompare.PinnedSettings;
                _pinRequested = true;
            }

            Fraction = 0.5f;
        }

        /// <summary>
        /// Pins the current display settings as what the split compares against, turning it on if it was
        /// off. The "pin, then fiddle" gesture: pin what you have, then move the dials and watch the
        /// difference.
        /// </summary>
        public void RequestPin()
        {
            Mode = SplitCompare.PinnedSettings;
            _pinRequested = true;
            Fraction ??= 0.5f;
        }

        /// <summary>
        /// Consumes a pending pin request against the rendition being displayed right now. Called from
        /// the paint, the one place the live uniforms exist.
        /// </summary>
        public void ConsumePinRequest(in DisplayRendition live)
        {
            if (_pinRequested)
            {
                Pinned = live;
                _pinRequested = false;
            }
        }

        /// <summary>
        /// Drops retained before-pixels that no longer belong to what is displayed, and takes the split
        /// down with them. Returns true when something was dropped, so the caller can free the pixels.
        /// </summary>
        /// <remarks>
        /// This is INVALIDATION, not eviction: the pixels are the before of a source that is gone, so
        /// there is nothing to reload. Asked where the comparison is DRAWN, so none of the eight paths
        /// that swap the displayed image has to remember it.
        /// </remarks>
        public bool DropIfStale(int sourceGeneration)
        {
            if (PixelsGeneration is not { } generation || generation == sourceGeneration)
            {
                return false;
            }

            PixelsGeneration = null;
            if (Mode is SplitCompare.BeforePixels)
            {
                Fraction = null;
                IsDragging = false;
            }
            return true;
        }

        /// <summary>
        /// The divider's X in surface pixels, or <c>null</c> when the split is off or cannot be shown.
        /// </summary>
        /// <param name="hasBeforePixels">Whether the backend is holding pre-enhance pixels.</param>
        /// <param name="dpiScale">Scales <see cref="MinHalfWidth"/> into surface pixels.</param>
        public float? ResolveDividerX(bool hasBeforePixels, float dpiScale)
        {
            if (Fraction is not { } fraction)
            {
                return null;
            }

            // Each mode has its own precondition, and a mode whose precondition fails must not draw a
            // divider: two identical halves with a line between them is indistinguishable from a bug.
            var available = Mode switch
            {
                SplitCompare.BeforePixels => hasBeforePixels,
                _ => Pinned is not null,
            };
            if (!available)
            {
                return null;
            }

            var minHalf = MinHalfWidth * dpiScale;
            if (_track.Width <= minHalf * 2f)
            {
                return null;
            }

            var x = _track.X + Math.Clamp(fraction, 0f, 1f) * _track.Width;
            return Math.Clamp(x, _track.X + minHalf, _track.Right - minHalf);
        }

        /// <summary>The rendition the left half draws with, given the one being displayed live.</summary>
        public DisplayRendition ComparisonRendition(in DisplayRendition live)
            => Mode is SplitCompare.BeforePixels ? live : Pinned ?? live;

        /// <summary>Whether the left half samples the retained pixels rather than the live ones.</summary>
        public bool ComparesPixels => Mode is SplitCompare.BeforePixels;
    }
}
