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
    /// (Goto, View in Planner, Pin, Solve &amp; Sync); the viewer passes Close and the sky-atlas link and
    /// gets a panel with no mount actions on it at all.</para>
    /// <para><b>Where an action LEAVES the app it is a link, not a button.</b> The object's Wikipedia
    /// article and the web sky atlas open a browser, so they read as links: underlined words in the
    /// palette's link colour, on a row of their own above the buttons, with the hand pointer. Each is a
    /// <see cref="HitResult.LinkHit"/>, so the web host draws a real anchor and a desktop host opens the
    /// page through its router's <c>OpenUrl</c>. A link is a URL for the reason an action is a callback:
    /// null is a link that is not offered.</para>
    /// <para><b>The rows a host cannot answer are omitted, not blanked.</b> Alt/Az and rise/transit/set
    /// need a SITE, and a photograph does not always carry one -- so they are options rather than
    /// always-present rows showing dashes. See <see cref="PanelDisplayOptions"/>.</para>
    /// </remarks>
    public static class ObjectInfoPanel
    {
        /// <summary>
        /// The panel's design width, before DPI. Widened 300 -> 320 -> 348 while it lived in the
        /// atlas, to fit three action buttons (Goto / View in Planner / Pin) without the longest
        /// label clipping.
        /// </summary>
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
        /// <param name="Picture">
        /// The object's picture, when its verified article has one (<see cref="ICelestialObjectDB.TryGetArticle"/>),
        /// shown under the rows with its credit. Sized from the aspect ratio the table records, so the panel is
        /// its final height before a single byte of the picture has arrived and does not jump when it does.
        /// </param>
        public readonly record struct PanelDisplayOptions(
            bool ShowAltAz = false,
            bool ShowRiseSet = false,
            TimeSpan TimeZone = default,
            string? Footnote = null,
            bool Sparkline = false,
            ObjectArticleImage? Picture = null);

        /// <summary>What the article link reads.</summary>
        public const string ArticleLinkLabel = "Wikipedia";

        /// <summary>What the sky-atlas link reads.</summary>
        public const string AtlasLinkLabel = "Sky atlas";

        /// <summary>The link row's design height: a text row and the underline under it.</summary>
        public const float DesignLinkRowHeight = DesignRowHeight + 2f;

        /// <summary>The key of the picture slot's <see cref="Layout.Content.Fill"/>, which the host draws into.</summary>
        public const string PictureFillKey = "ObjectInfoPicture";

        /// <summary>The key of the large view's slot, drawn by the same host hook at a bigger size.</summary>
        public const string LargePictureFillKey = "ObjectInfoPictureLarge";

        /// <summary>The share of the host's content rect the large view may cover.</summary>
        private const float LargePictureShare = 0.8f;

        /// <summary>The picture's design width: the panel's, less an 8-unit margin either side.</summary>
        public const float DesignPictureWidth = DesignWidth - 16f;

        /// <summary>The shortest and tallest the picture is drawn, whatever its aspect ratio.</summary>
        public const float DesignPictureMinHeight = 60f;

        /// <inheritdoc cref="DesignPictureMinHeight"/>
        public const float DesignPictureMaxHeight = 220f;

        /// <summary>
        /// The picture's design height at <see cref="DesignPictureWidth"/>, from the table's recorded size and
        /// clamped, so a panorama does not become a sliver and a tall frame does not push the buttons off.
        /// </summary>
        public static float DesignPictureHeight(in ObjectArticleImage image)
            => image.Width > 0 && image.Height > 0
                ? Math.Clamp(DesignPictureWidth * image.Height / image.Width, DesignPictureMinHeight, DesignPictureMaxHeight)
                : DesignPictureMinHeight;

        /// <summary>
        /// The picture a selected object's panel shows: its verified article's lead image, or null for an object
        /// with no article, an article with no picture the bake kept, or no catalogue to ask.
        /// </summary>
        public static ObjectArticleImage? PictureFor(CatalogIndex? index, ICelestialObjectDB? db)
            => index is { } catalogIndex && db is not null
               && db.TryGetArticle(catalogIndex, out var article) && article.Image is { } image
                ? image
                : null;

        /// <summary>
        /// The article a selected object's Wikipedia link opens: its verified article's URL, or null for an
        /// object with no article or no catalogue to ask, which gets no link rather than a guessed one.
        /// </summary>
        public static string? ArticleUrlFor(CatalogIndex? index, ICelestialObjectDB? db)
            => index is { } catalogIndex && db is not null && db.TryGetArticle(catalogIndex, out var article)
                ? article.Url
                : null;

        /// <summary>The picture section's whole design height: margins, the picture and its credit row.</summary>
        public static float DesignPictureSectionHeight(in ObjectArticleImage image)
            => 4f + DesignPictureHeight(in image) + DesignRowHeight + 4f;

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
        /// <param name="ArticleUrl">
        /// The object's verified Wikipedia article (<see cref="ArticleUrlFor"/>), offered as a link. Null for an
        /// object the bake did not verify: no link rather than a guessed one.
        /// </param>
        /// <param name="AtlasUrl">This object in the web sky atlas (<see cref="SkyAtlasLink"/>), offered as a link.</param>
        /// <param name="AtlasOpened">
        /// What the host does beside the browser opening <paramref name="AtlasUrl"/>: the viewer says so on its
        /// status line. Opening the page is the link's own job, never this callback's, so it cannot open twice.
        /// </param>
        public readonly record struct PanelActions(
            Action? Close = null,
            Action? Goto = null,
            bool GotoEnabled = true,
            Action? ViewInPlanner = null,
            Action? TogglePin = null,
            bool IsPinned = false,
            Action? SolveSync = null,
            bool SolveInProgress = false,
            string? ArticleUrl = null,
            string? AtlasUrl = null,
            Action? AtlasOpened = null)
        {
            /// <summary>Whether any button at all is offered, i.e. whether the row needs its height.</summary>
            public bool HasButtons
                => Goto is not null || ViewInPlanner is not null || TogglePin is not null
                   || SolveSync is not null;

            /// <summary>Whether any link is offered, i.e. whether the link row needs its height.</summary>
            public bool HasLinks => ArticleUrl is { Length: > 0 } || AtlasUrl is { Length: > 0 };
        }

        /// <summary>
        /// The colours the panel draws in. Passed rather than read from the theme so the atlas keeps
        /// the chrome it has always had while the viewer uses theme-derived colours -- the atlas's are
        /// hand-picked literals from before there was a palette, and quietly re-tinting a shipped panel
        /// is not part of sharing its layout.
        /// </summary>
        /// <param name="Link">
        /// The links' words and underline. Both hosts pass the theme's accent, which is what makes a link read
        /// as one and follows the palette into Night, where a fixed blue would be the brightest thing on screen.
        /// </param>
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
            RGBAColor32 UnpinBg,
            RGBAColor32 Link);

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
            if (options.Picture is { } picture)
            {
                height += DesignPictureSectionHeight(in picture);
            }
            if (actions.HasLinks)
            {
                height += DesignLinkRowHeight + (actions.HasButtons ? 4f : 8f);
            }
            if (actions.HasButtons)
            {
                height += DesignButtonHeight + 16f;
            }
            return height;
        }

        /// <summary>
        /// The link row's top edge, in design units below the panel's top: just above the button row when there
        /// is one, else above the bottom margin. Both hosts place the row with this, so it cannot sit on the
        /// buttons in one host and float in the other.
        /// </summary>
        public static float DesignLinkRowTop(in PanelDisplayOptions options, in PanelActions actions)
            => DesignHeight(in options, in actions)
               - (actions.HasButtons ? DesignButtonHeight + 16f + 4f : 8f)
               - DesignLinkRowHeight;

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
        /// The credit a picture is shown with: who made it and under what licence, which is what CC BY and
        /// CC BY-SA require wherever the picture appears. The artist where Commons names one, else its credit
        /// text; long values are cut, since the whole credit is one click away on the file page.
        /// </summary>
        public static string CreditLine(in ObjectArticleImage image)
        {
            const int MaxWho = 48;
            var who = image.Artist is { Length: > 0 } artist ? artist : image.Credit;
            if (who.Length > MaxWho)
            {
                who = who[..MaxWho].TrimEnd() + "...";
            }
            return who.Length > 0 ? $"{who}, {image.Licence}" : image.Licence;
        }

        /// <summary>
        /// The picture section: the slot the host draws the picture into (a keyed
        /// <see cref="Layout.Content.Fill"/>, backed dark so it reads as a picture frame while loading) and the
        /// credit under it, which links to the Commons file page.
        /// </summary>
        /// <remarks>
        /// The credit is a <see cref="HitResult.LinkHit"/> on the node, so the web host renders it as a real
        /// anchor and a desktop host opens it in the browser, from this one declaration.
        /// </remarks>
        public static Layout.Node BuildPictureSection(in ObjectArticleImage image, in PanelPalette palette, Action? onOpen = null)
        {
            var credit = CreditLine(in image);
            var filePage = image.FilePageUrl;
            var slot = Layout.Builder.Fill(key: PictureFillKey).WStar().HStar().Bg(PictureFrame);
            if (onOpen is not null)
            {
                slot = slot.Clickable(new HitResult.ButtonHit("ObjectInfoPictureOpen"), _ => onOpen(), CursorKind.Pointer);
            }

            return Layout.Builder.VStack(
                Layout.Builder.Spacer().RowH(4f),
                Layout.Builder.HStack(
                    Layout.Builder.Spacer().WFixed(8f).HStar(),
                    slot,
                    Layout.Builder.Spacer().WFixed(8f).HStar())
                    .RowH(DesignPictureHeight(in image)),
                Layout.Builder.HStack(
                    Layout.Builder.Spacer().WFixed(8f).HStar(),
                    Layout.Builder.Text(credit, DesignFontSize * 0.8f, palette.DimText, TextAlign.Near, TextAlign.Center)
                        .WStar().HStar()
                        .Clickable(new HitResult.LinkHit(filePage), cursor: CursorKind.Pointer),
                    Layout.Builder.Spacer().WFixed(8f).HStar())
                    .RowH(DesignRowHeight),
                Layout.Builder.Spacer().RowH(4f));
        }

        // A picture frame is near-black in every theme, as image data is never re-tinted: a dim grey would
        // read as a mat around the picture rather than the absence of one.
        private static readonly RGBAColor32 PictureFrame = new RGBAColor32(0x08, 0x08, 0x0C, 0xFF);

        // What the large view dims its host behind: dark enough that the picture is what the eye lands on,
        // and translucent so the sky (or the photograph) is still recognisably there.
        private static readonly RGBAColor32 LargePictureScrim = new RGBAColor32(0x00, 0x00, 0x00, 0xC8);

        /// <summary>
        /// Where the large view's picture goes in <paramref name="content"/>: the object's own aspect ratio, as
        /// large as fits in <see cref="LargePictureShare"/> of it, centred, with its credit row below.
        /// </summary>
        public static RectF32 LargePictureRect(RectF32 content, in ObjectArticleImage image, float dpiScale)
        {
            var creditRow = DesignRowHeight * dpiScale;
            var boxW = content.Width * LargePictureShare;
            var boxH = (content.Height * LargePictureShare) - creditRow;
            var aspect = image.Width > 0 && image.Height > 0 ? (float)image.Height / image.Width : 0.75f;
            var w = MathF.Min(boxW, boxH / aspect);
            var h = w * aspect;
            return new RectF32(
                content.X + ((content.Width - w) / 2f),
                content.Y + ((content.Height - h - creditRow) / 2f),
                w, h);
        }

        /// <summary>
        /// The large view: a scrim over the whole content rect that dismisses it, the picture in its own slot,
        /// and the credit under it. Drawn by the host after everything else, so it covers what it dims.
        /// </summary>
        /// <param name="pictureDesignHeight">
        /// The picture slot's height in DESIGN units: the height of the rect <see cref="LargePictureRect"/>
        /// placed it in, divided by the DPI scale the host arranges this tree at. The rect is in pixels and
        /// the tree is not; stating the pixel height here arranged a slot 1.5x too tall on a 1.5x screen, so
        /// the picture sat below the centre under a black band and the credit row fell off the window.
        /// </param>
        public static (Layout.Node Scrim, Layout.Node Picture) BuildLargePicture(
            in ObjectArticleImage image, in PanelPalette palette, float pictureDesignHeight, Action close)
        {
            var credit = CreditLine(in image) + "   (Esc closes)";
            var filePage = image.FilePageUrl;
            var scrim = Layout.Builder.Spacer().Bg(LargePictureScrim)
                .Clickable(new HitResult.ButtonHit("ObjectInfoPictureClose"), _ => close());
            var body = Layout.Builder.VStack(
                Layout.Builder.Fill(key: LargePictureFillKey).WStar().HStar().Bg(PictureFrame)
                    .RowH(pictureDesignHeight),
                Layout.Builder.Text(credit, DesignFontSize * 0.9f, palette.DimText, TextAlign.Center, TextAlign.Center)
                    .RowH(DesignRowHeight)
                    .Clickable(new HitResult.LinkHit(filePage), cursor: CursorKind.Pointer));
            return (scrim, body);
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
                    node = node.Clickable(new HitResult.ButtonHit("ObjectInfoSolveSync"), _ => solveSync())
                        .BgHover(ButtonHover(palette.GotoBg, in palette));
                }

                return Layout.Builder.HStack(
                    Layout.Builder.Spacer().WStar(),
                    node,
                    Layout.Builder.Spacer().WFixed(10f).HStar());
            }

            // Up to three buttons, each present only if it has a handler, separated by fixed gaps. Every one
            // that can act lights under the pointer; a disabled Goto does not, since nothing would happen.
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
                    node = node.Clickable(new HitResult.ButtonHit("ObjectInfoGoto"), _ => go())
                        .BgHover(ButtonHover(palette.GotoBg, in palette));
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
                    .BgHover(ButtonHover(palette.ViewBg, in palette))
                    .Clickable(new HitResult.ButtonHit("ObjectInfoViewInPlanner"), _ => view()));
            }

            if (actions.TogglePin is { } pin)
            {
                var pinFill = actions.IsPinned ? palette.UnpinBg : palette.PinBg;
                Add(Layout.Builder
                    .Text(actions.IsPinned ? "Unpin" : "Pin", dFont, palette.ButtonText,
                        TextAlign.Center, TextAlign.Center)
                    .WFixed(90f).HStar()
                    .Bg(pinFill)
                    .BgHover(ButtonHover(pinFill, in palette))
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
                    .BgHover(ButtonHover(palette.Background, in palette))
                    .Clickable(new HitResult.ButtonHit("ObjectInfoClose"), _ => close())
                : null;

        /// <summary>
        /// The link row, left-aligned under the text column, or null when the host offered no links: the
        /// Wikipedia article when the object has a verified one, then the sky atlas.
        /// </summary>
        public static Layout.Node? BuildLinkRow(in PanelActions actions, in PanelPalette palette)
        {
            if (!actions.HasLinks)
            {
                return null;
            }

            var parts = new System.Collections.Generic.List<Layout.Node>(4);
            if (actions.ArticleUrl is { Length: > 0 } article)
            {
                parts.Add(Link(ArticleLinkLabel, article, opened: null, in palette));
            }

            if (actions.AtlasUrl is { Length: > 0 } atlas)
            {
                if (parts.Count > 0)
                {
                    parts.Add(Layout.Builder.Spacer().WFixed(12f).HStar());
                }
                parts.Add(Link(AtlasLinkLabel, atlas, actions.AtlasOpened, in palette));
            }

            parts.Add(Layout.Builder.Spacer().WStar());
            return Layout.Builder.HStack([.. parts]);
        }

        /// <summary>
        /// One link: its word in <see cref="PanelPalette.Link"/>, underlined at the word's own width, with the
        /// hand pointer and a hover tint. The <see cref="HitResult.LinkHit"/> is on the whole node, the padding
        /// included, so the press target is a little larger than the word, as a button's is.
        /// </summary>
        /// <remarks>
        /// The underline is the word's width because the stack is as wide as its widest child, the word, and a
        /// Star child stretches across it. <paramref name="opened"/> runs beside the router opening the page,
        /// never instead of it (see <see cref="PanelActions.AtlasOpened"/>).
        /// </remarks>
        private static Layout.Node Link(string label, string url, Action? opened, in PanelPalette palette)
            => Layout.Builder.VStack(
                    Layout.Builder.Text(label, DesignFontSize, palette.Link, TextAlign.Near, TextAlign.Center).HStar(),
                    Layout.Builder.Spacer().Bg(palette.Link).WStar().HFixed(1f))
                .PadX(3f)
                .HStar()
                .BgHover(GuiTheme.Hover(palette.Background, palette.Link))
                .Clickable(new HitResult.LinkHit(url), opened is null ? null : _ => opened(), CursorKind.Pointer);

        /// <summary>A button's fill under the pointer, tinted toward the text drawn on it (<see cref="GuiTheme.Hover(RGBAColor32, RGBAColor32)"/>).</summary>
        private static RGBAColor32 ButtonHover(RGBAColor32 fill, in PanelPalette palette) => GuiTheme.Hover(fill, palette.Text);

        /// <summary>A clock time in the panel's stated offset, or dashes when there is none.</summary>
        public static string FormatHHMM(DateTimeOffset? t, TimeSpan timeZone)
            => t is { } dt ? $"{dt.ToOffset(timeZone):HH:mm}" : "--:--";
    }
}
