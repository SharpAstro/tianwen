using System;
using System.Collections.Immutable;
using System.Linq;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The toolbar actions a viewer's HOST offers: which buttons the bar shows, and which keys for an action only a
/// host can run (Open, Save, the crop, the solve, Enhance) do anything. The buttons always come in the viewer's
/// one order and under its own labels, so a host chooses WHAT is on the bar, never how it reads (step 3 of P1,
/// docs/plans/live-session-preview.md).
/// </summary>
/// <remarks>
/// <para>It replaced three ways of choosing the bar: a subclass overriding the button list (the planetary view),
/// a flag for Enhance, and four toolbars composed by whether a sky map and an AI pipeline had been wired. A press
/// on an offered action the viewer cannot run itself reaches the host through
/// <see cref="ImageRendererBase{TSurface}.ToolbarPressPolicy"/>, or through the signal its key posts.</para>
/// <para><b>Hidden and dim mean different things, and the sky button needs both.</b> A host that can NEVER draw
/// the sky (it gave the viewer no map: the GUI's embedded views) does not offer it, so the button is absent,
/// exactly as Enhance is without an AI pipeline. A host that can, looking at a frame with no astrometric
/// solution, can draw it LATER, so there the button is offered and dim, and its tooltip says to plate solve.
/// Collapsing the two would either put a permanently dead control on a bar or hide the very affordance the user
/// went looking for when they asked where the sky was.</para>
/// </remarks>
public sealed class ToolbarOffer
{
    // Every button any host can offer, in the one order every bar keeps. Group breaks insert extra spacing: 0
    // the file list, 1 file, 2 stretch, 3 channel/debayer/tone/compare, 4 zoom, 5 astrometry/stars/colour/
    // enhance, 6 help.
    // Group numbers matter only by INEQUALITY with the previous button's -- a change inserts
    // ButtonGroupSpacing -- so they are renumbered from 0 rather than the file list borrowing one.
    // Its own group at the head is what makes it read as a window control instead of a third file
    // action, and the leading button takes no gap of its own (prevGroup starts at -1).
    private static readonly ImmutableArray<(string Label, ToolbarAction Action, int Group)> AllButtons =
    [
        ("Files", ToolbarAction.FileList, 0),
        ("Open", ToolbarAction.Open, 1),
        ("Save", ToolbarAction.Save, 1),
        ("STF", ToolbarAction.StretchToggle, 2),
        ("Link", ToolbarAction.StretchLink, 2),
        ("Params", ToolbarAction.StretchParams, 2),
        ("Channel", ToolbarAction.Channel, 3),
        ("Debayer", ToolbarAction.Debayer, 3),
        // "Boost" and "HDR" until 8.1, side by side doing related things to the same pixels,
        // and the second was the one label in this bar that promised what the viewer does not
        // do -- a soft knee after the MTF, inside [0, 1], never a nit above SDR white. Folded
        // into one popover for the reason Calibrate and SPCC folded into White balance:
        // see ImageRendererBase.TonePanel.cs and docs/plans/hdr-display.md.
        ("Tone", ToolbarAction.Tone, 3),
        ("A/B", ToolbarAction.Compare, 3),
        // Two plain buttons, for a host that wants Fit and 1:1 one press each (the planetary view)
        // rather than the stepping Zoom control beside them.
        ("Fit", ToolbarAction.ZoomFit, 4),
        ("1:1", ToolbarAction.ZoomActual, 4),
        // One control, not two: the label is computed per frame (Fit / 1:1 / a percentage), so the
        // text here is only the measurement seed and the widest state it has to fit.
        ("Fit", ToolbarAction.Zoom, 4),
        ("Crop", ToolbarAction.AutoCrop, 5),
        ("Solve", ToolbarAction.PlateSolve, 5),
        // One control, not two: the mark says which rung of the annotation ladder the view is on
        // (grid / objects), and the text here is only the measurement seed.
        ("Objects", ToolbarAction.Overlays, 5),
        // The Sky button sits beside the annotation ladder it left: that one annotates the
        // photograph, this one shows where the photograph sits. It keeps a LABEL rather than going
        // mark-only like the white balance, because "where's the sky?" is the report that produced
        // it -- an unlabelled mark on a crowded bar is exactly as findable as the ladder rung it
        // replaced.
        ("Sky", ToolbarAction.SkyBackdrop, 5),
        ("Stars", ToolbarAction.Stars, 5),
        ("NeutBg", ToolbarAction.BackgroundNeutralize, 5),
        // Mark only (three colour discs): it opens the white-balance popover, and its highlight
        // says a white balance is in force. Last in the colour group, beside what sets one.
        //
        // "Calibrate" and "SPCC" used to sit here as well, and they were ONE control wearing two
        // labels: both ran SetColorCalibrationEnabled on the same flag, so whichever you pressed
        // lit the other. The photometric fit now lives inside this popover, next to the sliders it
        // populates, which is also the only place that can show its provenance (method, survivor
        // count, white reference) rather than three numbers that a grey-world guess could equally
        // have produced.
        ("", ToolbarAction.WhiteBalance, 5),
        ("Enhance", ToolbarAction.Enhance, 5),
        // The help button last of every bar: an Enhance appended past it once ran group 5 before group 4.
        ("?", ToolbarAction.Shortcuts, 6),
    ];

    /// <summary>
    /// The standalone file viewer's bar, without the two buttons a host has to have wired something for: the
    /// sky (a map) and Enhance (an AI pipeline). What a host that says nothing gets.
    /// </summary>
    public static readonly ToolbarOffer FileViewer = Of(
        ToolbarAction.FileList, ToolbarAction.Open, ToolbarAction.Save,
        ToolbarAction.StretchToggle, ToolbarAction.StretchLink, ToolbarAction.StretchParams,
        ToolbarAction.Channel, ToolbarAction.Debayer, ToolbarAction.Tone, ToolbarAction.Compare,
        ToolbarAction.Zoom, ToolbarAction.AutoCrop, ToolbarAction.PlateSolve, ToolbarAction.Overlays,
        ToolbarAction.Stars, ToolbarAction.BackgroundNeutralize, ToolbarAction.WhiteBalance, ToolbarAction.Shortcuts);

    /// <summary>
    /// The planetary view's bar. A planetary disk is featureless + bright: there are no stars to detect, no
    /// plate solve, no SPCC / background-neutralisation, and no file to "Open" in a live capture. So it offers
    /// only the controls that actually apply -- stretch (STF / Link / Params), channel + debayer (for a colour
    /// sensor), tone (boost + highlight soft clip), and zoom (Fit / 1:1). The wavelet-sharpen + white-balance
    /// sliders live in the info panel, not the toolbar.
    /// </summary>
    public static readonly ToolbarOffer Planetary = Of(
        ToolbarAction.StretchToggle, ToolbarAction.StretchLink, ToolbarAction.StretchParams,
        ToolbarAction.Channel, ToolbarAction.Debayer, ToolbarAction.Tone,
        ToolbarAction.ZoomFit, ToolbarAction.ZoomActual);

    /// <summary>
    /// The Live Session preview's bar, for a camera's frames as they are taken: stretch (STF / Link / Params),
    /// channel + debayer, tone, white balance, zoom (Fit / 1:1, as the planetary view), the grid and the objects
    /// once a solve has placed the frame, and the solve itself, which the node runs (the preview's host). Nothing
    /// of the file viewer: no file list, Open, A|B, Enhance or crop, and no Save as seen, since the pane's own
    /// Save writes the node's FITS. Stars join it with the node's measurements (P2 of
    /// docs/plans/live-session-preview.md).
    /// </summary>
    public static readonly ToolbarOffer LivePreview = Of(
        ToolbarAction.StretchToggle, ToolbarAction.StretchLink, ToolbarAction.StretchParams,
        ToolbarAction.Channel, ToolbarAction.Debayer, ToolbarAction.Tone,
        ToolbarAction.ZoomFit, ToolbarAction.ZoomActual,
        ToolbarAction.PlateSolve, ToolbarAction.Overlays, ToolbarAction.WhiteBalance);

    private readonly ImmutableHashSet<ToolbarAction> _actions;

    private ToolbarOffer(ImmutableHashSet<ToolbarAction> actions)
    {
        _actions = actions;
        // Built once per offer, so the per-frame render and hit-test loops stay allocation-free, which is
        // why the bar is a table at all.
        Buttons = AllButtons.Where(button => actions.Contains(button.Action)).ToImmutableArray();
    }

    /// <summary>An offer of exactly <paramref name="actions"/>; their order on the bar is the viewer's.</summary>
    public static ToolbarOffer Of(params ReadOnlySpan<ToolbarAction> actions)
        => new ToolbarOffer(ImmutableHashSet.CreateRange(actions.ToArray()));

    /// <summary>This offer and <paramref name="more"/>.</summary>
    public ToolbarOffer With(params ReadOnlySpan<ToolbarAction> more)
        => new ToolbarOffer(_actions.Union(more.ToArray()));

    /// <summary>Whether the host offers <paramref name="action"/>: its button is on the bar and its key acts.</summary>
    public bool Offers(ToolbarAction action) => _actions.Contains(action);

    /// <summary>The bar's buttons, in the viewer's order, under its labels.</summary>
    internal ImmutableArray<(string Label, ToolbarAction Action, int Group)> Buttons { get; }
}
