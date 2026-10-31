using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    partial class ImageRendererBase<TSurface>
    {
        // 6 a-trous detail scales, finest first. Linear gain over ViewerState.WaveletDialRange: 0 to 10, widened to hold every gain the
        // last derivation gave (#1435); neutral 1.0. Drawn for the stacked, Best and planetary master views.
        private const int WaveletBandCount = 6;

        // -----------------------------------------------------------------------
        // Info panel
        // -----------------------------------------------------------------------

        /// <summary>Between the lines of one section, design units: a text leaf is its line box, which sets lines edge to edge.</summary>
        private const float InfoLineGap = 2f;

        /// <summary>Between two sections of the strip, design units: an empty line.</summary>
        private const float InfoSectionGap = BaseFontSize;

        /// <summary>
        /// The docked info strip, as ONE tree the engine measures, arranges and paints: the metadata, the statistics, the
        /// frame's grade and the Cursor readout at the bottom. It REPORTS and holds no control but the Statistics heading.
        /// </summary>
        /// <remarks>
        /// <para>The metadata and the statistics read the SOURCE (InfoPanelData), so every source shows them; only the Grade
        /// section is a document's (its sharpening controls are the Enhance popover's planetary half). A SER's statistics
        /// are the shown frame's once it settles (the heading names the frame the table describes).</para>
        /// <para><b>The Statistics table opens where the strip has the room for it</b> and rolls up to its heading where it
        /// has not: it is the tallest block here and the one most frames never need, so it gives way first. The room is the
        /// engine's measure of the whole strip with the table in it, the Cursor readout's room included even while the
        /// pointer is off the picture (an empty block as tall as the readout gets), or the table would come and go as the
        /// pointer crossed the frame's edge. The heading's click pins the other state
        /// (<see cref="ViewerState.InfoPanelStatisticsPinned"/>), and a pin the room agrees with is dropped.</para>
        /// <para><b>The Cursor readout sits at the bottom</b>, under a spacer, because it exists only while the pointer is
        /// over the picture: anywhere above the sliders it would shove them up and down as the pointer entered and left, a
        /// control moving under the hand about to grab it.</para>
        /// </remarks>
        private void RenderInfoPanel(IPreviewSource source, ViewerState state)
        {
            if (string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            var document = source as AstroImageDocument;

            // Info-panel rect from the single layout pass (docked right by the Split's content Dock).
            var panel = _layout.InfoPanel;
            FillRect(panel.X, panel.Y, panel.Width, panel.Height, ViewerTheme.InfoPanelBg);
            var rect = new RectF32(panel.X, panel.Y, panel.Width, panel.Height);

            var ctx = MeasureContext();
            var roomy = Layout.Engine.Measure(BuildInfoStrip(source, state, document, statisticsOpen: true, toggle: null, ctx, rect.Width),
                new Layout.Size<float>(rect.Width, float.MaxValue), ctx).Height <= rect.Height;
            if (state.InfoPanelStatisticsPinned == roomy)
            {
                state.InfoPanelStatisticsPinned = null;
            }

            var open = state.InfoPanelStatisticsPinned ?? roomy;
            var tree = BuildInfoStrip(source, state, document, open, () =>
            {
                state.InfoPanelStatisticsPinned = !open == roomy ? null : !open;
                state.NeedsRedraw = true;
            }, ctx, rect.Width);

            PaintLayout(ArrangeLayout(tree, rect, ctx), ctx, DrawSectionRule);
        }

        /// <summary>Test seam: the strip's tree as the painted frame builds it, statistics open, so a test measures it at any width.</summary>
        internal Layout.Node InfoStripFor(IPreviewSource source, ViewerState state, float width)
            => BuildInfoStrip(source, state, source as AstroImageDocument, statisticsOpen: true, toggle: null, MeasureContext(), width);

        /// <summary>Test seam: the engine's measure of <see cref="InfoStripFor"/> in a strip <paramref name="width"/> wide, as the paint measures it.</summary>
        internal Layout.Size<float> MeasureInfoStrip(IPreviewSource source, ViewerState state, float width)
            => Layout.Engine.Measure(InfoStripFor(source, state, width), new Layout.Size<float>(width, float.MaxValue), MeasureContext());

        /// <summary>Test seam: how far right of a strip <paramref name="width"/> wide the painted tree reaches (arranged, as the paint places it).</summary>
        internal float InfoStripRightEdge(IPreviewSource source, ViewerState state, float width)
        {
            var arranged = ArrangeLayout(InfoStripFor(source, state, width), new RectF32(0f, 0f, width, 100000f), MeasureContext());
            var right = 0f;
            foreach (var node in arranged)
            {
                // The containers fill the strip by construction; what can run past its edge is a word or a row inside them.
                if (node.Bounds.Width < width)
                {
                    right = Math.Max(right, node.Bounds.X + node.Bounds.Width);
                }
            }
            return right;
        }

        /// <param name="ctx">The strip's measure context, which decides how the statistics table's columns fall into tables.</param>
        /// <param name="width">The strip's width on the surface, its padding included.</param>
        private Layout.Node BuildInfoStrip(IPreviewSource source, ViewerState state, AstroImageDocument? document,
            bool statisticsOpen, Action? toggle, PixelMeasureContext<TSurface> ctx, float width)
        {
            var sections = ImmutableArray.CreateBuilder<Layout.Node>();
            var body = ViewerTheme.Palette.BodyText;

            var metadata = ImmutableArray.CreateBuilder<Layout.Node>();
            metadata.Add(SectionHeading("Metadata"));
            foreach (var line in InfoPanelData.GetMetadataLines(source, state))
            {
                metadata.Add(WrappedText(line, body));
            }
            sections.Add(Section(metadata));

            // A table, not lines: the numbers line up on their decimal points in the strip's proportional face. The mark
            // is text rather than an icon: the equipment tab's convention for the same thing (Advanced [+]).
            var statistics = ImmutableArray.CreateBuilder<Layout.Node>();
            statistics.Add(SectionHeading(InfoPanelData.GetStatisticsHeading(source, statisticsOpen),
                new HitResult.ButtonHit("ToggleStatistics"), toggle));
            if (statisticsOpen)
            {
                var (header, rows) = InfoPanelData.GetStatisticsTable(source);
                statistics.Add(StatisticsTable(header, rows,
                    table => Layout.Engine.Measure(InStrip(table), new Layout.Size<float>(float.MaxValue, float.MaxValue), ctx).Width <= width));
            }
            sections.Add(Section(statistics));

            if (document is not null)
            {
                // How this frame compares with the best of its group graded so far (LightGrading): the grade the file list
                // colours its row by, in numbers, its first line in the grade's colour.
                if (InfoPanelData.GetGradeReadout(state, document) is { } readout)
                {
                    var grade = ImmutableArray.CreateBuilder<Layout.Node>();
                    grade.Add(SectionHeading("Grade"));
                    for (var i = 0; i < readout.Lines.Length; i++)
                    {
                        grade.Add(WrappedText(readout.Lines[i], i == 0 && readout.Relative is { } relative
                            ? LightGrading.Colour(ViewerTheme.Palette, relative)
                            : body));
                    }
                    if (readout.BestName is { } bestName)
                    {
                        // A file name is one long word: trimmed at its end to the strip, never wrapped past it.
                        grade.Add(Layout.Builder.HStack(
                                Layout.Builder.Text("Best:", BaseFontSize, body, trim: TextTrim.None),
                                Layout.Builder.Text(bestName, BaseFontSize, body, trim: TextTrim.End).WStar())
                            .WithGap(BaseFontSize / 3)
                            .WStar());
                    }
                    sections.Add(Section(grade));
                }
            }

            // The live rolling stack's picture is some frames behind the playhead and some seconds old: one line saying so, worded by
            // the controller (ViewerState.LiveStackBuiltLine) and set only for the Live view, never the Auto view or Raw.
            if (state.LiveStackBuiltLine is { } builtLine && source is LiveStackPreviewSource { FollowsStream: true })
            {
                sections.Add(Section([SectionHeading("Live stack"), WrappedText(builtLine, body)]));
            }

            // The white-balance sliders and the selection used to be sections here: a toolbar popover now
            // (ImageRendererBase.WhiteBalancePanel.cs), and a panel floating over the picture
            // (ImageRendererBase.SelectionPanel.cs).
            // The planet and telescope and the wavelet sharpening were sections here: the Enhance popover's planetary half now
            // (ImageRendererBase.EnhancePanel.cs). The strip reports and holds no control.
            // The keyboard-shortcut list that sat at the bottom is gone: a shortcut rides its button's tooltip, and the rest
            // live behind the toolbar's "?" list.
            sections.Add(Layout.Builder.Spacer().HStar());
            sections.Add(CursorSection(source, state));

            return InStrip(Layout.Builder.VStack([.. sections]).WithGap(InfoSectionGap));
        }

        /// <summary>The strip's padding, stated once: the strip itself and the statistics table measured for its break take it.</summary>
        private static Layout.Node InStrip(Layout.Node content) => content.Pad(BasePanelPadding);

        /// <summary>
        /// A section heading: the name, then a hairline rule across the rest of the row (drawn into a fill by
        /// <see cref="DrawSectionRule"/>), the whole row answering <paramref name="onClick"/> when it is given one.
        /// </summary>
        /// <remarks>
        /// The headings used to be written as <c>"-- Statistics --"</c>, which spends four characters of a narrow panel on
        /// decoration the heading COLOUR already provides. A drawn rule is also font-independent: the tidier characters for
        /// the job (an em dash, box-drawing U+2500) are the kind of codepoint a host face can lack.
        /// </remarks>
        private static Layout.Node SectionHeading(string title, HitResult? hit = null, Action? onClick = null)
            => Layout.Builder.HStack(
                    Layout.Builder.Text(title, BaseFontSize, ViewerTheme.Palette.HeaderText, vAlign: TextAlign.Near,
                        trim: TextTrim.None),
                    Layout.Builder.Fill(key: SectionRuleKey).WStar())
                .WithGap(BaseFontSize * 0.5f)
                .Clickable(hit, onClick is null ? null : _ => onClick())
                .WStar();

        /// <summary>A heading and its lines, a line gap apart.</summary>
        private static Layout.Node Section(ImmutableArray<Layout.Node>.Builder rows) => Section(rows.ToImmutable());

        private static Layout.Node Section(ImmutableArray<Layout.Node> rows)
            => Layout.Builder.VStack([.. rows]).WithGap(InfoLineGap).WStar();

        /// <summary>
        /// The statistics as columns: the first left-aligned, every other right-aligned, so the numbers line up on their
        /// decimal points in a proportional face, each column as wide as its widest cell, the header row dimmed. Cells are
        /// never trimmed: a truncated number is a wrong number, so the columns go into as few tables as fit across the strip,
        /// one under the other, each beside the same row labels and without the rows that have nothing in its columns.
        /// </summary>
        /// <param name="fitsAcross">Whether a table fits across the strip, by the engine's measure of it, so the face and the
        /// numbers on show decide where the table breaks. A column count fixed by hand (five abreast) was measured in the
        /// platform's monospace face, and the bundled one put a FITS colour frame's five columns 9 units past the strip.</param>
        private static Layout.Node StatisticsTable(ImmutableArray<string> header, ImmutableArray<ImmutableArray<string>> rows,
            Func<Layout.Node, bool> fitsAcross)
        {
            var tables = ImmutableArray.CreateBuilder<Layout.Node>();
            for (var first = 1; first < header.Length;)
            {
                // The most of the columns left that fit beside the labels; one at the least, even where that one does not.
                var count = header.Length - first;
                var table = Table(header, rows, first, count);
                while (count > 1 && !fitsAcross(table))
                {
                    table = Table(header, rows, first, --count);
                }
                tables.Add(table);
                first += count;
            }
            return tables.Count == 1 ? tables[0] : Layout.Builder.VStack([.. tables]).WithGap(InfoLineGap);

            // The label column and `count` columns from `first`.
            static Layout.Node Table(ImmutableArray<string> header, ImmutableArray<ImmutableArray<string>> rows, int first, int count)
            {
                var shown = rows.RemoveAll(row => Enumerable.Range(first, count).All(c => c >= row.Length || string.IsNullOrEmpty(row[c])));
                var columns = new Layout.Node[count + 1];
                for (var i = 0; i < columns.Length; i++)
                {
                    var c = i == 0 ? 0 : first + i - 1;
                    var align = c == 0 ? TextAlign.Near : TextAlign.Far;
                    var cells = new Layout.Node[shown.Length + 1];
                    cells[0] = Cell(header[c], ViewerTheme.Palette.DimText, align);
                    for (var r = 0; r < shown.Length; r++)
                    {
                        cells[r + 1] = Cell(c < shown[r].Length ? shown[r][c] : string.Empty, ViewerTheme.Palette.BodyText, align);
                    }
                    columns[i] = Layout.Builder.VStack(cells).WithGap(InfoLineGap);
                }

                return Layout.Builder.HStack(columns).WithGap(BaseFontSize * 0.5f);
            }

            static Layout.Node Cell(string text, RGBAColor32 color, TextAlign align)
                => Layout.Builder.Text(text, BaseFontSize, color, hAlign: align, trim: TextTrim.None).WStar();
        }

        /// <summary>
        /// The Cursor readout, or while the pointer is off the picture an empty block as tall as the readout gets over this
        /// frame (<see cref="InfoPanelData.GetWidestCursorLines"/>): the room is kept, so nothing above it is placed by
        /// whether the pointer happens to be over the picture.
        /// </summary>
        private static Layout.Node CursorSection(IPreviewSource source, ViewerState state)
        {
            var rows = ImmutableArray.CreateBuilder<Layout.Node>();
            if (state.CursorPixelInfo is not null)
            {
                rows.Add(SectionHeading("Cursor"));
                foreach (var line in InfoPanelData.GetCursorLines(state))
                {
                    rows.Add(Layout.Builder.Text(line, BaseFontSize, ViewerTheme.Palette.BodyText, trim: TextTrim.None));
                }
            }
            else
            {
                var lines = 1 + InfoPanelData.GetWidestCursorLines(source.ChannelCount, source.Findings.Wcs is not null,
                    state.ChannelView).Count;
                for (var i = 0; i < lines; i++)
                {
                    rows.Add(Layout.Builder.Text(string.Empty, BaseFontSize, ViewerTheme.Palette.BodyText));
                }
            }
            return Section(rows);
        }

        // -----------------------------------------------------------------------
        // Wavelet-sharpen layer sliders (info panel; the stacked, Best and planetary master views)
        //
        // The Registax / AstroSurface 6-layer convention: one slider per a-trous detail scale, finest first.
        // Linear gain over ViewerState.WaveletDialRange, neutral 1.0. Dragging a layer turns sharpening on and re-pushes
        // the params; the controller re-sharpens the cached stacked master off-thread (no re-stack), so the
        // image follows within a frame or two. Same press + drag + release model as the WB sliders.
        // -----------------------------------------------------------------------

        /// <summary>Inter-row and inter-column gap for the wavelet block, design units.</summary>
        private const float WaveletGap = 6f;

        /// <summary>
        /// The band-number column and the value column, at their widest readings: a derived gain runs negative and past ten (12.7 and
        /// -1.4 on a 12-inch Jupiter), which a "0.0" column cut to an ellipsis.
        /// </summary>
        private const string WaveletBandWidthSample = "6";
        private const string WaveletValueWidthSample = "-00.0";

        // One dial per band, living across frames because the tree does not: a Content.Slider leaf
        // carries a REFERENCE to caller-owned state, and the drag the engine arms on a press holds that
        // same reference until the release. Seeded from ViewerState at the top of every build, exactly
        // as the white-balance dials one section up are.
        //
        // What each carries is the track FRACTION, not the gain: a SliderState's range is 0..1 and the
        // gain runs over ViewerState.WaveletDialRange, so the scale lives in the two conversions either side,
        // which is where it lived when a drag mapped a cursor X onto a gain by hand.
        private readonly SliderState[] _waveletSliders =
            [new SliderState(), new SliderState(), new SliderState(),
             new SliderState(), new SliderState(), new SliderState()];

        /// <summary>Test seam: one band's dial, so a test can find its arranged leaf by identity.</summary>
        internal SliderState WaveletSliderState(int band) => _waveletSliders[band];

        /// <summary>
        /// One band's row: number, track, gain. The track is a <see cref="Layout.Content.Slider"/> leaf,
        /// so the engine draws it, registers it and arms its drag from the rect it painted -- draw ==
        /// drag by construction, where this used to read the band's track back out of the region list
        /// on every pointer move.
        /// </summary>
        private Layout.Node WaveletRow(int band, float gain, RGBAColor32 fill)
            => Layout.Builder.HStack(
                    Layout.Builder.Text((band + 1).ToString(), BaseFontSize, ViewerTheme.Palette.BodyText,
                        widthSample: WaveletBandWidthSample),
                    Layout.Builder.Slider(_waveletSliders[band], fill, TrackChrome).HStar(),
                    Layout.Builder.Text(gain.ToString("0.0"), BaseFontSize, ViewerTheme.Palette.DimText,
                        hAlign: TextAlign.Far, widthSample: WaveletValueWidthSample))
                .WithGap(WaveletGap)
                .CrossCenter()
                .WStar();

        /// <summary>
        /// A sentence that wraps rather than clips: a word a node in a flow (as the profile panel's notice wraps), a third of the font
        /// between words. Star in WIDTH only: its height is what the wrap measures, and a Star height shared a column's leftover between
        /// its sentences, so one wrapped to two lines was laid over the next.
        /// </summary>
        private Layout.Node WrappedText(string text, RGBAColor32 color)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var nodes = new Layout.Node[words.Length];
            for (var i = 0; i < words.Length; i++)
            {
                nodes[i] = Layout.Builder.Text(words[i], BaseFontSize, color);
            }
            return Layout.Builder.WrapH(nodes).WithGap(BaseFontSize / 3).WStar();
        }

        /// <summary>
        /// The wavelet block as one tree: an action row over one row per band. Nothing here is
        /// positioned and nothing is measured by hand; the engine is told what the content is and does
        /// both.
        /// </summary>
        private Layout.Node BuildWaveletTree(ViewerState state)
        {
            var gains = state.WaveletGains;

            // Brighter track when active; dim when sharpening is off. The sliders still WORK while dim
            // -- a drag re-enables -- they just read as inactive, which is the behaviour the hand-laid
            // version had and the reason this is a colour rather than a disabled node.
            var fill = state.WaveletSharpenEnabled
                ? RGBAColor32.FromFloat(0.45f, 0.72f, 0.78f, 1f)
                : RGBAColor32.FromFloat(0.40f, 0.45f, 0.48f, 1f);

            var rows = ImmutableArray.CreateBuilder<Layout.Node>();

            rows.Add(Layout.Builder.HStack(
                    PanelButton(
                        state.WaveletSharpenEnabled ? "Sharpen: On" : "Sharpen: Off",
                        "WaveletToggle", enabled: true,
                        onPress: () =>
                        {
                            state.WaveletSharpenEnabled = !state.WaveletSharpenEnabled;
                            state.WaveletDirty = true;
                            state.NeedsRedraw = true;
                        },
                        // Widest of the two labels, stated once on the node, so toggling it cannot move
                        // the Reset button beside it.
                        widthSample: "Sharpen: Off",
                        background: state.WaveletSharpenEnabled ? ViewerTheme.Palette.Selection : ChoiceFill),
                    PanelButton("Reset", "WaveletReset", enabled: true,
                        onPress: () =>
                        {
                            state.WaveletGains = WaveletSharpenOptions.PlanetaryDefault.Gains;
                            state.WaveletDerived = false;
                            state.DerivedWaveletGains = null;
                            state.WaveletDeriveNote = null;
                            state.WaveletDirty = true;
                            state.NeedsRedraw = true;
                        }),
                    // The derived sharpening's gains for the master on show, worked out in the background and seeded into the
                    // dials (#1159): the planet and telescope come from the section above.
                    PanelButton(state.WaveletDeriving ? "Deriving..." : "Derive", "WaveletDerive", enabled: !state.WaveletDeriving,
                        onPress: () =>
                        {
                            state.WaveletDeriveRequested = true;
                            state.NeedsRedraw = true;
                        },
                        widthSample: "Deriving..."),
                    Layout.Builder.Spacer().HStar())
                .WithGap(WaveletGap)
                .CrossCenter()
                .WStar());

            var caption = state.WaveletDeriving ? WaveletDerivation.Caption(state.WaveletDeriveElapsed, state.WaveletDeriveStage) : state.WaveletDeriveNote;
            if (caption is not null)
            {
                rows.Add(WrappedText(caption, ViewerTheme.Palette.DimText));
            }

            for (var b = 0; b < WaveletBandCount && b < gains.Length; b++)
            {
                rows.Add(WaveletRow(b, gains[b], fill));
            }

            return Layout.Builder.VStack([.. rows]).WithGap(WaveletGap);
        }

        /// <summary>
        /// Seeds each band's dial from the gains before the strip's tree is built: a slider leaf carries a REFERENCE to its
        /// dial, and the drag the engine arms on a press holds that same reference until the release.
        /// </summary>
        private void SeedWaveletDials(ViewerState state)
        {
            var gains = state.WaveletGains;
            // One range for every band, so the dials compare as positions, read once for both directions of the mapping.
            var (min, max) = state.WaveletDialRange;
            var span = max - min;
            for (var b = 0; b < WaveletBandCount && b < gains.Length; b++)
            {
                var band = b;
                _waveletSliders[b].Value = Math.Clamp((gains[b] - min) / span, 0f, 1f);
                _waveletSliders[b].OnChanged = frac =>
                {
                    if (_state is not { } dragState)
                    {
                        return;
                    }

                    // Touching a layer turns sharpening on. That rule used to live in a public
                    // BeginWaveletDragAt that two hosts had to remember to call; it is now on the only
                    // path that can move a gain at all.
                    dragState.WaveletSharpenEnabled = true;
                    dragState.WaveletGains = dragState.WaveletGains.SetItem(band, min + (frac * span));
                    dragState.WaveletDirty = true;
                    dragState.NeedsRedraw = true;
                };
            }
        }

    }
}
