using System;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// What a panel about a selected celestial object SAYS, and which of its buttons exist -- shared by
    /// the sky atlas and the FITS viewer.
    /// </summary>
    /// <remarks>
    /// <para><b>The content is shared; the placement is not.</b> Every string, row, button and size
    /// lives here, because that is the half that would drift: the atlas panel grew six rows, a comet
    /// sparkline and three actions over months, and a second copy in the viewer would have started as
    /// one line and diverged from the first edit. Where the panel SITS is the host's own business --
    /// the atlas floats it bottom-left of its content rect over a whole-sky projection, the viewer
    /// puts it over a photograph -- so each host does its own <c>RenderLayout</c> with its own rects,
    /// and nothing here knows about either geometry.</para>
    /// <para><b>An action is a callback, not a flag.</b> A null callback means the button is not
    /// offered, so a host cannot switch a button on and leave it wired to nothing, and this class
    /// needs no knowledge of signals, planners or mounts to describe a Goto. The atlas passes four
    /// (Goto, View in Planner, Pin, Solve &amp; Sync); the viewer passes Close and Open-in-atlas and
    /// gets a panel with no mount actions on it at all.</para>
    /// <para><b>The rows a host cannot answer are omitted, not blanked.</b> Alt/Az and rise/transit/set
    /// need a SITE, and a photograph does not always carry one -- so they are options rather than
    /// always-present rows showing dashes. See <see cref="PanelDisplayOptions"/>.</para>
    /// </remarks>
    public static class ObjectInfoPanel
    {
        /// <summary>The panel's design width, before DPI. Fits "View in Planner" without clipping.</summary>
        public const float DesignWidth = 348f;

        /// <summary>Design font size for the body rows; the title and subtitle scale off it.</summary>
        public const float DesignFontSize = 12f;

        /// <summary>One text row's design height.</summary>
        public const float DesignRowHeight = DesignFontSize * 1.35f;

        /// <summary>The title row is slightly taller than the five below it.</summary>
        public const float DesignTitleRowHeight = DesignRowHeight * 1.15f;

        /// <summary>Design height of the comet magnitude sparkline, when one is drawn.</summary>
        public const float DesignSparklineHeight = 30f;

        /// <summary>Design height of an action button.</summary>
        public const float DesignButtonHeight = 24f;

        /// <summary>Design size of the square close affordance at the top right.</summary>
        public const float DesignCloseSize = 20f;

        /// <summary>
        /// Which optional pieces this host can answer for. Everything here defaults to the CHEAPEST
        /// answer, so a host that states nothing gets the four rows that need no site and no actions.
        /// </summary>
        /// <param name="ShowAltAz">
        /// The object's altitude and azimuth. Needs a site; omitted entirely rather than shown as
        /// dashes, because a row of dashes reads as "the sky is broken" rather than "this frame does
        /// not say where it was taken".
        /// </param>
        /// <param name="ShowRiseSet">Rise / transit / set, likewise site-dependent.</param>
        /// <param name="TimeZone">
        /// The offset those times are stated in. The atlas uses the planner's site zone; the viewer
        /// uses the frame's own capture offset, so the panel's clock agrees with the timestamp in the
        /// metadata beside it.
        /// </param>
        /// <param name="Footnote">
        /// One dim line under the rows, or null. What the viewer says here is which INSTANT the
        /// site-dependent rows answer for -- they describe the night the photograph was taken, not
        /// tonight, and nothing else on the panel would give that away.
        /// </param>
        /// <param name="Sparkline">Leave room under the rows for a host-drawn magnitude curve.</param>
        public readonly record struct PanelDisplayOptions(
            bool ShowAltAz = false,
            bool ShowRiseSet = false,
            TimeSpan TimeZone = default,
            string? Footnote = null,
            bool Sparkline = false);

        /// <summary>
        /// What the panel's buttons DO. A null callback is a button that is not offered.
        /// </summary>
        /// <param name="Close">Dismiss the panel.</param>
        /// <param name="Goto">Slew a mount to the object.</param>
        /// <param name="GotoEnabled">
        /// False draws Goto disabled rather than hiding it -- an object that never rises from the
        /// current site still has the affordance, greyed, which is what says why.
        /// </param>
        /// <param name="ViewInPlanner">Open the planner on this target.</param>
        /// <param name="TogglePin">Pin or unpin the object.</param>
        /// <param name="IsPinned">Which way <paramref name="TogglePin"/> currently reads.</param>
        /// <param name="SolveSync">
        /// Solve the current frame and sync the mount. The mount entry's one meaningful action, and it
        /// REPLACES the other three: a goto to where the mount already reports being is a no-op and
        /// pinning it makes no sense.
        /// </param>
        /// <param name="SolveInProgress">Draws Solve &amp; Sync busy and unclickable.</param>
        /// <param name="OpenInAtlas">Open this object in the web sky atlas.</param>
        public readonly record struct PanelActions(
            Action? Close = null,
            Action? Goto = null,
            bool GotoEnabled = true,
            Action? ViewInPlanner = null,
            Action? TogglePin = null,
            bool IsPinned = false,
            Action? SolveSync = null,
            bool SolveInProgress = false,
            Action? OpenInAtlas = null)
        {
            /// <summary>Whether any button at all is offered, i.e. whether the row needs its height.</summary>
            public bool HasButtons
                => Goto is not null || ViewInPlanner is not null || TogglePin is not null
                   || SolveSync is not null || OpenInAtlas is not null;
        }

        /// <summary>
        /// The colours the panel draws in. Passed rather than read from the theme so the atlas keeps
        /// the chrome it has always had while the viewer uses theme-derived colours -- the atlas's are
        /// hand-picked literals from before there was a palette, and quietly re-tinting a shipped panel
        /// is not part of sharing its layout.
        /// </summary>
        public readonly record struct PanelPalette(
            RGBAColor32 Background,
            RGBAColor32 Border,
            RGBAColor32 Text,
            RGBAColor32 DimText,
            RGBAColor32 ButtonText,
            RGBAColor32 GotoBg,
            RGBAColor32 DisabledBg,
            RGBAColor32 ViewBg,
            RGBAColor32 PinBg,
            RGBAColor32 UnpinBg);

        /// <summary>
        /// How many text rows this panel shows: name, subtitle, RA/Dec and brightness always, then
        /// Alt/Az, rise/set and the footnote when the host can answer for them.
        /// </summary>
        public static int RowCount(in PanelDisplayOptions options)
            => 4
               + (options.ShowAltAz ? 1 : 0)
               + (options.ShowRiseSet ? 1 : 0)
               + (options.Footnote is { Length: > 0 } ? 1 : 0);

        /// <summary>The design height of the text block alone.</summary>
        public static float DesignTextBlockHeight(in PanelDisplayOptions options)
            => DesignTitleRowHeight + (DesignRowHeight * (RowCount(in options) - 1));

        /// <summary>
        /// The whole panel's design height: the rows, the sparkline when there is one, the button row
        /// when any button is offered, and the padding that keeps them apart.
        /// </summary>
        public static float DesignHeight(in PanelDisplayOptions options, in PanelActions actions)
        {
            var height = DesignTextBlockHeight(in options) + 8f;
            if (options.Sparkline)
            {
                height += DesignSparklineHeight + 4f;
            }
            if (actions.HasButtons)
            {
                height += DesignButtonHeight + 16f;
            }
            return height;
        }

        /// <summary>
        /// Designation, constellation and type, joined by two spaces -- whichever of the three the
        /// object actually has.
        /// </summary>
        /// <remarks>
        /// A planet carries no catalogue designation but DOES have a type and a current constellation,
        /// so an empty designation must not suppress those, nor show the "(no designation)" placeholder
        /// while there is still real information to show. That placeholder is for the case where all
        /// three are empty and the row would otherwise be blank.
        /// </remarks>
        public static string SubtitleLine(in SkyMapInfoPanelData info)
        {
            var subtitle = info.Canonical ?? "";
            var constell = info.Constellation != default ? info.Constellation.ToIAUAbbreviation() : "";
            var objType = info.ObjType != ObjectType.Unknown ? info.ObjType.ToName() : "";

            if (constell.Length > 0)
            {
                subtitle = subtitle.Length > 0 ? $"{subtitle}  {constell}" : constell;
            }
            if (objType.Length > 0)
            {
                subtitle = subtitle.Length > 0 ? $"{subtitle}  {objType}" : objType;
            }

            return subtitle.Length == 0 ? "(no designation)" : subtitle;
        }

        /// <summary>The position, sexagesimal, as every other panel in this app states it.</summary>
        public static string CoordinateLine(in SkyMapInfoPanelData info)
            => $"RA {CoordinateUtils.HoursToHMS(info.RA)}   Dec {CoordinateUtils.DegreesToDMS(info.Dec)}";

        /// <summary>
        /// Magnitude, surface brightness, B-V and angular size -- each omitted when the catalogue does
        /// not know it, rather than printed as a dash.
        /// </summary>
        /// <remarks>
        /// Surface brightness carries no unit here: the panel is narrow and mag/arcsec squared is the
        /// astronomer default (the planner's details panel spells it out). Magnitude is the one field
        /// that shows a dash when unknown, because a brightness panel with no brightness row at all
        /// reads as a rendering fault.
        /// </remarks>
        public static string BrightnessLine(in SkyMapInfoPanelData info)
        {
            var magPart = float.IsNaN(info.VMag) ? "mag -" : $"mag {info.VMag:F2}";
            var sbPart = float.IsNaN(info.SurfaceBrightness) ? "" : $"   SB {info.SurfaceBrightness:F2}";
            var bvPart = float.IsNaN(info.BMinusV) ? "" : $"   B-V {info.BMinusV:F2}";
            var sizePart = info.AngularSizeDeg is { } s ? $"   size {s * 60:F1}'" : "";

            return $"{magPart}{sbPart}{bvPart}{sizePart}";
        }

        /// <summary>Altitude and azimuth, or dashes when the site is known but the object's are not.</summary>
        public static string AltAzLine(in SkyMapInfoPanelData info)
            => double.IsNaN(info.AltDeg)
                ? "Alt -  Az -"
                : $"Alt {info.AltDeg:+0.0;-0.0}°   Az {info.AzDeg:F1}°";

        /// <summary>
        /// Rise, transit and set -- collapsing to one statement for an object that never sets or never
        /// rises, since three times are meaningless for either.
        /// </summary>
        public static string RiseSetLine(in SkyMapInfoPanelData info, TimeSpan timeZone)
            => info.NeverRises ? "Never rises (below horizon)"
                : info.Circumpolar ? $"Circumpolar   Transit {FormatHHMM(info.TransitTime, timeZone)}"
                : $"Rise {FormatHHMM(info.RiseTime, timeZone)}   Transit {FormatHHMM(info.TransitTime, timeZone)}   Set {FormatHHMM(info.SetTime, timeZone)}";

        /// <summary>
        /// The text rows, as one tree. What each row SAYS is one of the builders above, so the strings
        /// can be asserted without arranging a layout.
        /// </summary>
        public static Layout.Node BuildTextRows(
            in SkyMapInfoPanelData info, in PanelDisplayOptions options, in PanelPalette palette)
        {
            const float dFont = DesignFontSize;
            const float dRow = DesignRowHeight;

            var subtitle = SubtitleLine(in info);
            var raDec = CoordinateLine(in info);
            var magLine = BrightnessLine(in info);

            var rows = new Layout.Node[RowCount(in options)];
            var next = 0;
            rows[next++] = Layout.Builder
                .Text(info.Name, dFont * 1.1f, palette.Text, TextAlign.Near, TextAlign.Near)
                .RowH(DesignTitleRowHeight);
            rows[next++] = Layout.Builder.Text(subtitle, dFont * 0.9f, palette.DimText).RowH(dRow);
            rows[next++] = Layout.Builder.Text(raDec, dFont, palette.Text).RowH(dRow);
            rows[next++] = Layout.Builder.Text(magLine, dFont, palette.Text).RowH(dRow);

            if (options.ShowAltAz)
            {
                rows[next++] = Layout.Builder.Text(AltAzLine(in info), dFont, palette.Text).RowH(dRow);
            }

            if (options.ShowRiseSet)
            {
                rows[next++] = Layout.Builder
                    .Text(RiseSetLine(in info, options.TimeZone), dFont, palette.Text).RowH(dRow);
            }

            if (options.Footnote is { Length: > 0 } footnote)
            {
                rows[next++] = Layout.Builder.Text(footnote, dFont * 0.85f, palette.DimText).RowH(dRow);
            }

            return Layout.Builder.VStack(rows);
        }

        /// <summary>
        /// The action row, right-aligned, or null when the host offered no actions.
        /// </summary>
        /// <remarks>
        /// A leading Star spacer pushes the buttons right and a trailing fixed one holds the margin;
        /// the buttons are Clickable Text leaves, so what is drawn IS what is hit. Widths and gaps are
        /// DESIGN units -- the caller's <c>RenderLayout</c> re-applies the DPI scale.
        /// </remarks>
        public static Layout.Node? BuildButtonRow(in PanelActions actions, in PanelPalette palette)
        {
            if (!actions.HasButtons)
            {
                return null;
            }

            const float dFont = DesignFontSize;

            // The mount entry: Solve & Sync replaces the ordinary three (see PanelActions.SolveSync).
            if (actions.SolveSync is { } solveSync)
            {
                var solving = actions.SolveInProgress;
                var node = Layout.Builder
                    .Text(solving ? "Solving ..." : "Solve & Sync", dFont, palette.ButtonText,
                        TextAlign.Center, TextAlign.Center)
                    .WFixed(110f).HStar()
                    .Bg(solving ? palette.DisabledBg : palette.GotoBg);

                if (!solving)
                {
                    node = node.Clickable(new HitResult.ButtonHit("ObjectInfoSolveSync"), _ => solveSync());
                }

                return Layout.Builder.HStack(
                    Layout.Builder.Spacer().WStar(),
                    node,
                    Layout.Builder.Spacer().WFixed(10f).HStar());
            }

            // Up to four buttons, each present only if it has a handler, separated by fixed gaps.
            var parts = new System.Collections.Generic.List<Layout.Node>(9)
            {
                Layout.Builder.Spacer().WStar(),
            };

            void Add(Layout.Node node)
            {
                if (parts.Count > 1)
                {
                    parts.Add(Layout.Builder.Spacer().WFixed(8f).HStar());
                }
                parts.Add(node);
            }

            if (actions.Goto is { } go)
            {
                var node = Layout.Builder
                    .Text("Goto", dFont, palette.ButtonText, TextAlign.Center, TextAlign.Center)
                    .WFixed(90f).HStar()
                    .Bg(actions.GotoEnabled ? palette.GotoBg : palette.DisabledBg);
                if (actions.GotoEnabled)
                {
                    node = node.Clickable(new HitResult.ButtonHit("ObjectInfoGoto"), _ => go());
                }
                Add(node);
            }

            if (actions.ViewInPlanner is { } view)
            {
                // Wider and a smaller font than its neighbours: the longer label clipped at 90.
                Add(Layout.Builder
                    .Text("View in Planner", dFont * 0.9f, palette.ButtonText,
                        TextAlign.Center, TextAlign.Center)
                    .WFixed(116f).HStar().Bg(palette.ViewBg)
                    .Clickable(new HitResult.ButtonHit("ObjectInfoViewInPlanner"), _ => view()));
            }

            if (actions.OpenInAtlas is { } atlas)
            {
                Add(Layout.Builder
                    .Text("Atlas", dFont, palette.ButtonText, TextAlign.Center, TextAlign.Center)
                    .WFixed(80f).HStar().Bg(palette.ViewBg)
                    .Clickable(new HitResult.ButtonHit("ObjectInfoOpenInAtlas"), _ => atlas()));
            }

            if (actions.TogglePin is { } pin)
            {
                Add(Layout.Builder
                    .Text(actions.IsPinned ? "Unpin" : "Pin", dFont, palette.ButtonText,
                        TextAlign.Center, TextAlign.Center)
                    .WFixed(90f).HStar()
                    .Bg(actions.IsPinned ? palette.UnpinBg : palette.PinBg)
                    .Clickable(new HitResult.ButtonHit("ObjectInfoPinToggle"), _ => pin()));
            }

            parts.Add(Layout.Builder.Spacer().WFixed(10f).HStar());
            return Layout.Builder.HStack([.. parts]);
        }

        /// <summary>
        /// The close affordance, or null when the host did not offer one. A draw==hit Text leaf, so the
        /// glyph box and the click surface are the same arranged rect.
        /// </summary>
        public static Layout.Node? BuildCloseButton(in PanelActions actions, in PanelPalette palette)
            => actions.Close is { } close
                ? Layout.Builder
                    .Text("X", DesignFontSize * 0.9f, palette.DimText, TextAlign.Center, TextAlign.Center)
                    .Stretch()
                    .Clickable(new HitResult.ButtonHit("ObjectInfoClose"), _ => close())
                : null;

        /// <summary>A clock time in the panel's stated offset, or dashes when there is none.</summary>
        public static string FormatHHMM(DateTimeOffset? t, TimeSpan timeZone)
            => t is { } dt ? $"{dt.ToOffset(timeZone):HH:mm}" : "--:--";
    }
}
