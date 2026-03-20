using System;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// A clickable region registered during rendering. The hit test walks these
    /// in reverse order (last-registered = on top) to find what was clicked.
    /// </summary>
    public readonly record struct ClickableRegion(float X, float Y, float Width, float Height, HitResult Result, Action? OnClick = null);

    /// <summary>
    /// Describes what was hit during a click. Carries enough context for
    /// the event loop to handle it without knowing the tab's internal layout.
    /// </summary>
    public record HitResult
    {
        /// <summary>A text input field was clicked — activate it and start SDL text input.</summary>
        public sealed record TextInputHit(TextInputState Input) : HitResult;

        /// <summary>A named action button was clicked.</summary>
        public sealed record ButtonHit(string Action) : HitResult;

        /// <summary>A list item was clicked at the given index.</summary>
        public sealed record ListItemHit(string ListId, int Index) : HitResult;

        /// <summary>A profile slot was clicked for device assignment.</summary>
        public sealed record SlotHit(AssignTarget Slot) : HitResult;
    }
}
