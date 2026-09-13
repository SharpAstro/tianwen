namespace TianWen.UI.Abstractions;

/// <summary>
/// How much ANNOTATION the viewer draws over the photograph: an ordered ladder, stepped by the
/// <see cref="ToolbarAction.Overlays"/> button and the <c>O</c> key.
/// </summary>
/// <remarks>
/// <para>
/// <b>A ladder rather than a checklist, because the rungs are how much you want to know rather than
/// which layers you want.</b> Every other toolbar cycler here (channel, debayer, stretch link) picks
/// ONE value from a set, and a bare toggle per layer would have been the honest alternative -- but
/// independent toggles are a power of two no key can traverse, and what matters in practice is how
/// heavily to annotate. So the rungs are cumulative and the key walks them.
/// </para>
/// <para>
/// <b>The sky behind the frame is NOT a rung, and used to be.</b> It reads as one more step of the
/// same idea and is not: every rung here draws ON the photograph from the photograph's own solution,
/// while the backdrop puts a whole second view BEHIND it, takes the pan clamp off, brings up its own
/// layer palette and re-points the star field. Riding the ladder cost it three ways. It was
/// unreachable without stepping through two annotation states nobody wanted on the way, it was
/// invisible on a toolbar that showed one mark for four meanings -- "where's the sky?", which is what
/// started this -- and pressing past the top rung on an UNSOLVED frame did nothing at all, silently,
/// because the backdrop needs a WCS and a ladder rung has nowhere to say so. It has its own button
/// and its own key (<c>Y</c>) now, and that button dims with a reason when the frame cannot carry it.
/// </para>
/// <para>
/// <b>The rung is DERIVED from the layer flags, never stored beside them</b>
/// (<see cref="ViewerState.OverlayLevel"/>). Each rung writes both flags on the way past, and a
/// layer key that flips one afterwards -- <c>G</c> for the grid -- moves the rung with it rather than
/// leaving the button lit for a layer that is off. A stored rung is a third piece of state that can
/// disagree with the two it summarises, and nothing on screen would say which was right.
/// </para>
/// </remarks>
public enum ViewerOverlayLevel
{
    /// <summary>The photograph alone.</summary>
    None = 0,

    /// <summary>The WCS coordinate grid over the frame.</summary>
    Grid = 1,

    /// <summary>The grid plus catalog objects: ellipses, markers and labels from the object database.</summary>
    Objects = 2,
}
