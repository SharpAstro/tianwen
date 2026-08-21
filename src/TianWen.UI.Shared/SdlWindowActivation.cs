using SdlVulkan.Renderer;
using SharpAstro.AppShell;

namespace TianWen.UI.Shared;

/// <summary>
/// The SDL side of a single-instance hand-off: adapts <see cref="SdlVulkanWindow"/> onto
/// <see cref="IActivatableWindow"/> so both hosts share one statement of the activation rule
/// instead of each carrying its own copy.
///
/// <para>They did each carry a copy, and both were wrong the same way -- an unconditional
/// <c>Restore()</c> before the raise, which un-maximises the window it is bringing forward. The
/// reasoning lives with the decision now (see <see cref="WindowActivation"/>); what is left here is
/// the three-verb translation, which is all a toolkit adapter should be.</para>
/// </summary>
public static class SdlWindowActivation
{
    extension(SdlVulkanWindow window)
    {
        /// <summary>
        /// Bring this window forward for a hand-off from a later launch, preserving whether it was
        /// maximised.
        /// </summary>
        public void ActivateForHandoff() => new SdlActivatableWindow(window).Activate();
    }

    /// <summary>
    /// Allocated per hand-off, which is per user double-click -- so the cost is irrelevant and a
    /// cached instance would only add a field to keep in step with the window it wraps.
    /// </summary>
    private sealed class SdlActivatableWindow(SdlVulkanWindow window) : IActivatableWindow
    {
        public bool IsMinimized => window.IsMinimized;

        public void Restore() => window.Restore();

        public void Raise() => window.Raise();
    }
}
