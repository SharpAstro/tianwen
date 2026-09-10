namespace TianWen.UI.Abstractions;

/// <summary>
/// How much CONTEXT the viewer draws around the photograph: an ordered ladder, stepped by the
/// <see cref="ToolbarAction.Overlays"/> button and the <c>O</c> key.
/// </summary>
/// <remarks>
/// <para>
/// <b>A ladder rather than a checklist, because the rungs are how much you want to know rather than
/// which layers you want.</b> Every other toolbar cycler here (channel, debayer, stretch link) picks
/// ONE value from a set, and a bare toggle per layer would have been the honest alternative -- but
/// three independent toggles are eight states no key can traverse, and the two that matter in
/// practice are "annotate this frame" and "show me where it sits". So the rungs are cumulative and
/// the key walks them.
/// </para>
/// <para>
/// <b>The rung is DERIVED from the layer flags, never stored beside them</b>
/// (<see cref="ViewerState.OverlayLevel"/>). Each rung writes all three flags on the way past, and a
/// layer key that flips one afterwards -- <c>G</c> for the grid -- moves the rung with it rather than
/// leaving the button lit for a layer that is off. A stored rung is a fourth piece of state that can
/// disagree with the three it summarises, and nothing on screen would say which was right.
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

    /// <summary>
    /// The grid, the objects, and the SKY the frame was taken from drawn behind it -- star field,
    /// constellations, the milky way and (where the frame says where and when it was shot) the
    /// horizon, with the photograph composited on top at its solved place, scale and rotation.
    /// </summary>
    Sky = 3,
}
