using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// TianWen-specific <see cref="HitResult"/> subclasses.
    /// Generic cases (TextInputHit, ButtonHit, ListItemHit) are in DIR.Lib.
    /// HitResult is an open record hierarchy — app-specific subclasses inherit from it.
    /// </summary>
    public static class AppHitResults
    {
        /// <summary>A profile slot was clicked for device assignment.</summary>
        public sealed record SlotHit(AssignTarget Slot) : HitResult;

        /// <summary>A handoff slider between pinned targets was clicked/dragged.</summary>
        public sealed record SliderHit(int SliderIndex) : HitResult;
    }
}
