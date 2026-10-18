using System;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The orrery's info panel (#1656): a click on a body opens the sky's own object panel for it, drawn over the orrery without the sky's
/// marks, its disc the body as seen from the orrery's centre (<see cref="OrreryState.PanelVantage"/>). Its rows are the sky's, from the
/// Earth; the disc's line and the footnote say where the disc is seen from.
/// </summary>
public partial class SkyMapTab<TSurface>
{
    /// <summary>The panel the last orrery frame drew, or null: what the tests read.</summary>
    internal SkyMapInfoPanelData? LastOrreryPanel { get; private set; }

    /// <summary>Where the last orrery frame's panel disc was seen from, or null with no disc: what the tests read.</summary>
    internal CatalogIndex? LastOrreryPanelVantage { get; private set; }

    /// <summary>The view the last orrery frame's panel disc was drawn from, or null with no disc: what the tests read.</summary>
    internal PlanetView? LastOrreryPanelView { get; private set; }

    // Drawn over the bodies and under the strip, so the strip and its drop-ups stay on top.
    private void DrawOrreryPanel(PlannerState plannerState, OrreryScene scene, RectF32 contentRect, DateTimeOffset viewingTime)
    {
        (LastOrreryPanel, LastOrreryPanelVantage, LastOrreryPanelView) = (null, null, null);
        var orrery = State.Orrery;
        if (orrery.Selected is not { } selected || !scene.TryFind(selected, out var body)
            || !TryOrreryPanelInfo(plannerState, selected, viewingTime, out var info))
        {
            return;
        }

        // A comet is a dot and has no disc; every other body is drawn from the vantage, at its true diameter from there.
        string? footnote = null;
        PlanetView? view = null;
        var vantage = OrreryState.PanelVantage(selected, orrery.Centre);
        if (selected.ToCatalog() != Catalog.Comet && scene.TryFind(vantage, out var from))
        {
            var seen = PlanetView.FromVantage(selected, viewingTime, (body.X, body.Y, body.Z), (from.X, from.Y, from.Z), out var distanceAu);
            info = info with
            {
                Aspect = seen.ToAspect(viewingTime) with
                {
                    AngularDiameterArcsec = PlanetView.AngularDiameterArcsec(selected, distanceAu),
                    DistanceAu = distanceAu,
                },
            };
            view = seen;
            // Short enough for the panel at 1x in the platform's face ("the rest from the Earth" trimmed there).
            footnote = vantage == CatalogIndex.Earth
                ? $"Disc from the Earth, {distanceAu:F2} AU"
                : $"Disc from {BodyName(vantage)}, {distanceAu:F2} AU; rows from the Earth";
            (LastOrreryPanelVantage, LastOrreryPanelView) = (vantage, seen);
        }

        var magCurve = selected.ToCatalog() == Catalog.Comet
            ? State.GetCometMagnitudeCurveCached(plannerState.Comets, selected, viewingTime)
            : default;
        var onEarth = selected != CatalogIndex.Earth;
        var options = new ObjectInfoPanel.PanelDisplayOptions(
            ShowAltAz: onEarth,
            ShowRiseSet: onEarth,
            TimeZone: plannerState.SiteTimeZone,
            Footnote: footnote,
            Sparkline: magCurve.Length > 0,
            Picture: ObjectInfoPanel.PictureFor(info.Index, plannerState.ObjectDb),
            Disk: view is null ? null : ObjectInfoPanel.DiskLine(in info),
            Disc: view is not null,
            DiscCredit: view is { } v && PlanetTextures.TryGet(v.Body, out _, out var credit) ? credit : null);
        LastPanelDiscCredit = options.DiscCredit;
        LastPanelDiscRect = null;

        var (name, ra, dec, type) = (info.Name, info.RA, info.Dec, info.ObjType);
        var actions = new ObjectInfoPanel.PanelActions(
            Close: CloseOrreryPanel,
            // The Earth has no place on the sky to pin.
            TogglePin: onEarth ? () => PostSignal(new SkyMapPinObjectSignal(name, ra, dec, selected, type)) : null,
            IsPinned: IsPinned(plannerState, selected),
            ArticleUrl: ObjectInfoPanel.ArticleUrlFor(info.Index, plannerState.ObjectDb),
            Centre: selected != orrery.Centre ? () => CentreOrreryOn(selected) : null);

        LastOrreryPanel = info;
        DrawObjectPanelBox(in info, in options, in actions, magCurve, contentRect,
            view is { } shown ? rect => DrawOrreryPanelDisc(shown, viewingTime, rect) : null);
    }

    // The sky's panel rows for the body at the instant: the planet cache's (the Sun, the planets, the Moon), a comet's own ephemeris
    // (the orrery's set reaches past the sky's candidates), and for the Earth only its name, since nothing about it is seen from it.
    private bool TryOrreryPanelInfo(PlannerState plannerState, CatalogIndex index, DateTimeOffset viewingTime, out SkyMapInfoPanelData info)
    {
        var (lat, lon) = (plannerState.SiteLatitude, plannerState.SiteLongitude);
        if (index == CatalogIndex.Earth)
        {
            info = new SkyMapInfoPanelData("Earth", "", ObjectType.Planet, default, double.NaN, double.NaN, float.NaN, float.NaN,
                double.NaN, double.NaN, null, null, null, false, false, null, null, CatalogIndex.Earth);
            return true;
        }
        var site = SiteContext.Create(lat, lon, viewingTime);
        if (index.ToCatalog() == Catalog.Comet)
        {
            if (plannerState.Comets is { } comets && comets.TryGet(index, out var elements)
                && CometEphemeris.TryGetEquatorialJ2000WithMagnitude(elements, viewingTime, out var ra, out var dec, out var mag))
            {
                info = SkyMapSearchActions.CometInfoPanel(comets, index, ra, dec, mag, lat, lon, viewingTime, in site);
                return true;
            }
        }
        else if (plannerState.ObjectDb is { } db && State.TryGetPlanetCached(index, viewingTime, out var planet))
        {
            info = SkyMapSearchActions.PlanetInfoPanel(db, planet, lat, lon, viewingTime, in site);
            return true;
        }
        info = default;
        return false;
    }

    // The body's disc in the panel's slot: the orrery's own disc routine (traced on a GPU host, a CPU sprite otherwise) from the
    // vantage's view, as large as the slot holds it, the sprite in the panel's own cache.
    private void DrawOrreryPanelDisc(in PlanetView view, DateTimeOffset viewingTime, RectF32 slot)
    {
        var radius = (float)PlanetDiscSprite.PanelRadiusPx(view.Body, slot.Width, slot.Height);
        TryDrawOrreryDisc(view, viewingTime, slot.X + (slot.Width / 2), slot.Y + (slot.Height / 2), radius, PanelSprites);
    }

    private string BodyName(CatalogIndex index)
        => index == CatalogIndex.Sol ? "the Sun"
            : index == CatalogIndex.Moon ? "the Moon"
            : index == CatalogIndex.Earth ? "the Earth"
            : OrreryScene.TryFind(index, out var body) ? body.Name
            : index.ToCanonical();

    /// <summary>Closes the orrery's panel (its close button, Escape, a click on nothing); the view and its centre stay as they are.</summary>
    private void CloseOrreryPanel()
    {
        State.Orrery.Selected = null;
        State.NeedsRedraw = true;
    }

    /// <summary>The panel's Centre: the camera centred on the body, following it; on the Sun, back to the Sun.</summary>
    private void CentreOrreryOn(CatalogIndex index)
    {
        var orrery = State.Orrery;
        orrery.Focus = index;
        orrery.CentredOnFocus = index != CatalogIndex.Sol;
        State.NeedsRedraw = true;
    }

    /// <summary>Escape in the orrery: the panel first, then the view.</summary>
    private void EscapeOrrery()
    {
        if (State.Orrery.Selected is not null)
        {
            CloseOrreryPanel();
        }
        else
        {
            CloseOrrery();
        }
    }
}
