using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The Enhance popover: what the sparkles button opens, one panel for the viewer's enhancement (#1345, P36 of
    /// <c>docs/plans/viewer-prerelease-fixes.md</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Two halves, one rule.</b> <see cref="ViewerState.EnhancesAsPlanet"/> says which shows: the deep-sky AI
    /// program for a frame, the planetary sharpening for a SER, the live stack or a planetary master. The deep-sky models
    /// do not serve a planetary frame, so neither half shares the popover with the other. The planetary half is a
    /// placeholder until the strip's controls move in.</para>
    /// <para><b>The panel runs nothing.</b> Run, Cancel and Off all press the Enhance button with no mouse button, which is
    /// what <c>E</c> does, so the one toggle (run, cancel, revert) is the controller's, through the host's press policy.
    /// The backend is the one thing the panel writes (<see cref="ViewerState.PreferredEnhanceBackend"/>), and only among the
    /// backends the controller found to serve (<see cref="ViewerState.EnhanceChoices"/>), which is the only way to choose
    /// one now that the right-click cycle is gone.</para>
    /// </remarks>
    partial class ImageRendererBase<TSurface>
    {
        /// <summary>The widest run button label, so Run and Cancel do not change the row under the pointer.</summary>
        private const string EnhanceRunWidthSample = "Cancel";

        private const string EnhanceOffLabel = "Off";
        private const string EnhanceCheckingText = "Checking what serves this frame...";
        private const string EnhancePlanetPlaceholder = "Planetary sharpening: its controls move here from the info strip.";

        /// <summary>Presses the Enhance button as <c>E</c> does: through the host's policy, to the controller's toggle.</summary>
        private void PressEnhanceToggle(ViewerState state)
        {
            PressToolbarButton(state, ToolbarAction.Enhance, MouseButton.None);
            state.NeedsRedraw = true;
        }

        private Layout.Node BuildEnhanceTree(ViewerState state)
        {
            var rows = ImmutableArray.CreateBuilder<Layout.Node>();
            var small = BaseFontSize * 0.85f;

            if (state.EnhancesAsPlanet)
            {
                rows.Add(Layout.Builder.Text(EnhancePlanetPlaceholder, small, ViewerTheme.Palette.DimText).WStar());
            }
            else
            {
                AddDeepSkyRows(rows, state, small);
            }

            // Outer: a one-unit border ring around the padded body; the body swallows a press anywhere on it.
            return Layout.Builder.VStack(
                    Layout.Builder.VStack(rows.ToImmutable().AsSpan())
                        .Pad(BasePanelPadding)
                        .Bg(ViewerTheme.InfoPanelBg)
                        .Clickable(new HitResult.ButtonHit("EnhancePanelBackground"), _ => { })
                        .WStar())
                .Pad(1f)
                .Bg(ViewerTheme.Palette.SeparatorStrong)
                .WStar();
        }

        private void AddDeepSkyRows(ImmutableArray<Layout.Node>.Builder rows, ViewerState state, float small)
        {
            var choices = state.EnhanceChoices;
            if (choices.IsDefaultOrEmpty)
            {
                rows.Add(Layout.Builder.Text(EnhanceCheckingText, small, ViewerTheme.Palette.DimText).WStar());
            }
            else
            {
                // Only the backends that serve: one way to choose.
                var options = new Layout.ButtonGroupOption<EnhanceBackend>[choices.Length];
                for (var i = 0; i < choices.Length; i++)
                {
                    options[i] = new(choices[i].Backend, choices[i].Label) { Hit = new HitResult.ButtonHit($"EnhanceBackend{choices[i].Backend}") };
                }
                rows.Add(Layout.Builder.ButtonGroup<EnhanceBackend>(options, state.PreferredEnhanceBackend,
                        chosen =>
                        {
                            state.PreferredEnhanceBackend = chosen;
                            state.NeedsRedraw = true;
                        },
                        PlanetaryChoiceStyle, BaseFontSize)
                    .WStar());

                // The program as the canonical one would run it from what serves, one step to a row.
                var selected = choices[0];
                foreach (var choice in choices)
                {
                    if (choice.Backend == state.PreferredEnhanceBackend)
                    {
                        selected = choice;
                    }
                }
                for (var i = 0; i < selected.Steps.Length; i++)
                {
                    rows.Add(Layout.Builder.Text($"{i + 1}. {selected.Steps[i]}", small, ViewerTheme.Palette.DimText).WStar());
                }
            }

            // One toggle: Run starts what E starts, Cancel stops it with its progress beside it, Off reverts. Each is
            // registered even when it cannot act (PanelButton), so a press lands on it and stops there.
            var running = state.IsEnhancing;
            var canRun = !running && !state.IsEnhanced && IsToolbarButtonEnabled(ToolbarAction.Enhance, _document);
            var run = running
                ? PanelButton("Cancel", "EnhanceCancel", true, () => PressEnhanceToggle(state), EnhanceRunWidthSample)
                : PanelButton("Run", "EnhanceRun", canRun, () => PressEnhanceToggle(state), EnhanceRunWidthSample);
            var off = PanelButton(EnhanceOffLabel, "EnhanceOff", state.IsEnhanced && !running, () => PressEnhanceToggle(state), EnhanceOffLabel);
            rows.Add(Layout.Builder.HStack(run, off,
                    Layout.Builder.Text(running ? $"{state.EnhanceProgressPct:F0}%" : string.Empty, BaseFontSize,
                        ViewerTheme.Palette.DimText, widthSample: "100%"),
                    Layout.Builder.Spacer().WStar())
                .WithGap(WbGap)
                .CrossCenter()
                .WStar());
        }

        private void RenderEnhancePanel(ViewerState state)
        {
            // The engine paints nothing for a closed popover; this is the measure the frame does not pay for.
            if (!state.EnhancePopover.IsOpen)
            {
                return;
            }

            // Nothing to hang it from: the button is not on this bar. It closes rather than floating at a guess.
            if (!TryGetPaintedToolbarRect(ToolbarAction.Enhance, out var anchor))
            {
                state.EnhancePopover.Close();
                return;
            }

            var ctx = MeasureContext();
            var content = BuildEnhanceTree(state).WStar(1f, BaseInfoPanelWidth);
            var tree = Layout.Builder.Popover(anchor, content, state.EnhancePopover);
            PaintLayout(ArrangeLayout(tree, new RectF32(0f, 0f, Width, Height), ctx), ctx);
        }
    }
}
