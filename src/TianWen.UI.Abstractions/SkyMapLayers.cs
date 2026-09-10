using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// One toggleable layer of the sky map: what it is called, the key that flips it, and how to
    /// read and write it on <see cref="SkyMapState"/>.
    /// </summary>
    /// <param name="Label">Palette text. Short, because ten of them stack into a narrow panel.</param>
    /// <param name="KeyLabel">The key as the palette prints it, so the panel doubles as the legend.</param>
    /// <param name="Key">The key that toggles it.</param>
    /// <param name="IsOn">Reads the layer's flag.</param>
    /// <param name="Set">Writes it.</param>
    /// <param name="IsAvailable">
    /// Whether the layer can be shown at all right now. Null means always. Three answer no, each
    /// because the thing they draw is not there: the milky way's texture is a file beside the
    /// executable (<see cref="SkyMapState.MilkyWayAvailable"/>), the horizon and the Alt/Az grid need
    /// a place on Earth (<see cref="SkyMapState.SiteAvailable"/>), and the mount reticle needs a mount
    /// reporting where it points.
    /// </param>
    public readonly record struct SkyMapLayer(
        string Label,
        string KeyLabel,
        InputKey Key,
        Func<SkyMapState, bool> IsOn,
        Action<SkyMapState, bool> Set,
        Func<SkyMapState, bool>? IsAvailable = null)
    {
        /// <summary>Whether this layer can be toggled against <paramref name="state"/> at all.</summary>
        public bool Available(SkyMapState state) => IsAvailable is null || IsAvailable(state);

        /// <summary>
        /// Flips the layer and asks for a redraw. False when the layer is unavailable, which is what
        /// keeps an unavailable key UNHANDLED rather than silently swallowed: the milky-way key used
        /// to be a <c>when</c> clause on its switch case, so with no texture the press fell through to
        /// whatever else wanted it, and that stays true here.
        /// </summary>
        public bool Toggle(SkyMapState state)
        {
            if (!Available(state))
            {
                return false;
            }

            Set(state, !IsOn(state));
            state.NeedsRedraw = true;
            return true;
        }
    }

    /// <summary>
    /// The sky map's toggleable layers, in the order the palette lists them.
    ///
    /// <para><b>One table, two consumers.</b> The key handler and the palette both read this, so a
    /// layer cannot exist on one and not the other -- which is the state this replaced: ten
    /// hand-written switch cases and no visible control for any of them, on a tab whose web build has
    /// no keyboard to press them with at all.</para>
    ///
    /// <para>Order is how a reader builds a sky rather than how <see cref="SkyMapState"/> declares
    /// its fields: the reference frames first, then what is drawn in them, then the overlays that
    /// answer a question about tonight.</para>
    /// </summary>
    public static class SkyMapLayers
    {
        public static readonly ImmutableArray<SkyMapLayer> All =
        [
            new SkyMapLayer("Grid", "G", InputKey.G,
                static s => s.ShowGrid, static (s, v) => s.ShowGrid = v),
            // The two that need a PLACE on Earth. In the GUI a profile always carries one, so these
            // are effectively always available there; in the FITS viewer the site comes from the
            // photograph's own header and half the frames in the world do not have it.
            new SkyMapLayer("Alt/Az grid", "A", InputKey.A,
                static s => s.ShowAltAzGrid, static (s, v) => s.ShowAltAzGrid = v,
                static s => s.SiteAvailable),
            new SkyMapLayer("Horizon", "H", InputKey.H,
                static s => s.ShowHorizon, static (s, v) => s.ShowHorizon = v,
                static s => s.SiteAvailable),
            new SkyMapLayer("Figures", "C", InputKey.C,
                static s => s.ShowConstellationFigures, static (s, v) => s.ShowConstellationFigures = v),
            new SkyMapLayer("Boundaries", "B", InputKey.B,
                static s => s.ShowConstellationBoundaries, static (s, v) => s.ShowConstellationBoundaries = v),
            new SkyMapLayer("Milky Way", "S", InputKey.S,
                static s => s.ShowMilkyWay, static (s, v) => s.ShowMilkyWay = v,
                static s => s.MilkyWayAvailable),
            new SkyMapLayer("Objects", "O", InputKey.O,
                static s => s.ShowObjectOverlay, static (s, v) => s.ShowObjectOverlay = v),
            new SkyMapLayer("Dark nebulae", "D", InputKey.D,
                static s => s.ShowDarkNebulae, static (s, v) => s.ShowDarkNebulae = v),
            new SkyMapLayer("Comets", "E", InputKey.E,
                static s => s.ShowComets, static (s, v) => s.ShowComets = v),
            // Unavailable with no mount reporting, which is every host but the GUI with a rig
            // connected -- the reticle has nowhere to be.
            new SkyMapLayer("Mount", "M", InputKey.M,
                static s => s.ShowMountOverlay, static (s, v) => s.ShowMountOverlay = v,
                static s => s.MountOverlay is not null),
        ];

        /// <summary>
        /// The layers a host should OFFER, which is <see cref="All"/> minus any the host draws itself.
        /// </summary>
        /// <remarks>
        /// <b>Offered is not the same as available.</b> An unavailable layer is listed and dimmed --
        /// it is one of this map's layers and it simply has nothing to draw right now, which is worth
        /// saying. A layer the HOST draws is not this map's at all: listing it dimmed reads as a
        /// broken button, and listing it live would give the reader two grids. The FITS viewer is the
        /// case: its grid comes per-pixel from the frame's own WCS, stays fine at any zoom, and has
        /// its own key and toolbar rung.
        /// </remarks>
        public static IEnumerable<SkyMapLayer> Offered(SkyMapState state)
        {
            foreach (var layer in All)
            {
                if (layer.Key is InputKey.G && state.GridDrawnByHost)
                {
                    continue;
                }

                yield return layer;
            }
        }

        /// <summary>
        /// Toggles the layer <paramref name="key"/> names, if any. False when no layer claims the key,
        /// the one that does is unavailable, or the host draws it -- so the caller goes on looking for
        /// a handler.
        /// </summary>
        public static bool TryToggleByKey(SkyMapState state, InputKey key)
        {
            foreach (var layer in Offered(state))
            {
                if (layer.Key == key)
                {
                    return layer.Toggle(state);
                }
            }

            return false;
        }
    }
}
