using System;
using System.Collections.Immutable;
using DIR.Lib;

// Layout is aliased project-wide (a global using), so the tree reads as Layout.Builder.* here without
// a local alias and the collision-prone barewords (Node, Content) stay out of scope.

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The sky map's layer palette: <see cref="SkyMapLayers.All"/> rendered as DIR.Lib's
    /// <see cref="FloatingPalette"/>.
    ///
    /// <para><b>This is an adapter, and deliberately thin.</b> Everything generic about a floating
    /// palette -- the placement and its clamp reconciliation, the grip drag, the double-click collapse,
    /// the idle recede, the drawn-and-dimmed treatment of an unavailable row -- lives in DIR.Lib 8.16,
    /// hoisted there out of this panel and the PDF viewer's tool palette once it was clear both had
    /// built the same thing and had each got a different part of it wrong. What stays here is what is
    /// actually TianWen's: which layers exist, what they are called, the keys they teach, and a star
    /// chart's colours.</para>
    ///
    /// <para><b>Each row prints its key, so the palette IS the legend.</b> The ten layers were reachable
    /// only by a bare letter -- and on the web build, which hosts this same renderer-agnostic tab on
    /// devices with no keyboard, not reachable at all.</para>
    /// </summary>
    /// <remarks>
    /// The colours are this map's own dark literals rather than <see cref="GuiTheme"/>'s palette,
    /// matching the search and info panels beside it: a star chart is dark in every theme, and chrome
    /// drawn over it has to stay legible against the sky rather than against the app.
    /// <para>
    /// The title ink is warm and the count carries the map's own selection yellow. Grey-on-starfield is
    /// the one combination this map has none of -- its labels are warm (planets) or tinted
    /// (constellations) -- so a neutral title reads as haze, and collapsed the count is the panel's
    /// entire state readout.
    /// </para>
    /// </remarks>
    public static class SkyMapLayerPalette
    {
        /// <summary>Panel width in DESIGN units; the renderer applies the DPI scale.</summary>
        public const float PanelWidth = 124f;

        /// <summary>The action id the grip binds.</summary>
        public const string GripAction = "SkyMapLayerPaletteGrip";

        private static readonly PaletteColors Colors = new(
            PanelBg: new(0x14, 0x14, 0x1C, 0x9C),
            GripBg: new(0x2A, 0x2A, 0x36, 0xB4),
            TitleInk: new(0xE8, 0xDC, 0xB4, 0xFF),
            CountInk: new(0xFF, 0xEE, 0x60, 0xFF),
            RowOnBg: new(0x37, 0x48, 0x5C, 0xDC),
            RowOffBg: new(0x20, 0x20, 0x2A, 0xA0),
            RowHoverBg: new(0x44, 0x56, 0x6A, 0xE6),
            KeyChipBg: new(0x2C, 0x2C, 0x36, 0xC8),
            OnInk: new(0xE8, 0xE8, 0xF0, 0xFF),
            OffInk: new(0x9A, 0x9A, 0xA6, 0xFF),
            DisabledInk: new(0x5A, 0x5A, 0x64, 0xFF));

        /// <summary>The action a layer's row carries, keyed by its key label so a test can name a row
        /// without depending on its index.</summary>
        public static string RowAction(in SkyMapLayer layer) => "SkyMapLayer:" + layer.KeyLabel;

        /// <summary>The layer a row action names, or null when it names none.</summary>
        public static SkyMapLayer? LayerForAction(string action)
        {
            foreach (var layer in SkyMapLayers.All)
            {
                if (RowAction(in layer) == action)
                {
                    return layer;
                }
            }

            return null;
        }

        /// <summary>Projects the layer table onto the palette's row type against a given state.</summary>
        /// <param name="state">Read for each layer's on/off and availability.</param>
        /// <param name="includeKeyHints">
        /// Whether each row prints the key that toggles it. True where this map owns the keyboard,
        /// which is the tab hosts; FALSE where it does not, and the viewer is that case -- every one
        /// of these ten letters already means something else there (<c>S</c> detects stars, <c>C</c>
        /// cycles the channel, <c>D</c> the demosaic), so a printed key would be a row teaching a
        /// shortcut that does something quite different. A palette is a control first and a legend
        /// second; where it cannot be the legend it stays the control.
        /// </param>
        public static ImmutableArray<PaletteItem> ItemsFor(SkyMapState state, bool includeKeyHints = true)
        {
            ArgumentNullException.ThrowIfNull(state);

            var builder = ImmutableArray.CreateBuilder<PaletteItem>(SkyMapLayers.All.Length);
            foreach (var layer in SkyMapLayers.Offered(state))
            {
                var available = layer.Available(state);
                builder.Add(new PaletteItem(
                    Label: layer.Label,
                    Action: RowAction(in layer),
                    IsOn: available && layer.IsOn(state),
                    KeyHint: includeKeyHints ? layer.KeyLabel : null,
                    IsAvailable: available));
            }

            return builder.ToImmutable();
        }

        /// <summary>
        /// Builds the palette, floated against the right edge of whatever rect it is arranged into --
        /// the half of the content area the info panel does not use, since that one pins itself
        /// bottom-left above the status strip.
        /// </summary>
        /// <param name="state">Read for each layer's on/off and for the palette's own placement.</param>
        /// <param name="fontSize">Row text size in DESIGN units.</param>
        /// <param name="onToggle">Invoked with the layer a click landed on.</param>
        /// <param name="onGripPress">Invoked when the grip is pressed, to begin a drag or collapse.</param>
        /// <param name="includeKeyHints">See <see cref="ItemsFor"/>: false in a host whose keyboard
        /// these letters do not belong to.</param>
        public static Layout.Node Build(SkyMapState state, float fontSize,
            Action<SkyMapLayer> onToggle, Action onGripPress, bool includeKeyHints = true)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(onToggle);

            return FloatingPalette.Build(
                state.LayerPalette,
                "LAYERS",
                ItemsFor(state, includeKeyHints).AsSpan(),
                in Colors,
                fontSize,
                GripAction,
                action =>
                {
                    if (LayerForAction(action) is { } layer)
                    {
                        onToggle(layer);
                    }
                },
                onGripPress,
                PanelWidth);
        }
    }
}
