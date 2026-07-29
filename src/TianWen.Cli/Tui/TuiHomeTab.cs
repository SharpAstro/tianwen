using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.Cli.Tui;

/// <summary>
/// The TUI home screen. Renders the <b>same</b> <see cref="HomeBoardLayout"/> tree the GPU tab does, with
/// the same <see cref="HomeBoardStyle.Default"/> palette and the same <see cref="HomeBoard.BuildCards"/>
/// data -- no per-surface copy of the card, its rows, or its colours.
/// <para>
/// <b>This is the first tree genuinely shared across surface kinds</b>, and it only works because a design
/// unit now resolves per axis (DIR.Lib 6.23) and the cell context is told which convention the tree was
/// authored in (<see cref="CellMeasureContext.PixelAuthored"/>, Console.Lib). Before that, sharing a tree
/// between a pixel surface and a terminal was type-correct and geometrically meaningless: the card's
/// 250-unit width would have become 250 COLUMNS instead of 31.
/// </para>
/// <para>
/// Cards are built here rather than read from <c>GuiAppState.HomeCards</c>: that snapshot is published by
/// the GUI's telemetry poll, which the TUI does not run. The builder is pure, so calling it per frame is
/// the same work the poll would have done.
/// </para>
/// </summary>
internal sealed class TuiHomeTab(
    GuiAppState appState,
    ViewContexts contexts,
    RemoteRigRegistry rigs,
    ITimeProvider timeProvider,
    SignalBus bus) : TuiTabBase
{
    /// <summary>
    /// The board's tree counts in pixel-ish design units because it is shared with the GPU surface, so the
    /// cell mapping has to say how big a cell is rather than assume one unit per cell.
    /// </summary>
    protected override CellMeasureContext MeasureContext => CellMeasureContext.PixelAuthored;

    protected override bool IsReady => true;

    protected override void CreateWidgets()
    {
        // Nothing hosted: the whole screen is layout nodes, so there is no raster sub-widget to place.
    }

    protected override void RenderContent()
    {
        // Pure projection over live state -- see the class remarks for why this is not read from appState.
        appState.HomeCards = HomeBoard.BuildCards(contexts, rigs, appState, timeProvider.GetUtcNow());
    }

    protected override Layout.Node BuildLayout()
    {
        // Columns come from the width in DESIGN units, not cells, since that is what the shared builder
        // reasons in. One column is CellMeasureContext.PixelAuthored's 8 design units.
        // Cells to design units on each axis -- the terminal's 8x16 nominal cell, matching
        // CellMeasureContext.PixelAuthored. Both extents matter: the width picks the columns, and the height
        // is what Auto uses to swap the cards for the table when the window has been made too small for them.
        return HomeBoardLayout.Build(
            appState.HomeCards, HomeBoardStyle.Default,
            Content.Width * PixelAuthoredCellWidth, timeProvider.GetUtcNow(),
            Content.Height * PixelAuthoredCellHeight,
            appState.HomeBoardView, SelectAction, SelectViewAction);
    }

    /// <summary>Design units per character cell, matching <see cref="CellMeasureContext.PixelAuthored"/>'s 8x16.</summary>
    private const float PixelAuthoredCellWidth = 8f;

    /// <inheritdoc cref="PixelAuthoredCellWidth"/>
    private const float PixelAuthoredCellHeight = 16f;

    protected override void PaintHost(string key, Rect<int> rect, bool geometryChanged)
    {
        // No Fill leaves in this tree.
    }

    /// <summary>The header's shape selector, posting the same signal the GUI board's does.</summary>
    private Action<InputModifier>? SelectViewAction(HomeBoardView view) => _ =>
    {
        bus.Post(new SetHomeBoardViewSignal(view));
        NeedsRedraw = true;
    };

    /// <summary>Looking at a rig, not driving it -- the same two signals the GUI board posts.</summary>
    private Action<InputModifier>? SelectAction(RigCard card) => _ =>
    {
        if (card.IsLocal)
        {
            bus.Post(new SelectLocalContextSignal());
        }
        else
        {
            bus.Post(new SelectRemoteRigSignal(card.Title));
        }
        NeedsRedraw = true;
    };

    // No input override: every card is a Clickable node in the tree, and TuiTabBase dispatches tree hits
    // before delegating -- so the whole screen works with no per-tab wiring.
}
