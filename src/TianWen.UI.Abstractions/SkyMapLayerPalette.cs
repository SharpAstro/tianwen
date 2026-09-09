using System;
using DIR.Lib;

// Layout is aliased project-wide (a global using), so the tree reads as Layout.Builder.* here without
// a local alias and the collision-prone barewords (Node, Content) stay out of scope.

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The sky map's layer palette: one clickable row per <see cref="SkyMapLayers.All"/> entry,
    /// floating against the content area's right edge.
    ///
    /// <para><b>Pure on purpose.</b> It returns a <see cref="Layout.Node"/> and touches no surface, so
    /// its geometry and its click bindings are testable with a stub measure context, exactly as
    /// <c>PlannerTab.BuildFrameLayout</c> is. The tab renders what this returns and binds the clicks
    /// from the same arranged rect, so draw == hit by construction.</para>
    ///
    /// <para><b>Each row prints its key, so the palette IS the legend.</b> The ten layers were
    /// reachable only by a bare letter with nothing on screen naming it -- and on the web build, which
    /// hosts the same tab on a device with no keyboard, not reachable at all. A row that says
    /// <c>G  Grid</c> answers both at once, and teaches the key to a reader who would rather press
    /// it.</para>
    /// </summary>
    /// <remarks>
    /// The colours are this map's own dark literals rather than <see cref="GuiTheme"/>'s palette,
    /// matching the search and info panels beside it: a star chart is dark in every theme, and chrome
    /// drawn over it has to stay legible against the sky rather than against the app.
    /// </remarks>
    public static class SkyMapLayerPalette
    {
        /// <summary>Panel width in DESIGN units; the renderer applies the DPI scale.</summary>
        public const float PanelWidth = 124f;

        /// <summary>Height of one layer row, design units.</summary>
        public const float RowHeight = 19f;

        /// <summary>Inset from the edge it floats against, design units.</summary>
        public const float Margin = 10f;

        /// <summary>
        /// How far down the right edge the panel starts, design units. Equal to <see cref="Margin"/>
        /// deliberately: the clamp keeps the panel at least a margin inside the rect on every side, so
        /// a smaller default is silently overridden and the panel does not sit where the constant
        /// says. It was 8 against a margin of 10, which measured as a 2-unit lie.
        /// </summary>
        public const float TopOffset = Margin;

        // Alpha is the point of these, not decoration. This panel sits ON the sky, and an opaque card
        // is a hole punched in the thing the reader came to look at -- the first cut set 0xE0 on the
        // panel and then 0xFF on every row, which is most of its area, so it was opaque in all but
        // name. The ON rows stay the most solid of the three because "which layers are on" is the one
        // thing the panel says that the old status-strip hint could not, and that has to survive a
        // bright star field behind it.
        private static readonly RGBAColor32 PanelBg     = new(0x14, 0x14, 0x1C, 0x9C);
        private static readonly RGBAColor32 HeaderInk   = new(0x9A, 0x9A, 0xA8, 0xFF);
        private static readonly RGBAColor32 GripBg      = new(0x2A, 0x2A, 0x36, 0xB4);
        private static readonly RGBAColor32 RowOnBg     = new(0x37, 0x48, 0x5C, 0xDC);
        private static readonly RGBAColor32 RowOffBg    = new(0x20, 0x20, 0x2A, 0xA0);
        private static readonly RGBAColor32 RowHoverBg  = new(0x44, 0x56, 0x6A, 0xE6);
        private static readonly RGBAColor32 KeyChipBg   = new(0x2C, 0x2C, 0x36, 0xC8);
        private static readonly RGBAColor32 OnInk       = new(0xE8, 0xE8, 0xF0, 0xFF);
        private static readonly RGBAColor32 OffInk      = new(0x9A, 0x9A, 0xA6, 0xFF);
        private static readonly RGBAColor32 DisabledInk = new(0x5A, 0x5A, 0x64, 0xFF);

        /// <summary>
        /// The action id the grip carries, so the render pass can find its arranged rect and a
        /// mouse-down can be tested against the very rect that was drawn.
        /// </summary>
        public const string GripAction = "SkyMapLayerPaletteGrip";

        /// <summary>The action id a row's <see cref="HitResult.ButtonHit"/> carries, keyed by the
        /// layer's key label so a test can name a row without depending on its index.</summary>
        public static string RowAction(in SkyMapLayer layer) => "SkyMapLayer:" + layer.KeyLabel;

        /// <summary>
        /// Builds the palette, floated against <see cref="Layout.DockSide.Right"/> of whatever rect it is
        /// arranged into. Arrange it against the map's own content rect: the clamp is the engine's, so
        /// a window resize or a sidebar opening under the panel shoves it back into view instead of
        /// stranding it off-screen.
        /// </summary>
        /// <param name="state">Read for each layer's on/off and availability.</param>
        /// <param name="fontSize">Row text size in DESIGN units.</param>
        /// <param name="onToggle">Invoked with the layer a click landed on.</param>
        public static Layout.Node Build(SkyMapState state, float fontSize, Action<SkyMapLayer> onToggle)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(onToggle);

            var layers = SkyMapLayers.All;

            // Header plus one row per layer. Built into an array rather than a params span because the
            // count is the table's, not a literal.
            var children = new Layout.Node[layers.Length + 1];

            // The header IS the grip. A separate handle would cost a row and teach nothing: a title
            // bar is where a reader already tries to drag a panel from, and the Move cursor over it
            // says so before they try. The drag itself is the tab's (it owns the pointer), and this
            // only has to be findable in the arranged tree -- hence the action id.
            children[0] = Layout.Builder.Text("LAYERS", fontSize * 0.85f, HeaderInk,
                    TextAlign.Near, TextAlign.Center)
                .RowH(RowHeight * 0.9f)
                .Bg(GripBg)
                .Clickable(new HitResult.ButtonHit(GripAction), _ => { }, CursorKind.Move);

            for (var i = 0; i < layers.Length; i++)
            {
                // Copy out of the ImmutableArray so the click lambda captures a value rather than the
                // loop's index into a collection it would have to re-read.
                var layer = layers[i];
                var available = layer.Available(state);
                var on = available && layer.IsOn(state);
                var ink = !available ? DisabledInk : on ? OnInk : OffInk;

                var row = Layout.Builder.HStack(
                        Layout.Builder.Text(layer.KeyLabel, fontSize, ink,
                                TextAlign.Center, TextAlign.Center)
                            .WFixed(13f).HStar().Bg(KeyChipBg),
                        Layout.Builder.Text(layer.Label, fontSize, ink,
                                TextAlign.Near, TextAlign.Center)
                            .WStar().HStar())
                    .WithGap(5f)
                    .RowH(RowHeight)
                    .Bg(on ? RowOnBg : RowOffBg);

                // A layer with nothing to draw is shown and dimmed rather than hidden: a row that comes
                // and goes is a palette whose shape moves under the pointer, and "Milky Way, greyed"
                // tells the reader the texture is missing where an absent row tells them nothing.
                if (available)
                {
                    row = row.BgHover(RowHoverBg)
                        .Clickable(new HitResult.ButtonHit(RowAction(in layer)),
                            _ => onToggle(layer), CursorKind.Pointer);
                }

                children[i + 1] = row;
            }

            var panel = Layout.Builder.VStack(children)
                .WFixed(PanelWidth)
                .Bg(PanelBg)
                .Pad(5f)
                .WithGap(2f);

            return Layout.Builder.Anchored(panel, Layout.DockSide.Right,
                offsetAlong: state.LayerPaletteOffset, margin: Margin);
        }
    }
}
