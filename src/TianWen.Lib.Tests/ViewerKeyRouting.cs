using DIR.Lib;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.Tests;

/// <summary>
/// Sends a key to a viewer the way its HOSTS send one: through <see cref="InputRouter"/>.
/// </summary>
/// <remarks>
/// <para>
/// The viewer used to ask <c>Ui.KeyboardClaimant</c> itself at the top of its key handler, so a test
/// could call <c>HandleInput</c> directly and an open popover would still take Escape. That question is
/// the router's -- it is the first thing <c>InputRouter.HandleKeyDown</c> asks -- and asking it in both
/// places was a second answer to it, which is what kept <c>IKeyboardClaimant</c> alive in tianwen.
/// </para>
/// <para>
/// So a test that reaches past the router is no longer testing what runs: every host routes keys (the
/// GUI since T1, the browser through its own router, and <c>tianwen-fits</c> since the claimant consult
/// was removed). This helper is the smallest thing that puts one back, so these tests keep asking the
/// same question about the same path.
/// </para>
/// </remarks>
internal static class ViewerKeyRouting
{
    /// <summary>A router over one viewer, with everything the router declines falling back to it.</summary>
    internal static InputRouter RouterFor(IPixelWidget viewer)
        => new InputRouter(viewer.Ui, new BackgroundTaskTracker(), static () => { })
        {
            Widgets = () => [viewer],
            Unhandled = viewer.HandleInput,
        };

    /// <summary>
    /// A whole gesture through ONE router, which is what makes press / move / release a gesture rather
    /// than three unrelated events: the capture a press arms lives on the router, so a fresh one per
    /// event would drop the drag between them.
    /// </summary>
    internal static void Route(IPixelWidget viewer, params InputEvent[] events)
    {
        var router = RouterFor(viewer);
        foreach (var evt in events)
        {
            router.Handle(evt);
        }
    }

    /// <summary>One key, routed.</summary>
    internal static void RouteKey(IPixelWidget viewer, InputKey key, InputModifier modifiers = InputModifier.None)
        => RouterFor(viewer).Handle(new InputEvent.KeyDown(key, modifiers));
}
