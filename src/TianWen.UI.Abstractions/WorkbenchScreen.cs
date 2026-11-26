using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DIR.Lib;
using TianWen.Lib.Imaging.Workbench;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The processing app's guided screen as ONE layout tree: a top bar (the project and the image), the step bar left to right,
/// the selected step's panel at the left and the canvas. The canvas is a keyed fill the engine places; the viewer's own
/// renderer is embedded in it (<c>SetContentRegion</c>), so a master looks the same here as in the viewer.
/// </summary>
/// <remarks>
/// Nothing here is positioned by a sum of constants and nothing is measured by hand: the engine is told what each part is.
/// Colours are the theme's alone, so Night is judged by the palette. There is nothing in the top bar for a screen that does
/// not exist yet (Stack, Process, Workflow).
/// </remarks>
public sealed class WorkbenchScreen<TSurface> : CompositeWidget<TSurface>
{
    /// <summary>The key of the fill the viewer is embedded in.</summary>
    internal const string CanvasKey = "WorkbenchCanvas";

    private static float BaseFontSize => GuiTheme.Metrics.BaseFontSize;
    private const float PanelWidth = 340f;
    private const float StepBarHeight = 52f;
    private const float Gap = 8f;
    private const float TopBarHeight = 44f;

    private readonly ImageRendererBase<TSurface> _viewer;
    private readonly IReadOnlyList<PixelWidgetBase<TSurface>> _children;
    private readonly ListScrollController _panelScroll = new();
    private readonly ListCursor _imageCursor = new();

    /// <param name="renderer">The surface the screen and the viewer draw on.</param>
    /// <param name="viewer">The viewer embedded in the canvas; it takes this widget's window context.</param>
    public WorkbenchScreen(Renderer<TSurface> renderer, ImageRendererBase<TSurface> viewer) : base(renderer)
    {
        _viewer = viewer;
        // The viewer was configured on its own (its face, its scale) before it was embedded, and embedding replaces its
        // context with this widget's: keep what it resolved.
        FontPath = viewer.FontPath;
        EmojiFontPath = viewer.EmojiFontPath;
        DpiScale = viewer.DpiScale;
        _children = [viewer];
        ShareUiContext(viewer);
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<PixelWidgetBase<TSurface>> Children => _children;

    /// <summary>The viewer is painted over the canvas this widget lays out, after this widget's own tree.</summary>
    protected override int ChildrenUnderOwn => 0;

    /// <summary>Where the canvas was placed by the last frame, in device pixels.</summary>
    public RectF32 CanvasRect { get; private set; }

    /// <summary>Paints the screen and the viewer in its canvas.</summary>
    public void Render(WorkbenchState state, WorkbenchActions actions, ViewerState viewerState, IPreviewSource? source, float width, float height)
    {
        BeginFrame();
        var palette = GuiTheme.Palette;
        FillRect(0, 0, width, height, palette.ContentBg);

        var arranged = RenderLayout(Build(state, actions), new RectF32(0, 0, width, height));
        CanvasRect = ArrangedFills.RectOf(arranged, CanvasKey);

        if (CanvasRect.Width <= 0f || CanvasRect.Height <= 0f)
        {
            return;
        }

        if (state.OriginalNote is { } note)
        {
            RenderLayout(Layout.Builder.VStack(
                    Layout.Builder.Text(note, BaseFontSize, palette.DimText, TextAlign.Center, TextAlign.Center).WStar().HStar())
                .Pad(Gap * 2).Bg(palette.PanelBg), CanvasRect);
            return;
        }

        _viewer.SetSurfaceSize((uint)Renderer.Width, (uint)Renderer.Height);
        _viewer.SetContentRegion(CanvasRect);
        // The texture upload a document swap asks for happens inside Render's PrepareFrame, before anything samples the channel views.
        _viewer.Render(source, viewerState);
    }

    private Layout.Node Build(WorkbenchState state, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var steps = actions.Steps();
        var body = Layout.Builder.HStack(
                Panel(state, actions).WFixed(PanelWidth).HStar(),
                Layout.Builder.Fill(key: CanvasKey).WStar().HStar())
            .WStar().HStar();
        return Layout.Builder.VStack(
                TopBar(state, actions).WStar().HFixed(TopBarHeight),
                StepBar(state, actions, steps).WStar().HFixed(StepBarHeight),
                body)
            .Stretch().Bg(palette.ContentBg);
    }

    // The project, the image and Open project, on one row of one height and one middle.
    private Layout.Node TopBar(WorkbenchState state, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var view = state.View;
        var parts = new List<Layout.Node>();
        var folder = view?.Folder ?? "No project open";
        parts.Add(Layout.Builder.Text(folder, BaseFontSize, view is null ? palette.DimText : palette.BodyText, trim: TextTrim.Start)
            .WStar().HStar().WithTooltip(folder));
        if (view is { ImageName: { } name })
        {
            if (view.ImageNames.Length > 1)
            {
                parts.Add(ImageChoice(view, actions));
            }
            else
            {
                parts.Add(Layout.Builder.Text(name, BaseFontSize, palette.BodyText, TextAlign.Far).WAuto().HStar());
            }
        }

        parts.Add(Layout.Builder.Button("Open project", Layout.ButtonKind.Standard, actions.ChooseProject, GuiTheme.Theme,
            tooltip: "Open a workbench project folder"));
        return Layout.Builder.HStack([.. parts]).WithGap(Gap).CrossCenter().Pad(Gap, 0f).Bg(palette.HeaderBg);
    }

    private Layout.Node ImageChoice(WorkbenchView view, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var options = new Layout.ButtonGroupOption<string>[view.ImageNames.Length];
        for (var i = 0; i < options.Length; i++)
        {
            options[i] = new Layout.ButtonGroupOption<string>(view.ImageNames[i], view.ImageNames[i])
            {
                Hit = new HitResult.ButtonHit($"Image:{view.ImageNames[i]}"),
            };
        }

        var style = new Layout.ButtonGroupStyle(palette.Selection, palette.RaisedBg, palette.SelectionText, palette.BodyText,
            GuiTheme.Hover(palette.RaisedBg)) { Gap = 1f };
        return Layout.Builder.ButtonGroup<string>(options, view.ImageName, actions.ChooseImage, style, BaseFontSize).WAuto();
    }

    // Each step numbered and named by its operation's title, with its state in words and a mark from the runner's answer.
    private Layout.Node StepBar(WorkbenchState state, WorkbenchActions actions, ImmutableArray<WorkbenchStepItem> steps)
    {
        var palette = GuiTheme.Palette;
        if (steps.IsEmpty)
        {
            var message = state.View switch
            {
                null => "Open a project to see the steps of an image.",
                { Refusal: { } refusal } => refusal,
                _ => state.IsReading ? "Reading the project" : "This image has no edits yet.",
            };
            return Layout.Builder.Text(message, BaseFontSize, palette.DimText).WStar().HStar().Pad(Gap, 0f).Bg(palette.PanelBg);
        }

        var cells = new Layout.Node[steps.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i] = StepCell(state, actions, steps[i]);
        }

        // A project being asked again keeps the bar it had, and the bar says so rather than showing it as current.
        return Layout.Builder.HStack(cells).WithGap(1f).Bg(palette.Separator);
    }

    private Layout.Node StepCell(WorkbenchState state, WorkbenchActions actions, WorkbenchStepItem step)
    {
        var palette = GuiTheme.Palette;
        var selected = step.Index == state.SelectedStep;
        var fill = selected ? palette.Selection : palette.PanelBg;
        var ink = selected ? palette.SelectionText : palette.BodyText;
        var dim = selected ? palette.SelectionText : palette.DimText;
        var stateInk = step.State is StepState.Refused or StepState.Blocked ? palette.Error
            : step.State is StepState.Kept or StepState.Ran ? palette.Success
            : dim;
        if (selected)
        {
            stateInk = palette.SelectionText;
        }

        var stateRow = new List<Layout.Node>
        {
            Layout.Builder.Text(step.Mark, BaseFontSize * 0.9f, stateInk).WAuto().HStar(),
            Layout.Builder.Text(step.Words, BaseFontSize * 0.85f, stateInk).WStar().HStar(),
        };
        if (step.Changed)
        {
            stateRow.Add(Layout.Builder.Text("●", BaseFontSize * 0.7f, selected ? palette.SelectionText : palette.Warn).WAuto().HStar());
        }

        var index = step.Index;
        return Layout.Builder.VStack(
                Layout.Builder.Text($"{step.Index + 1}  {step.Title}", BaseFontSize, ink).WStar().HStar(),
                Layout.Builder.HStack([.. stateRow]).WithGap(4f).CrossCenter().WStar().HStar())
            .Pad(Gap, 4f)
            .Bg(fill)
            .BgHover(GuiTheme.Hover(fill))
            .WStar().HStar()
            .WithTooltip(step.Hover)
            .Clickable(new HitResult.ButtonHit($"Step:{index}"), _ => actions.SelectStep(index), CursorKind.Pointer);
    }

    // The selected step: what the operation does, its settings (shown, not edited), what serves it, its command line.
    private Layout.Node Panel(WorkbenchState state, WorkbenchActions actions)
    {
        var palette = GuiTheme.Palette;
        var rows = new List<Layout.Node>();
        if (actions.Panel() is not { } panel)
        {
            rows.Add(Words(state.View is { Refusal: { } refusal } ? refusal : "Select a step to see what it does.", palette.DimText));
            return Column(rows);
        }

        var step = panel.Step;
        rows.Add(Layout.Builder.Text($"{step.Index + 1}  {step.Title}", BaseFontSize * 1.15f, palette.BodyText).WStar().WithTooltip(step.Title));
        if (panel.Summary.Length > 0)
        {
            rows.Add(Words(panel.Summary, palette.BodyText));
        }

        var stateInk = step.State is StepState.Refused or StepState.Blocked ? palette.Error : palette.BodyText;
        rows.Add(Layout.Builder.HStack(
                Layout.Builder.Text(step.Mark, BaseFontSize, stateInk).WAuto(),
                Layout.Builder.Text(step.Words, BaseFontSize, stateInk).WStar())
            .WithGap(4f).CrossCenter().WStar());
        if (step.Changed)
        {
            rows.Add(Words("Changed since the last run: its settings, or a step before it, are not what a run last made it from.", palette.Warn));
        }

        if (step.Message is { Length: > 0 } message)
        {
            rows.Add(Words(message, step.State is StepState.Refused or StepState.Blocked ? palette.Error : palette.DimText));
        }

        rows.Add(Heading("Settings"));
        if (panel.SettingsRefusal is { } settingsRefusal)
        {
            rows.Add(Words(settingsRefusal, palette.Error));
        }
        else if (panel.Settings.IsEmpty)
        {
            rows.Add(Words("This step has no settings.", palette.DimText));
        }

        foreach (var setting in panel.Settings)
        {
            // Shown, never edited: a name and its value on one line, and what it does under them.
            rows.Add(Layout.Builder.HStack(
                    Layout.Builder.Text(setting.Name, BaseFontSize, palette.DimText).WAuto(),
                    Layout.Builder.Text(setting.Value, BaseFontSize, palette.BodyText, TextAlign.Far).WStar().WithTooltip(setting.Value))
                .WithGap(Gap).CrossCenter().WStar());
            if (setting.Summary.Length > 0)
            {
                rows.Add(Words(setting.Summary, palette.DimText));
            }
        }

        if (panel.ServedBy.Length > 0)
        {
            rows.Add(Heading("Served by"));
            rows.Add(Words(panel.ServedBy, palette.BodyText));
        }

        rows.Add(Heading("Command line"));
        if (panel.CommandLine.IsEmpty)
        {
            rows.Add(Words("There is no command line until the step can be planned.", palette.DimText));
        }
        else
        {
            rows.Add(CommandLineText(panel, palette.BodyText));
            rows.Add(Layout.Builder.HStack(
                    Layout.Builder.Button("Copy", Layout.ButtonKind.Standard, actions.CopyCommandLine, GuiTheme.Theme,
                        tooltip: "Put the command line on the clipboard").WAuto(),
                    Layout.Builder.Text(state.CommandCopied ? "Copied to the clipboard" : string.Empty, BaseFontSize * 0.85f, palette.DimText)
                        .WStar())
                .WithGap(Gap).CrossCenter().WStar());
        }

        return Column(rows);
    }

    private Layout.Node Column(List<Layout.Node> rows)
        => Layout.Builder.VStack([.. rows]).Pad(Gap).WithGap(Gap).Bg(GuiTheme.Palette.PanelBg).WithScroll(_panelScroll);

    private static Layout.Node Heading(string text)
        => Layout.Builder.Text(text, BaseFontSize * 0.9f, GuiTheme.Palette.Accent).WStar();

    // A sentence that wraps rather than clips (the engine's flow of words, as the viewer's panels do).
    private static Layout.Node Words(string text, RGBAColor32 color)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var nodes = new Layout.Node[words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            nodes[i] = Layout.Builder.Text(words[i], BaseFontSize, color);
        }

        return Layout.Builder.WrapH(nodes).WithGap(BaseFontSize / 3).WStar();
    }

    // The command as one flow of arguments, wrapped, never cut. An argument too long for the panel (a path) is broken after a
    // separator into pieces that each fit, so every character is on screen; Copy puts the unbroken line on the clipboard.
    private static Layout.Node CommandLineText(WorkbenchStepPanel panel, RGBAColor32 color)
    {
        var nodes = new List<Layout.Node>();
        foreach (var raw in panel.CommandLine)
        {
            var argument = StepCommandLine.Display([raw]);
            foreach (var piece in Pieces(argument))
            {
                nodes.Add(Layout.Builder.Text(piece, BaseFontSize, color).WithTooltip(argument));
            }
        }

        return Layout.Builder.WrapH([.. nodes]).WithGap(BaseFontSize / 3).WStar();
    }

    private const int LongestPiece = 24;

    // The argument in runs of at most LongestPiece characters, each ending after a path separator where there is one.
    private static IEnumerable<string> Pieces(string argument)
    {
        var start = 0;
        while (start < argument.Length)
        {
            var end = Math.Min(argument.Length, start + LongestPiece);
            if (end < argument.Length)
            {
                var cut = argument.LastIndexOfAny(['\', '/', '-', '_', '.'], end - 1, end - start);
                if (cut > start)
                {
                    end = cut + 1;
                }
            }

            yield return argument[start..end];
            start = end;
        }
    }
}
